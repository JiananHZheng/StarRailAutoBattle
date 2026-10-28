using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AxisRecognizer;
using Microsoft.Win32;
using VideoRecorder;
using IOPath = System.IO.Path;

namespace GameLauncher;

/// <summary>The video page: record a Bilibili clip (or take the user's own file), then recognize the action axis.</summary>
public partial class MainWindow
{
    private static readonly string[] VideoFileExtensions = [".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv"];

    /// <summary>15 frames per second of video, the same default as the developer blind tests (axis test run).</summary>
    private const double SampleInterval = VideoRecognition.DefaultEvery;

    private CancellationTokenSource? _videoCts;
    private string? _videoRunDir;

    private async Task RefreshAccountAsync()
    {
        if (BilibiliRecorder.RuntimeVersion() is null)
        {
            BiliAccountText.Text = "B 站登录：这台电脑没有 WebView2 运行时，无法录制链接";
            BiliLoginButton.IsEnabled = false;
            return;
        }

        try
        {
            var signedIn = await BilibiliAccount.IsSignedInAsync(RecorderPaths.DefaultBrowserDataFolder, new WindowInteropHelper(this).Handle);
            ShowAccount(signedIn);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            BiliAccountText.Text = "B 站登录：无法检查（" + ex.Message + "）";
        }
    }

    private void ShowAccount(bool signedIn)
    {
        BiliAccountText.Text = signedIn ? "B 站登录：已登录（录制可用 1080P）" : "B 站登录：未登录（录制只有 360P）";
        BiliLoginButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        BiliLogoutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void BiliLoginButton_Click(object sender, RoutedEventArgs e)
    {
        BilibiliAccount.ShowLoginWindow(RecorderPaths.DefaultBrowserDataFolder, this);
        await RefreshAccountAsync();
    }

    private async void BiliLogoutButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "退出后，录制会回到未登录的 360P。确定退出本程序里的 B 站登录吗？（不影响 Edge 浏览器里的登录）",
            "退出 B 站登录", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            await BilibiliAccount.SignOutAsync(RecorderPaths.DefaultBrowserDataFolder, new WindowInteropHelper(this).Handle);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            BiliAccountText.Text = "B 站登录：退出失败（" + ex.Message + "）";
            return;
        }

