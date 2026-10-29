using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AxisRecognizer;

namespace GameLauncher;

/// <summary>The 凹分序列 page: write and check the expected sequence of one battle, saved for the auto-play code.</summary>
public partial class MainWindow
{
    private const string NewSequenceItem = "（新序列）";

    private readonly DispatcherTimer _sequenceCheckTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private Func<string, UnitLookup>? _sequenceLookup;
    private string? _sequenceLookupNote;
    private bool _sequenceLookupLoaded;

    /// <summary>Name and text as last loaded or saved; null name = a new sequence not saved yet.</summary>
    private string? _sequenceLoadedName;
    private string _sequenceSavedText = string.Empty;
    private bool _sequencePickerBusy;

    private static string SequenceDataRoot => AxisData.Resolve(AppContext.BaseDirectory);

    private bool SequenceDirty => SequenceEditor.Text != _sequenceSavedText;

    private void InitSequencePage()
    {
        _sequenceCheckTimer.Tick += (_, _) =>
        {
            _sequenceCheckTimer.Stop();
            CheckSequence();
        };
        Closing += SequencePage_Closing;
        RefreshSequencePicker(select: null);
        CheckSequence();
    }

    private void SequencePage_Closing(object? sender, CancelEventArgs e)
    {
        if (!e.Cancel && !ConfirmDiscardSequence("关闭程序"))
        {
            e.Cancel = true;
        }
    }

    // ---------- Picking and loading ----------

    private void RefreshSequencePicker(string? select)
    {
        IReadOnlyList<string> names;
        try
        {
            names = SequenceFiles.List(SequenceDataRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            names = [];
            SequenceSaveText.Text = "读取已保存的序列失败：" + ex.Message;
        }

        _sequencePickerBusy = true;
        SequencePicker.Items.Clear();
        SequencePicker.Items.Add(NewSequenceItem);
        foreach (var name in names)
        {
            SequencePicker.Items.Add(name);
        }

        SequencePicker.SelectedItem = select is not null && names.Contains(select) ? select : NewSequenceItem;
        _sequencePickerBusy = false;
    }

    private void SequencePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sequencePickerBusy || SequencePicker.SelectedItem is not string picked)
        {
            return;
        }

        var name = picked == NewSequenceItem ? null : picked;
        if (name == _sequenceLoadedName)
        {
            return;
        }

        if (!ConfirmDiscardSequence(name is null ? "新建序列" : $"打开“{name}”"))
        {
            // Put the picker back on the sequence still being edited.
            _sequencePickerBusy = true;
            SequencePicker.SelectedItem = _sequenceLoadedName ?? NewSequenceItem;
            _sequencePickerBusy = false;
            return;
        }

