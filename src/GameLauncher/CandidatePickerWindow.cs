using System.Windows;
using System.Windows.Controls;
using GameLauncher.Core;

namespace GameLauncher;

/// <summary>Lets the user confirm a window whose process path Windows would not reveal. Nothing is switched without a click.</summary>
public sealed class CandidatePickerWindow : Window
{
    private readonly ListBox _list;
    private readonly IReadOnlyList<WindowCandidate> _candidates;

    public CandidatePickerWindow(string explanation, IReadOnlyList<WindowCandidate> candidates)
    {
        _candidates = candidates;
        Title = "确认游戏窗口";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (System.Windows.Media.Brush)Application.Current.Resources["PageBrush"];
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI, Segoe UI");

        _list = new ListBox { Margin = new Thickness(0, 12, 0, 12), MaxHeight = 220, FontSize = 13 };
        foreach (var candidate in candidates)
        {
            var title = candidate.Window.Title.Length > 0 ? candidate.Window.Title : "（无标题）";
            var state = candidate.Window.Minimized ? " · 已最小化" : string.Empty;
            _list.Items.Add(new ListBoxItem
            {
                Content = $"{title}　—　{candidate.Process.Name}.exe，进程 ID {candidate.Process.Id}{state}",
                Padding = new Thickness(6, 5, 6, 5),
            });
        }

        _list.SelectedIndex = 0;

        var confirm = new Button
        {
            Content = "切换到所选窗口",
            IsDefault = true,
            Style = (Style)Application.Current.Resources["SecondaryButton"],
            Margin = new Thickness(0, 0, 8, 0),
        };
        confirm.Click += (_, _) => DialogResult = _list.SelectedIndex >= 0;
        var cancel = new Button
        {
            Content = "取消",
            IsCancel = true,
            Style = (Style)Application.Current.Resources["SecondaryButton"],
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock { Text = explanation, FontSize = 13, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(_list);
        root.Children.Add(buttons);
        Content = root;
    }

    public WindowCandidate? Selected => _list.SelectedIndex >= 0 ? _candidates[_list.SelectedIndex] : null;
}
