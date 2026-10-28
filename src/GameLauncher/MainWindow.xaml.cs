using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameLauncher.Core;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace GameLauncher;

public partial class MainWindow : Window
{
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WindowWaitTimeout = TimeSpan.FromSeconds(90);

    private readonly AppStorage _storage = new();
    private readonly DispatcherTimer _entryTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _gameExeTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly List<string> _diagnostics = [];
    private int _entryVersion;
    private int _gameExeVersion;
    private CancellationTokenSource? _waitCts;
    private IReadOnlyList<WindowCandidate> _switchCandidates = [];
    private string _waitingLaunchLine = string.Empty;

    private enum StatusKind
    {
        Idle,
        Busy,
        Success,
        Warning,
        Error,
    }

    public MainWindow()
    {
        InitializeComponent();
        _entryTimer.Tick += async (_, _) =>
        {
            _entryTimer.Stop();
            await RefreshEntryPreviewAsync();
        };
        _gameExeTimer.Tick += async (_, _) =>
        {
            _gameExeTimer.Stop();
            await RefreshGameExePreviewAsync();
        };

        var settings = _storage.LoadSettings();
        EntryBox.Text = settings.EntryPath;
        GameExeBox.Text = settings.GameExePath;
        ModeIdentify.IsChecked = settings.Mode == LaunchMode.IdentifyRunning;
        ModeStart.IsChecked = settings.Mode != LaunchMode.IdentifyRunning;
        // First, so the user can still cancel closing over an unsaved sequence before anything else runs.
        InitSequencePage();
        Closing += (_, e) =>
        {
            if (e.Cancel)
            {
                return;
            }

            SaveSettings();
            CancelVideoRun();
        };
        Loaded += async (_, _) => await RefreshAccountAsync();
    }

    private LaunchMode SelectedMode => ModeIdentify.IsChecked == true ? LaunchMode.IdentifyRunning : LaunchMode.StartGame;

    private static bool IsSelfElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static Color StatusColor(StatusKind kind) => kind switch
    {
        StatusKind.Busy => Color.FromRgb(0x2F, 0x6B, 0xDB),
        StatusKind.Success => Color.FromRgb(0x1E, 0x8E, 0x3E),
        StatusKind.Warning => Color.FromRgb(0xB2, 0x6A, 0x00),
        StatusKind.Error => Color.FromRgb(0xC4, 0x2B, 0x1C),
        _ => Color.FromRgb(0x9A, 0xA5, 0xB5),
    };

    // ---------- Input handling ----------