        LoadSequence(name);
    }

    private void LoadSequence(string? name)
    {
        var text = string.Empty;
        if (name is not null)
        {
            try
            {
                text = SequenceFiles.Read(SequenceDataRoot, name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SequenceSaveText.Text = $"打开“{name}”失败：{ex.Message}";
                RefreshSequencePicker(_sequenceLoadedName);
                return;
            }
        }

        _sequenceLoadedName = name;
        _sequenceSavedText = text;
        SequenceNameBox.Text = name ?? string.Empty;
        SequenceEditor.Text = text;
        SequenceSaveText.Text = name is null ? "新序列，还没有保存。" : $"已打开“{name}”。";
        UpdateSequenceDirty();
        CheckSequence();
    }

    /// <summary>True when there are no unsaved changes, or the user chose to save or drop them.</summary>
    private bool ConfirmDiscardSequence(string action)
    {
        if (!SequenceDirty)
        {
            return true;
        }

        var answer = MessageBox.Show(this, $"凹分序列有还没保存的修改。{action}之前要先保存吗？\n\n是：保存后继续\n否：丢掉修改后继续\n取消：回去继续编辑",
            "凹分序列未保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => SaveSequence(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // ---------- Editing and checking ----------

    private void SequenceEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSequenceDirty();
        _sequenceCheckTimer.Stop();
        _sequenceCheckTimer.Start();
    }

    // R47: typed text gets a space after the index; doubled spaces and unused symbols are not added.
    private void SequenceEditor_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.TextComposition is FrameworkTextComposition ime)
        {
            // An IME commit is already in the text when this is raised, so fix it up afterwards.
            var offset = ime.ResultOffset;
            var committed = e.Text;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => FixImeCommit(offset, committed));
            return;
        }

        var text = SequenceTyping.Filter(SequenceEditor.Text, SequenceEditor.SelectionStart, SequenceEditor.SelectionLength, e.Text);
        if (text == e.Text)
        {
            return;
        }

        e.Handled = true;
        if (text.Length > 0)
        {
            var start = SequenceEditor.SelectionStart;
            SequenceEditor.SelectedText = text;
            SequenceEditor.Select(start + text.Length, 0);
        }
    }

    private void FixImeCommit(int offset, string committed)
    {
        var current = SequenceEditor.Text;
        if (offset < 0 || offset + committed.Length > current.Length
            || string.CompareOrdinal(current, offset, committed, 0, committed.Length) != 0)
        {
            return;
        }

        var without = current.Remove(offset, committed.Length);
        var text = SequenceTyping.Filter(without, offset, 0, committed);
        if (text != committed)
        {
            SequenceEditor.Select(offset, committed.Length);
            SequenceEditor.SelectedText = text;
            SequenceEditor.Select(offset + text.Length, 0);
        }
    }

    // The space key does not raise PreviewTextInput in a TextBox.
    private void SequenceEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift
            && SequenceTyping.Filter(SequenceEditor.Text, SequenceEditor.SelectionStart, SequenceEditor.SelectionLength, " ").Length == 0)
        {
            e.Handled = true;
        }
    }

    private void SequenceNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ShowFieldError(SequenceNameError, null);
        UpdateSequenceDirty();
    }

    private void SequenceEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        var line = SequenceEditor.GetLineIndexFromCharacterIndex(SequenceEditor.CaretIndex);
        SequenceCaretText.Text = line >= 0 ? $"第 {line + 1} 行" : string.Empty;
    }

    private void UpdateSequenceDirty()
    {
        SequenceReloadButton.IsEnabled = SequenceDirty;
        var name = SequenceNameBox.Text.Trim();
        SequenceSaveButton.Content = _sequenceLoadedName is not null && name.Length > 0 && name != _sequenceLoadedName ? "另存为" : "保存";
    }

    private Func<string, UnitLookup>? SequenceLookup()
    {
        if (_sequenceLookupLoaded)
        {
            return _sequenceLookup;
        }

        _sequenceLookupLoaded = true;
        try
        {
            var kb = KnowledgeBase.Load(AxisData.KbRoot(SequenceDataRoot));
            _sequenceLookup = ExpectedSequence.KbLookup(kb);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            _sequenceLookupNote = "找不到识别知识库，单位名称没有检查。";
        }

        return _sequenceLookup;
    }

    private ExpectedSequence? CheckSequence()
    {
        _sequenceCheckTimer.Stop();
        SequenceIssueList.Items.Clear();
        if (string.IsNullOrWhiteSpace(SequenceEditor.Text))
        {
            SetSequenceCheck(StatusKind.Idle, "还没有内容", "在上面逐行写预期序列，写法见下方“写法说明”。");
            return null;
        }

        var sequence = ExpectedSequence.Parse(SequenceEditor.Text, SequenceLookup());
        var actions = sequence.Actions.ToList();
        var markers = sequence.Steps.Count - actions.Count;
        var checks = sequence.Steps.Count(s => s.Check is not null);
        var errors = sequence.Issues.Count(i => i.Level == IssueLevel.Error);
        var warnings = sequence.Issues.Count - errors;
        var counts = $"共 {actions.Count} 个行动（主轴 {actions.Count(a => a.Kind == StepKind.Main)}、额外 {actions.Count(a => a.Kind == StepKind.Extra)}），"
            + $"换波或转阶段 {markers} 次，检定 {checks} 处。";
        if (_sequenceLookupNote is not null)
        {
            counts += _sequenceLookupNote;
        }

        if (errors > 0)
        {
            SetSequenceCheck(StatusKind.Error, $"有 {errors} 处错误" + (warnings > 0 ? $"、{warnings} 条提醒" : string.Empty),
                "改好错误之前，这份序列不能用于检定。" + counts);
        }
        else if (warnings > 0)
        {
            SetSequenceCheck(StatusKind.Warning, $"格式正确，有 {warnings} 条提醒", counts);
        }
        else
        {
            SetSequenceCheck(StatusKind.Success, "格式正确", counts);
        }

        foreach (var issue in sequence.Issues.OrderBy(i => i.Line).ThenBy(i => i.Level))
        {
            SequenceIssueList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock { Text = issue.ToString(), Foreground = new SolidColorBrush(StatusColor(issue.Level == IssueLevel.Error ? StatusKind.Error : StatusKind.Warning)) },
                Tag = issue.Line,
                Cursor = System.Windows.Input.Cursors.Hand,
            });
        }

        SequenceIssueList.Visibility = SequenceIssueList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return sequence;
    }

    private void SetSequenceCheck(StatusKind kind, string title, string text)
    {
        var color = StatusColor(kind);
        SequenceCheckDot.Fill = new SolidColorBrush(color);
        SequenceCheckBorder.BorderBrush = new SolidColorBrush(kind == StatusKind.Idle ? Color.FromRgb(0xD9, 0xDE, 0xE7) : color);
        SequenceCheckTitle.Text = title;
        SequenceCheckText.Text = text;
    }

    private void SequenceIssueList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SequenceIssueList.SelectedItem is not ListBoxItem { Tag: int line })
        {
            return;
        }

        var index = line - 1;
        if (index >= 0 && index < SequenceEditor.LineCount)
        {
            var start = SequenceEditor.GetCharacterIndexFromLineIndex(index);
            SequenceEditor.Focus();
            SequenceEditor.Select(start, SequenceEditor.GetLineLength(index));
            SequenceEditor.ScrollToLine(index);
        }

        // Let the same issue be clicked again.
        SequenceIssueList.SelectedItem = null;
    }

    // ---------- Saving ----------

    private void SequenceSaveButton_Click(object sender, RoutedEventArgs e) => SaveSequence();

    private bool SaveSequence()
    {
        var name = SequenceNameBox.Text.Trim();
        if (SequenceFiles.NameError(name) is { } nameError)
        {
            ShowFieldError(SequenceNameError, nameError);
            SequenceNameBox.Focus();
            return false;
        }

        var sequence = CheckSequence();
        var errors = sequence?.Issues.Count(i => i.Level == IssueLevel.Error) ?? 0;
        if (sequence is null || errors > 0)
        {
            var reason = sequence is null ? "序列还是空的" : $"序列还有 {errors} 处错误";
            if (MessageBox.Show(this, $"{reason}，保存后在改好之前不能用于检定。仍要保存吗？", "保存凹分序列",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return false;
            }
        }

        var root = SequenceDataRoot;
        try
        {
            if (name != _sequenceLoadedName && SequenceFiles.Exists(root, name)
                && MessageBox.Show(this, $"已经有一份叫“{name}”的序列。要用现在的内容覆盖它吗？", "覆盖序列",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            {
                return false;
            }

            SequenceFiles.Write(root, name, SequenceEditor.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SequenceSaveText.Text = "保存失败：" + ex.Message;
            return false;
        }

        _sequenceLoadedName = name;
        _sequenceSavedText = SequenceEditor.Text;
        RefreshSequencePicker(name);
        UpdateSequenceDirty();
        SequenceSaveText.Text = $"已保存“{name}”（{DateTime.Now:HH:mm:ss}）" + (errors > 0 || sequence is null ? "，但还不能用于检定。" : "。");
        _storage.Log("sequence", $"保存序列 | 步数={sequence?.Steps.Count ?? 0} | 错误={errors}");
        return true;
    }

    private void SequenceReloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "丢掉还没保存的修改，回到上次保存的内容吗？", "放弃修改",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
        {
            SequenceEditor.Text = _sequenceSavedText;
            SequenceNameBox.Text = _sequenceLoadedName ?? string.Empty;
        }
    }

    private void SequenceFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folder = SequenceFiles.Folder(SequenceDataRoot);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SequenceSaveText.Text = "无法打开序列文件夹：" + ex.Message;
            return;
        }

        OpenFolder(folder);
    }
}