        await RefreshAccountAsync();
    }

    private void VideoLinkBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ShowFieldError(VideoLinkError, null);

    private void VideoTimeBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ShowFieldError(VideoTimeError, null);

    private void VideoFileBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ShowFieldError(VideoFileError, null);

    private void BrowseVideoFileButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择自己录好的战斗视频",
            Filter = "视频文件 (*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv)|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        SetInitialDirectory(dialog, VideoFileBox.Text);
        if (dialog.ShowDialog(this) == true)
        {
            VideoFileBox.Text = dialog.FileName;
        }
    }

    private void ClearVideoFileButton_Click(object sender, RoutedEventArgs e) => VideoFileBox.Text = string.Empty;

    private void VideoCancelButton_Click(object sender, RoutedEventArgs e) => CancelVideoRun();

    private void CancelVideoRun() => _videoCts?.Cancel();

    private async void VideoStartButton_Click(object sender, RoutedEventArgs e)
    {
        ShowFieldError(VideoLinkError, null);
        ShowFieldError(VideoTimeError, null);
        ShowFieldError(VideoFileError, null);

        var startOk = TimeText.TryParse(VideoStartBox.Text, out var start, out var startError);
        var endOk = TimeText.TryParse(VideoEndBox.Text, out var end, out var endError);
        if (!startOk || !endOk)
        {
            var message = !startOk ? "起点：" + startError : "终点：" + endError;
            ShowFieldError(VideoTimeError, message);
            SetVideoStatus(StatusKind.Error, "请先修正时间段", "没有录制，也没有识别。" + message);
            return;
        }

        if (start is { } s && end is { } en && en <= s)
        {
            ShowFieldError(VideoTimeError, "终点必须晚于起点。");
            SetVideoStatus(StatusKind.Error, "请先修正时间段", "没有录制，也没有识别。终点必须晚于起点。");
            return;
        }

        var file = VideoFileBox.Text.Trim().Trim('"');
        BilibiliLink? link = null;
        if (file.Length > 0)
        {
            var problem = !VideoFileExtensions.Contains(IOPath.GetExtension(file).ToLowerInvariant())
                ? "不支持这种文件。请选择 mp4、mkv、mov、avi、webm 或 flv 视频。"
                : !File.Exists(file) ? "找不到这个文件，请重新选择。" : null;
            if (problem is not null)
            {
                ShowFieldError(VideoFileError, problem);
                SetVideoStatus(StatusKind.Error, "请先修正自录视频文件", "没有识别。" + problem);
                return;
            }
        }
        else if (!BilibiliLinks.TryParse(VideoLinkBox.Text, out link, out var linkError))
        {
            var message = string.IsNullOrWhiteSpace(VideoLinkBox.Text) ? "请填写 B 站视频链接，或选择自录视频文件。" : linkError;
            ShowFieldError(VideoLinkError, message);
            SetVideoStatus(StatusKind.Error, "请先修正视频来源", "没有录制，也没有识别。" + message);
            return;
        }

        var dataRoot = AxisData.Resolve(AppContext.BaseDirectory);
        KnowledgeBase kb;
        string runDir;
        try
        {
            kb = KnowledgeBase.Load(AxisData.KbRoot(dataRoot));
            runDir = VideoRunFiles.CreateRunDirectory(dataRoot, link?.VideoId ?? IOPath.GetFileNameWithoutExtension(file), DateTime.Now);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            SetVideoStatus(StatusKind.Error, "找不到识别知识库", "没有录制，也没有识别。" + ex.Message);
            return;
        }

        _videoRunDir = runDir;
        EditResultButton.IsEnabled = false;
        OpenRunFolderButton.Visibility = Visibility.Visible;
        _videoCts = new CancellationTokenSource();
        var token = _videoCts.Token;
        SetVideoBusy(true);

        var info = new StringBuilder();
        info.AppendLine("# 识别运行说明（由启动器自动生成）");
        info.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        info.AppendLine($"知识库：v{kb.Version}，内容校验 {kb.ContentHash()}，样本 {kb.Samples.Count}");
        info.AppendLine($"识别工具：AxisRecognizer {ResultText.ToolVersion}；录制工具：VideoRecorder {BilibiliRecorder.ToolVersion}");
        info.AppendLine($"取帧：{VideoRecognition.SamplingText(SampleInterval)}");
        var stage = "录制";
        try
        {
            string videoPath;
            string sourceLabel;
            double recognizeStart;
            double? recognizeEnd;
            if (link is not null)
            {
                SetVideoStatus(StatusKind.Busy, "正在录制…", "正在后台打开 B 站页面（不会显示窗口）。", progress: -1);
                var recording = await BilibiliRecorder.RecordAsync(
                    new RecordingRequest(link, start ?? 0, end, runDir, RecorderPaths.DefaultBrowserDataFolder),
                    new Progress<RecordingProgress>(ShowRecordingProgress),
                    token);
                info.AppendLine($"来源：B 站链接 {link.VideoId}，请求区间 {TimeText.Format(start ?? 0)}–{(end is { } re ? TimeText.Format(re) : "视频结尾")}");
                if (!recording.Ok)
                {
                    info.AppendLine($"录制结果：失败（{recording.Failure}）{recording.Message}");
                    VideoRunFiles.WriteInfo(runDir, info.ToString());
                    _storage.Log("video", $"来源=链接 | 录制失败={recording.Failure}");
                    var cancelled = recording.Failure == RecordingFailure.Cancelled;
                    SetVideoStatus(cancelled ? StatusKind.Warning : StatusKind.Error, cancelled ? "已取消" : "录制失败", recording.Message);
                    return;
                }

                ShowAccount(recording.SignedIn);
                info.AppendLine($"录制结果：{recording.Message}{(recording.SignedIn ? "（已登录）" : "（未登录）")}详见 {BilibiliRecorder.InfoFileName}");
                videoPath = recording.VideoPath!;
                // The recording's timeline starts at the requested start, so the whole file is recognized.
                (recognizeStart, recognizeEnd) = (0, null);
                sourceLabel = $"B 站 {recording.VideoId}，视频 {TimeText.Format(recording.RecordedStart)}–{TimeText.Format(recording.RecordedEnd)}（录制文件 {BilibiliRecorder.VideoFileName}，文件时间 0 = 视频 {TimeText.Format(recording.RecordedStart)}）";
            }
            else
            {
                SetVideoStatus(StatusKind.Busy, "正在读取文件…", "正在记录文件校验值。", progress: -1);
                var (size, hash) = await Task.Run(() => (new FileInfo(file).Length, Sha256(file)), token);
                info.AppendLine($"来源：自录文件 {IOPath.GetFileName(file)}，{size} 字节，sha256={hash}");
                info.AppendLine($"区间：{TimeText.Format(start ?? 0)}–{(end is { } fe ? TimeText.Format(fe) : "文件结尾")}");
                videoPath = file;
                (recognizeStart, recognizeEnd) = (start ?? 0, end);
                sourceLabel = $"自录文件 {IOPath.GetFileName(file)}，{TimeText.Format(recognizeStart)}–{(end is { } le ? TimeText.Format(le) : "结尾")}";
            }

            stage = "识别";
            SetVideoStatus(StatusKind.Busy, "正在识别…", "正在逐帧识别行动轴（在本机运行，不调用大模型）。", progress: 0);
            var progress = new Progress<double>(f => VideoProgress.Value = f * 100);
            var result = await Task.Run(
                () => VideoRecognition.Run(kb, new Recognizer(kb), videoPath, SampleInterval, recognizeStart, recognizeEnd, sourceLabel, progress, token, runDir,
                    timeOffset: link is not null ? start ?? 0 : 0),
                token);
            var header = ResultText.Header(kb, "行动轴识别结果（工具首次生成，原始结果只读；修改请用“编辑结果”打开副本）", "视频");
            VideoRunFiles.WriteResult(runDir, result with { Text = header + Environment.NewLine + result.Text });
            info.AppendLine($"识别：{result.Frames} 帧，其中 {result.AxisFrames} 帧找到行动轴；游戏画面高 {result.ContentHeight} 像素" + (result.LowResolution ? "（低于暂定门槛，已提示）" : string.Empty));
            VideoRunFiles.WriteInfo(runDir, info.ToString());
            _storage.Log("video", $"来源={(link is null ? "自录文件" : "链接")} | 帧={result.Frames} | 有行动轴={result.AxisFrames} | 低清晰度={result.LowResolution}");

            EditResultButton.IsEnabled = true;
            var summary = result.AxisFrames == 0
                ? $"识别了 {result.Frames} 帧，但都没有找到行动轴（可能不是战斗画面，或画面布局还没有标定）。"
                : $"识别了 {result.Frames} 帧，其中 {result.AxisFrames} 帧找到行动轴。";
            SetVideoStatus(result.LowResolution ? StatusKind.Warning : StatusKind.Success, "识别完成",
                $"{summary}结果已保存为只读文件 {VideoRunFiles.ResultFile}；点“编辑结果”可打开一份可编辑的副本。",
                note: result.LowResolution ? VideoRecognition.LowResolutionNote(result.ContentHeight) : null);
        }
        catch (OperationCanceledException)
        {
            info.AppendLine($"结果：{stage}时取消");
            TryWriteInfo(runDir, info.ToString());
            SetVideoStatus(StatusKind.Warning, "已取消", $"{stage}已取消，没有生成识别结果。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            info.AppendLine($"结果：{stage}失败：{ex.Message}");
            TryWriteInfo(runDir, info.ToString());
            _storage.Log("video", $"{stage}失败");
            SetVideoStatus(StatusKind.Error, $"{stage}失败", ex.Message);
        }
        finally
        {
            SetVideoBusy(false);
            _videoCts?.Dispose();
            _videoCts = null;
        }
    }

    private void ShowRecordingProgress(RecordingProgress p)
    {
        var (text, value) = p.Stage switch
        {
            RecordingStage.Opening => ("正在打开 B 站页面…", -1.0),
            RecordingStage.Preparing => ("正在关闭弹幕、选择可用的最高清晰度…", -1.0),
            RecordingStage.Seeking => ($"正在跳转到起点 {TimeText.Format(p.Start)}…", -1.0),
            RecordingStage.Recording => ($"录制中 {TimeText.Format(p.MediaTime)} / {TimeText.Format(p.End)}（后台放慢播放以截满每秒 15 帧）",
                p.End > p.Start ? Math.Clamp((p.MediaTime - p.Start) / (p.End - p.Start), 0, 1) * 100 : -1.0),
            _ => ("正在保存录制文件…", -1.0),
        };
        SetVideoStatus(StatusKind.Busy, "正在录制…", text, value);
    }

    private void EditResultButton_Click(object sender, RoutedEventArgs e)
    {
        if (_videoRunDir is null)
        {
            return;
        }

        string path;
        try
        {
            path = VideoRunFiles.EnsureEditCopy(_videoRunDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetVideoStatus(StatusKind.Error, "无法打开结果副本", ex.Message);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // No program is associated with .txt: fall back to Notepad.
            Process.Start(new ProcessStartInfo("notepad.exe") { ArgumentList = { path }, UseShellExecute = false })?.Dispose();
        }
    }

    private void OpenRunFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_videoRunDir is not null && Directory.Exists(_videoRunDir))
        {
            OpenFolder(_videoRunDir);
        }
    }

    private void SetVideoBusy(bool busy)
    {
        VideoStartButton.IsEnabled = !busy;
        VideoStartButton.Content = busy ? "处理中…" : "启动";
        VideoCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        VideoLinkBox.IsEnabled = !busy;
        VideoStartBox.IsEnabled = !busy;
        VideoEndBox.IsEnabled = !busy;
        VideoFileBox.IsEnabled = !busy;
        BrowseVideoFileButton.IsEnabled = !busy;
        ClearVideoFileButton.IsEnabled = !busy;
        // The login window and the recorder share one browser profile; never open both at once.
        BiliLoginButton.IsEnabled = !busy;
        BiliLogoutButton.IsEnabled = !busy;
    }

    /// <param name="progress">null hides the bar, a negative value shows it as indeterminate, 0–100 is a percentage.</param>
    private void SetVideoStatus(StatusKind kind, string title, string text, double? progress = null, string? note = null)
    {
        var color = StatusColor(kind);
        VideoStatusDot.Fill = new SolidColorBrush(color);
        VideoStatusBorder.BorderBrush = new SolidColorBrush(kind == StatusKind.Idle ? Color.FromRgb(0xD9, 0xDE, 0xE7) : color);
        VideoStatusTitle.Text = title;
        VideoStatusText.Text = text;
        VideoStatusNote.Text = note ?? string.Empty;
        VideoStatusNote.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        VideoProgress.Visibility = progress is null ? Visibility.Collapsed : Visibility.Visible;
        VideoProgress.IsIndeterminate = progress is < 0;
        if (progress is >= 0)
        {
            VideoProgress.Value = progress.Value;
        }
    }

    private static void TryWriteInfo(string runDir, string info)
    {
        try
        {
            VideoRunFiles.WriteInfo(runDir, info);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
