using GameLauncher.Core;

namespace GameLauncher.Tests;

public class WindowMatcherTests
{
    private const string Target = @"D:\Games\Star Rail Game\StarRail.exe";

    private static ProcessRecord Proc(int id, string? path, string name = "StarRail", int? error = null) =>
        new(id, name, path, path is null ? error ?? 5 : null);

    private static WindowRecord Win(
        int pid,
        string title = "崩坏：星穹铁道",
        long handle = 0,
        bool visible = true,
        bool minimized = false,
        bool owner = false,
        bool tool = false,
        int w = 1920,
        int h = 1080,
        string cls = "UnityWndClass") =>
        new(new IntPtr(handle == 0 ? pid * 10 : handle), pid, title, cls, visible, minimized, owner, tool, w, h);

    [Fact]
    public void Not_running_when_no_process_has_the_file_name()
    {
        var result = WindowMatcher.Match(Target, [Proc(1, @"C:\x\Other.exe", "Other")], [Win(1)]);
        Assert.Equal(MatchStatus.NotRunning, result.Status);
    }

    [Fact]
    public void Title_alone_never_matches()
    {
        var result = WindowMatcher.Match(Target, [Proc(1, @"C:\Browser\browser.exe", "browser")], [Win(1, title: "崩坏：星穹铁道 - 视频")]);
        Assert.Equal(MatchStatus.NotRunning, result.Status);
    }

    [Fact]
    public void Same_name_from_a_different_folder_is_not_the_target()
    {
        var result = WindowMatcher.Match(Target, [Proc(1, @"E:\Copy\StarRail.exe")], [Win(1)]);
        Assert.Equal(MatchStatus.OnlyOtherProgramRunning, result.Status);
    }

    [Fact]
    public void Running_without_a_window_yet()
    {
        var result = WindowMatcher.Match(Target, [Proc(1, Target)], [Win(1, visible: false), Win(1, handle: 99, owner: true)]);
        Assert.Equal(MatchStatus.RunningNoWindow, result.Status);
    }

    [Fact]
    public void Verified_path_finds_the_window()
    {
        var result = WindowMatcher.Match(Target, [Proc(7, Target.ToUpperInvariant())], [Win(7)]);
        Assert.Equal(MatchStatus.Found, result.Status);
        Assert.True(result.Best!.PathVerified);
        Assert.Equal(7, result.Best.Process.Id);
    }

    [Fact]
    public void Nt_style_image_path_prefix_is_normalised()
    {
        var result = WindowMatcher.Match(Target, [Proc(7, @"\\?\" + Target)], [Win(7)]);
        Assert.Equal(MatchStatus.Found, result.Status);
    }

    [Fact]
    public void Minimized_window_is_still_a_candidate()
    {
        var result = WindowMatcher.Match(Target, [Proc(7, Target)], [Win(7, minimized: true, w: 160, h: 28)]);
        Assert.Equal(MatchStatus.Found, result.Status);
        Assert.True(result.Best!.Window.Minimized);
    }

    [Fact]
    public void Unreadable_path_requires_confirmation()
    {
        var result = WindowMatcher.Match(Target, [Proc(7, null)], [Win(7)]);
        Assert.Equal(MatchStatus.NeedsConfirmation, result.Status);
        Assert.False(result.Best!.PathVerified);
    }

    [Fact]
    public void Verified_candidate_wins_over_unverified()
    {
        var result = WindowMatcher.Match(
            Target,
            [Proc(1, null), Proc(2, Target)],
            [Win(1, w: 3840, h: 2160), Win(2, w: 800, h: 600)]);
        Assert.Equal(MatchStatus.Found, result.Status);
        Assert.Equal(2, result.Best!.Process.Id);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Tool_owned_hidden_and_zero_size_windows_are_ignored()
    {
        var result = WindowMatcher.Match(
            Target,
            [Proc(3, Target)],
            [
                Win(3, handle: 31, tool: true),
                Win(3, handle: 32, owner: true),
                Win(3, handle: 33, visible: false),
                Win(3, handle: 34, w: 0, h: 0),
                Win(3, handle: 35, title: "", cls: ""),
                Win(3, handle: 36, title: "main"),
            ]);
        Assert.Equal(MatchStatus.Found, result.Status);
        Assert.Equal(new IntPtr(36), result.Best!.Window.Handle);
        Assert.Single(result.Candidates);
    }

    [Fact]
    public void Prefers_titled_then_larger_window()
    {
        var result = WindowMatcher.Match(
            Target,
            [Proc(3, Target)],
            [Win(3, handle: 41, title: "", w: 4000, h: 4000), Win(3, handle: 42, w: 300, h: 300), Win(3, handle: 43, w: 1280, h: 720)]);
        Assert.Equal(new IntPtr(43), result.Best!.Window.Handle);
    }

    [Fact]
    public void Launcher_window_does_not_count_as_game()
    {
        // A launcher process (different file name) with a visible window is not the selected game.
        var result = WindowMatcher.Match(Target, [Proc(5, @"D:\Launcher\launcher.exe", "launcher")], [Win(5, title: "启动器")]);
        Assert.Equal(MatchStatus.NotRunning, result.Status);
    }
}