    private void EntryBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _entryTimer.Stop();
        _entryTimer.Start();
    }

    private void GameExeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _gameExeTimer.Stop();
        _gameExeTimer.Start();
    }

    private void BrowseEntryButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择游戏程序、官方启动器或它们的快捷方式",
            Filter = "程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*",
            // Keep the .lnk itself; by default the dialog would silently return the shortcut's target instead.
            DereferenceLinks = false,
            CheckFileExists = true,
        };
        SetInitialDirectory(dialog, EntryBox.Text);
        if (dialog.ShowDialog(this) == true)
        {
            EntryBox.Text = dialog.FileName;
        }
    }

    private void BrowseGameButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择用于识别窗口的游戏本体程序",
            Filter = "程序 (*.exe)|*.exe|程序或快捷方式 (*.exe;*.lnk)|*.exe;*.lnk",
            DereferenceLinks = false,
            CheckFileExists = true,
        };
        SetInitialDirectory(dialog, GameExeBox.Text.Length > 0 ? GameExeBox.Text : EntryBox.Text);
        if (dialog.ShowDialog(this) == true)
        {
            GameExeBox.Text = dialog.FileName;
        }
    }

    private static void SetInitialDirectory(OpenFileDialog dialog, string currentPath)
    {
        try
        {
            var directory = IOPath.GetDirectoryName(currentPath.Trim().Trim('"'));
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private async Task<InspectResult?> RefreshEntryPreviewAsync()
    {
        var text = EntryBox.Text;
        var version = ++_entryVersion;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowFieldError(EntryError, null);
            EntryInfoPanel.Visibility = Visibility.Collapsed;
            return null;
        }

        var result = await Task.Run(() => EntryInspector.Inspect(text));
        if (version != _entryVersion)
        {
            return result;
        }

        if (!result.Ok)
        {
            ShowFieldError(EntryError, result.Error!.Message);
            EntryInfoPanel.Visibility = Visibility.Collapsed;
            return result;
        }

        ShowFieldError(EntryError, null);
        var entry = result.Entry!;
        EntryTypeText.Text = "实际类型：" + entry.KindDisplay + HiddenExtensionHint(entry);
        EntryDetailsText.Text = string.Join("\n", DescribeEntry(entry));
        var warnings = EntryWarnings(entry).ToList();
        EntryWarningText.Text = string.Join("\n", warnings);
        EntryWarningText.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        EntryInfoPanel.Visibility = Visibility.Visible;
        return result;
    }

    private async Task<InspectResult?> RefreshGameExePreviewAsync()
    {
        var text = GameExeBox.Text;
        var version = ++_gameExeVersion;
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowFieldError(GameExeError, null);
            return null;
        }

        var result = await Task.Run(() => EntryInspector.Inspect(text));
        if (version == _gameExeVersion)
        {
            var error = result.Error?.Message
                ?? (result.Entry!.ResolvedExecutable is null ? "无法确定这个快捷方式指向的 .exe，请直接选择游戏本体的 .exe。" : null);
            ShowFieldError(GameExeError, error);
        }

        return result;
    }

    private static string HiddenExtensionHint(EntryInfo entry) =>
        entry.Kind == EntryKind.Shortcut && IOPath.GetFileNameWithoutExtension(entry.Path).EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? "——名称里的“.exe”只是显示名的一部分，真实扩展名是 .lnk"
            : string.Empty;

    private static IEnumerable<string> DescribeEntry(EntryInfo entry)
    {
        if (entry.Shortcut is { } shortcut)
        {
            yield return "目标：" + (shortcut.TargetPath ?? "（没有普通文件目标，交给 Windows 处理）");
            yield return "参数：" + (shortcut.Arguments.Length > 0 ? shortcut.Arguments : "（无）");
            yield return "起始位置：" + (shortcut.WorkingDirectory.Length > 0 ? shortcut.WorkingDirectory : "（未设置）");
        }

        if (entry.ResolvedExecutable is { } exe)
        {
            var product = exe.ProductName ?? exe.FileDescription;
            yield return $"程序：{exe.FileName} · {ArchitectureText(exe.Architecture)}" + (product is null ? string.Empty : $" · {product}");
            yield return "权限要求：" + ExecutionLevelText(exe.ExecutionLevel);
        }
    }

    private static IEnumerable<string> EntryWarnings(EntryInfo entry)
    {
        var level = entry.ResolvedExecutable?.ExecutionLevel;
        if (level is ExecutionLevel.RequireAdministrator or ExecutionLevel.HighestAvailable)
        {
            yield return "该程序要求管理员权限：“启动游戏”时 Windows 会弹出用户账户控制（UAC）确认，是否允许由你决定，本应用不会自行提权。" +
                         "若游戏必须经官方启动器启动，直接打开游戏本体可能进不了游戏，请改选官方启动器，或先用官方启动器启动再使用“只识别已运行游戏”。";
        }

        foreach (var note in entry.Notes)
        {
            yield return note;
        }
    }

    private static string ArchitectureText(PeArchitecture architecture) => architecture switch
    {
        PeArchitecture.X64 => "64 位",
        PeArchitecture.X86 => "32 位",
        PeArchitecture.Arm64 => "ARM64",
        _ => "未知架构",
    };

    private static string ExecutionLevelText(ExecutionLevel level) => level switch
    {
        ExecutionLevel.AsInvoker => "普通权限（asInvoker）",
        ExecutionLevel.NotSpecified => "未声明（按普通权限运行）",
        ExecutionLevel.HighestAvailable => "尽可能高的权限（highestAvailable）",
        ExecutionLevel.RequireAdministrator => "需要管理员权限（requireAdministrator）",
        _ => "无法读取程序清单",
    };

    private static void ShowFieldError(System.Windows.Controls.TextBlock target, string? message)
    {
        target.Text = message ?? string.Empty;
        target.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveSettings() => _storage.SaveSettings(new UserSettings
    {
        EntryPath = EntryBox.Text.Trim(),
        GameExePath = GameExeBox.Text.Trim(),
        Mode = SelectedMode,
    });

    // ---------- Launch ----------

    private async void LaunchButton_Click(object sender, RoutedEventArgs e)
    {
        CancelWait();
        HideActivate();
        _diagnostics.Clear();
        AddDiagnostic("时间", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        AddDiagnostic("本应用权限", IsSelfElevated ? "管理员" : "普通用户（未提权）");
        AddDiagnostic("模式", SelectedMode == LaunchMode.StartGame ? "启动游戏" : "只识别已运行游戏");

        SaveSettings();
        SetBusy(true);
        try
        {
            if (SelectedMode == LaunchMode.StartGame)
            {
                await StartGameAsync();
            }
            else
            {
                await IdentifyRunningAsync();
            }
        }
        finally
        {
            SetBusy(false);
            FlushDiagnostics();
        }
    }

    private async Task StartGameAsync()
    {
        _entryTimer.Stop();
        var inspected = await RefreshEntryPreviewAsync();
        if (inspected?.Entry is not { } entry)
        {
            var message = inspected?.Error?.Message ?? "请填写游戏入口路径，或点“浏览文件”选择。";
            ShowFieldError(EntryError, message);
            SetStatus(StatusKind.Error, "请先修正游戏入口", "没有启动程序。" + message);
            AddDiagnostic("入口检查", inspected?.Error?.Kind.ToString() ?? "Empty");
            return;
        }

        var watchTarget = await ResolveGameExeAsync(entry);
        if (watchTarget.Error is not null)
        {
            SetStatus(StatusKind.Error, "请先修正“识别窗口用的游戏程序”", "没有启动程序。" + watchTarget.Error);
            return;
        }

        DescribeEntryForDiagnostics(entry);
        SetStatus(StatusKind.Busy, "正在处理…", "游戏：正在请求 Windows 打开所选入口。如出现用户账户控制（UAC）提示，请自行决定是否允许。");

        var outcome = await ShellLauncher.LaunchAsync(entry, new WindowInteropHelper(this).Handle, EarlyExitWindow);
        AddDiagnostic("启动调用", outcome.Method);
        if (!outcome.Accepted)
        {
            var failure = outcome.Failure!;
            AddDiagnostic("结果", $"失败：{failure.Category}");
            AddDiagnostic("Win32 错误码", failure.Win32Error.ToString());
            AddDiagnostic("系统消息", failure.SystemMessage);
            var title = failure.Category == LaunchFailureCategory.UserCancelledElevation ? "已取消启动" : "游戏启动失败";
            var kind = failure.Category == LaunchFailureCategory.UserCancelledElevation ? StatusKind.Warning : StatusKind.Error;
            SetStatus(kind, title, "游戏：" + failure.Message);
            return;
        }

        AddDiagnostic("结果", "Windows 已受理启动请求");
        AddDiagnostic("进程 ID", outcome.ProcessId?.ToString() ?? "（Windows 未返回新进程，可能交给了已运行的程序）");
        var pidText = outcome.ProcessId is { } pid ? $"（进程 ID {pid}）" : string.Empty;
        var launchedName = entry.ResolvedExecutable?.FileName ?? entry.FileName;

        if (outcome.ExitedEarly)
        {
            AddDiagnostic("提前退出", $"{EarlyExitWindow.TotalSeconds:0} 秒内退出，退出码 {outcome.ExitCode}");
            var launchedIsWatchTarget = watchTarget.Path is not null && entry.ResolvedExecutable is { } exe && WindowMatcher.PathsEqual(exe.Path, watchTarget.Path);
            var hint = launchedIsWatchTarget
                ? "该游戏可能要求通过官方启动器启动。请改选官方启动器作为入口，或先用官方启动器启动游戏，再使用“只识别已运行游戏”模式。"
                : "如果它是启动器，可能已把任务交给已运行的启动器窗口，请查看任务栏。";
            SetStatus(StatusKind.Warning, "程序启动后很快退出了",
                $"游戏：Windows 已启动 {launchedName}{pidText}，但它在 {EarlyExitWindow.TotalSeconds:0} 秒内退出（退出码 {outcome.ExitCode}）。{hint}");
            if (launchedIsWatchTarget)
            {
                return;
            }
        }
        else
        {
            SetStatus(StatusKind.Success, "已启动", $"游戏：Windows 已启动 {launchedName}{pidText}。");
        }

        if (watchTarget.Path is null)
        {
            StatusGame.Text += " 无法确定游戏本体程序，因此不检测游戏窗口；如需自动切换窗口，请填写“识别窗口用的游戏程序”。";
            return;
        }

        _waitCts = new CancellationTokenSource();
        await WatchForWindowAsync(watchTarget.Path, StatusGame.Text, _waitCts);
    }

    private async Task WatchForWindowAsync(string exePath, string launchLine, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var name = IOPath.GetFileName(exePath);
        _waitingLaunchLine = launchLine;
        var deadline = DateTime.UtcNow + WindowWaitTimeout;
        MatchResult? last = null;
        CancelWaitButton.Visibility = Visibility.Visible;
        SetBusy(false);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                SetStatus(StatusKind.Busy, "已启动，正在等待游戏窗口…",
                    $"{launchLine}\n正在等待 {name} 的窗口出现（最多 {WindowWaitTimeout.TotalSeconds:0} 秒）。");
                last = await Task.Run(() => WindowService.FindGameWindow(exePath), token);
                token.ThrowIfCancellationRequested();
                if (last.Status == MatchStatus.Found)
                {
                    DescribeMatchForDiagnostics(last);
                    await ActivateAndReportAsync(last.Best!, launchLine + "\n");
                    return;
                }

                if (last.Status == MatchStatus.NeedsConfirmation)
                {
                    DescribeMatchForDiagnostics(last);
                    ShowActivate(last.Candidates, "确认并切换到游戏窗口");
                    SetStatus(StatusKind.Warning, "已启动，需要你确认游戏窗口",
                        $"{launchLine}\n检测到 {name} 的窗口，但 Windows 不允许本应用读取该进程的程序位置，无法自动确认它就是你选择的程序。确认无误后请点“确认并切换到游戏窗口”。");
                    return;
                }

                await Task.Delay(1000, token);
            }

            if (last is not null)
            {
                DescribeMatchForDiagnostics(last);
            }

            var reason = last?.Status switch
            {
                MatchStatus.RunningNoWindow => $"检测到 {name} 进程，但 {WindowWaitTimeout.TotalSeconds:0} 秒内没有出现可切换的窗口。游戏可能仍在加载，稍后可用“只识别已运行游戏”模式再试。",
                MatchStatus.OnlyOtherProgramRunning => $"检测到同名进程 {name}，但它的程序位置与“识别窗口用的游戏程序”不同，因此没有切换。",
                _ => $"{WindowWaitTimeout.TotalSeconds:0} 秒内没有检测到 {name} 运行。如果入口是官方启动器，请在启动器里点击“开始游戏”；游戏窗口出现后，可切换到“只识别已运行游戏”模式再点“启动”。",
            };
            SetStatus(StatusKind.Warning, "已启动，但未检测到游戏窗口", $"{launchLine}\n{reason}");
        }
        catch (OperationCanceledException)
        {
            // Whoever cancelled (stop button, a new launch, manual switch) has already updated the status.
        }
        finally
        {
            if (_waitCts is null || _waitCts == cts)
            {
                CancelWaitButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task IdentifyRunningAsync()
    {
        EntryInfo? entry = null;
        if (!string.IsNullOrWhiteSpace(EntryBox.Text))
        {
            _entryTimer.Stop();
            var inspected = await RefreshEntryPreviewAsync();
            entry = inspected?.Entry;
            if (entry is null && string.IsNullOrWhiteSpace(GameExeBox.Text))
            {
                SetStatus(StatusKind.Error, "请先修正游戏入口", inspected?.Error?.Message ?? "游戏入口无效。");
                return;
            }
        }

        var target = await ResolveGameExeAsync(entry);
        if (target.Error is not null || target.Path is null)
        {
            var message = target.Error ?? (entry is null
                ? "请填写游戏入口，或在“识别窗口用的游戏程序”中选择游戏本体 .exe。"
                : "无法确定入口指向的程序。请在“识别窗口用的游戏程序”中选择游戏本体 .exe。");
            SetStatus(StatusKind.Error, "无法确定要识别的游戏", message);
            return;
        }

        if (entry is not null)
        {
            DescribeEntryForDiagnostics(entry);
        }

        var name = IOPath.GetFileName(target.Path);
        AddDiagnostic("识别目标", name);
        SetStatus(StatusKind.Busy, "正在查找已运行的游戏…", $"游戏：正在查找 {name} 的窗口（不会启动任何程序）。");

        var match = await Task.Run(() => WindowService.FindGameWindow(target.Path));
        DescribeMatchForDiagnostics(match);
        switch (match.Status)
        {
            case MatchStatus.NotRunning:
                SetStatus(StatusKind.Error, "未检测到已运行的游戏",
                    $"游戏：未检测到已运行的游戏（{name}）。请先通过官方启动器启动，再返回本应用重试。");
                break;
            case MatchStatus.OnlyOtherProgramRunning:
                SetStatus(StatusKind.Error, "运行中的程序不是所选游戏",
                    $"游戏：检测到同名进程 {name}，但它的程序位置与你选择的不同，因此没有切换。请确认所选的是正在运行的那份游戏程序。");
                break;
            case MatchStatus.RunningNoWindow:
                SetStatus(StatusKind.Warning, "游戏还没有可切换的窗口",
                    $"游戏：检测到 {name} 正在运行（进程 ID {string.Join("、", match.NameMatches.Select(p => p.Id))}），但还没有可切换的窗口，可能仍在加载。请稍后再点“启动”。");
                break;
            case MatchStatus.NeedsConfirmation:
                var picked = PickCandidate(match.Candidates, name);
                if (picked is null)
                {
                    SetStatus(StatusKind.Warning, "未切换窗口",
                        $"游戏：检测到 {name} 的窗口，但无法确认它就是所选游戏，你选择了不切换。可以手动点击任务栏中的游戏窗口。");
                    ShowActivate(match.Candidates, "确认并切换到游戏窗口");
                    break;
                }

                await ActivateAndReportAsync(picked, string.Empty);
                break;
            case MatchStatus.Found:
                await ActivateAndReportAsync(match.Best!, string.Empty);
                break;
        }
    }

    private async Task<(string? Path, string? Error)> ResolveGameExeAsync(EntryInfo? entry)
    {
        if (string.IsNullOrWhiteSpace(GameExeBox.Text))
        {
            return (entry?.ResolvedExecutable?.Path, null);
        }

        _gameExeTimer.Stop();
        var result = await RefreshGameExePreviewAsync();
        if (result?.Entry?.ResolvedExecutable is { } exe)
        {
            return (exe.Path, null);
        }

        return (null, GameExeError.Text.Length > 0 ? GameExeError.Text : "“识别窗口用的游戏程序”无效。");
    }

    // ---------- Window switching ----------

    private async Task ActivateAndReportAsync(WindowCandidate candidate, string prefix)
    {
        var title = candidate.Window.Title.Length > 0 ? $"“{candidate.Window.Title}”" : "（无标题）";
        var name = candidate.Process.Name + ".exe";
        var result = await WindowService.ActivateAsync(candidate.Window.Handle);
        AddDiagnostic("置前结果", $"窗口已关闭={result.WindowGone}，原为最小化={result.WasMinimized}，仍最小化={result.StillMinimized}，已确认在前台={result.ForegroundConfirmed}");

        if (result.WindowGone)
        {
            HideActivate();
            SetStatus(StatusKind.Warning, "游戏窗口已关闭", $"{prefix}游戏：{name} 的窗口已关闭或已变化，请重新点击“启动”。");
        }
        else if (result.ForegroundConfirmed)
        {
            HideActivate();
            SetStatus(StatusKind.Success, "已切换到游戏窗口",
                $"{prefix}游戏：{name}（进程 ID {candidate.Process.Id}）的窗口 {title} 已{(result.WasMinimized ? "还原并" : string.Empty)}切换到前台。");
        }
        else
        {
            ShowActivate([candidate with { PathVerified = true }], "切换到游戏窗口");
            var detail = result.StillMinimized
                ? "Windows 没有允许还原它（若游戏以管理员身份运行，普通权限的程序可能无法操作它的窗口）。"
                : "Windows 没有允许把它切到前台（系统会限制程序抢占前台）。";
            SetStatus(StatusKind.Warning, "已找到游戏窗口，但未能切到前台",
                $"{prefix}游戏：已找到 {name}（进程 ID {candidate.Process.Id}）的窗口 {title}，但{detail}请手动点击任务栏中的游戏图标，或点“切换到游戏窗口”再试。");
        }
    }

    private async void ActivateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchCandidates.Count == 0)
        {
            return;
        }

        var candidate = _switchCandidates[0].PathVerified
            ? _switchCandidates[0]
            : PickCandidate(_switchCandidates, _switchCandidates[0].Process.Name + ".exe");
        if (candidate is null)
        {
            return;
        }

        CancelWait();
        await ActivateAndReportAsync(candidate, string.Empty);
        FlushDiagnostics();
    }

    private WindowCandidate? PickCandidate(IReadOnlyList<WindowCandidate> candidates, string exeName)
    {
        var errors = candidates.Select(c => c.Process.PathError).Where(code => code is not null).Distinct().ToList();
        var explanation =
            $"找到以下名为 {exeName} 的进程窗口，但 Windows 不允许本应用读取这些进程的程序位置" +
            (errors.Count > 0 ? $"（错误码 {string.Join("、", errors)}）" : string.Empty) +
            "，因此无法自动确认它们就是你选择的游戏。请只在确认是游戏窗口时选择并切换。";
        var dialog = new CandidatePickerWindow(explanation, candidates) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }

    private void ShowActivate(IReadOnlyList<WindowCandidate> candidates, string text)
    {
        _switchCandidates = candidates;
        ActivateButton.Content = text;
        ActivateButton.Visibility = Visibility.Visible;
    }

    private void HideActivate()
    {
        _switchCandidates = [];
        ActivateButton.Visibility = Visibility.Collapsed;
    }

    private void CancelWaitButton_Click(object sender, RoutedEventArgs e)
    {
        CancelWait();
        CancelWaitButton.Visibility = Visibility.Collapsed;
        SetStatus(StatusKind.Idle, "已停止等待游戏窗口",
            _waitingLaunchLine + "\n已停止检测游戏窗口。游戏窗口出现后，可用“只识别已运行游戏”模式切换。");
    }

    private void CancelWait()
    {
        _waitCts?.Cancel();
        _waitCts?.Dispose();
        _waitCts = null;
    }

    // ---------- Status & diagnostics ----------

    private void SetBusy(bool busy)
    {
        LaunchButton.IsEnabled = !busy;
        BrowseEntryButton.IsEnabled = !busy;
        BrowseGameButton.IsEnabled = !busy;
        LaunchButton.Content = busy ? "处理中…" : "启动";
    }

    private void SetStatus(StatusKind kind, string title, string gameLine)
    {
        var color = StatusColor(kind);
        StatusDot.Fill = new SolidColorBrush(color);
        StatusBorder.BorderBrush = new SolidColorBrush(kind == StatusKind.Idle ? Color.FromRgb(0xD9, 0xDE, 0xE7) : color);
        StatusTitle.Text = title;
        StatusGame.Text = gameLine;
        UpdateDiagnosticsBox();
    }

    private void DescribeEntryForDiagnostics(EntryInfo entry)
    {
        AddDiagnostic("入口类型", entry.KindDisplay);
        AddDiagnostic("入口文件名", entry.FileName);
        if (entry.Shortcut is { } shortcut)
        {
            AddDiagnostic("快捷方式目标", shortcut.TargetPath is null ? "（无普通文件目标）" : IOPath.GetFileName(shortcut.TargetPath));
            AddDiagnostic("快捷方式参数", shortcut.Arguments.Length > 0 ? $"已设置（{shortcut.Arguments.Length} 个字符，内容省略）" : "无");
            var workingDirectory = shortcut.WorkingDirectory.Length == 0
                ? "未设置"
                : shortcut.TargetPath is not null && WindowMatcher.PathsEqual(shortcut.WorkingDirectory, IOPath.GetDirectoryName(shortcut.TargetPath)!)
                    ? "与目标程序同一文件夹"
                    : "其他文件夹";
            AddDiagnostic("起始位置", workingDirectory);
        }

        if (entry.ResolvedExecutable is { } exe)
        {
            AddDiagnostic("程序清单权限", exe.ExecutionLevel.ToString());
            AddDiagnostic("程序架构", exe.Architecture.ToString());
        }
    }

    private void DescribeMatchForDiagnostics(MatchResult match)
    {
        AddDiagnostic("识别状态", match.Status.ToString());
        AddDiagnostic("同名进程", match.NameMatches.Count == 0
            ? "无"
            : string.Join("；", match.NameMatches.Select(p => $"PID {p.Id}：" + (p.ImagePath is null ? $"路径不可读（错误码 {p.PathError}）" : "路径可读"))));
        AddDiagnostic("候选窗口数", match.Candidates.Count.ToString());
    }

    private void AddDiagnostic(string key, string value)
    {
        _diagnostics.Add($"{key}：{value}");
        UpdateDiagnosticsBox();
    }

    private void UpdateDiagnosticsBox()
    {
        DiagnosticsBox.Text = string.Join(Environment.NewLine, _diagnostics);
        DiagnosticsExpander.Visibility = _diagnostics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FlushDiagnostics()
    {
        if (_diagnostics.Count > 0)
        {
            _storage.Log("launch", string.Join(" | ", _diagnostics) + " | 状态：" + StatusTitle.Text);
        }
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = IOPath.GetDirectoryName(_storage.LogPath)!;
        Directory.CreateDirectory(folder);
        OpenFolder(folder);
    }

    private static void OpenFolder(string folder) =>
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
}
