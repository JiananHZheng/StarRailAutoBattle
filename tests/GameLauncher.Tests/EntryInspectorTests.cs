using GameLauncher.Core;

namespace GameLauncher.Tests;

public sealed class EntryInspectorTests : IDisposable
{
    private static readonly string Notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("", InspectErrorKind.Empty)]
    [InlineData("   ", InspectErrorKind.Empty)]
    [InlineData("game.exe", InspectErrorKind.NotAbsolute)]
    [InlineData(@"Games\game.exe", InspectErrorKind.NotAbsolute)]
    [InlineData("C:game.exe", InspectErrorKind.NotAbsolute)]
    [InlineData("C:\\a\"b.exe", InspectErrorKind.InvalidCharacters)]
    [InlineData("C:\\a.exe\nC:\\b.exe", InspectErrorKind.InvalidCharacters)]
    public void Rejects_bad_paths(string input, InspectErrorKind expected)
    {
        Assert.Equal(expected, EntryInspector.Inspect(input).Error?.Kind);
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        var result = EntryInspector.Inspect(Path.Combine(_temp.Path, "nope.exe"));
        Assert.Equal(InspectErrorKind.NotFound, result.Error?.Kind);
    }

    [Fact]
    public void Display_name_with_hidden_lnk_extension_is_explained()
    {
        // Explorer shows "StarRail_for_test.exe" for a file really named "StarRail_for_test.exe.lnk".
        _temp.Shortcut("StarRail_for_test.exe.lnk", Notepad);
        var result = EntryInspector.Inspect(Path.Combine(_temp.Path, "StarRail_for_test.exe"));
        Assert.Equal(InspectErrorKind.NotFoundButShortcutExists, result.Error?.Kind);
        Assert.Contains(".lnk", result.Error!.Message);
    }

    [Fact]
    public void Directory_is_rejected()
    {
        Assert.Equal(InspectErrorKind.IsDirectory, EntryInspector.Inspect(_temp.Path).Error?.Kind);
    }

    [Fact]
    public void Real_exe_is_detected_by_content()
    {
        var result = EntryInspector.Inspect(Notepad);
        Assert.True(result.Ok);
        Assert.Equal(EntryKind.Executable, result.Entry!.Kind);
        Assert.Equal(Notepad, result.Entry.ResolvedExecutable!.Path, ignoreCase: true);
        Assert.NotEqual(PeArchitecture.Unknown, result.Entry.ResolvedExecutable.Architecture);
    }

    [Fact]
    public void Surrounding_quotes_from_copy_as_path_are_removed()
    {
        var result = EntryInspector.Inspect($"\"{Notepad}\"");
        Assert.True(result.Ok);
        Assert.Contains(result.Entry!.Notes, n => n.Contains("引号"));
    }

    [Fact]
    public void Text_file_renamed_to_exe_is_not_a_program()
    {
        var path = _temp.File("fake.exe", "hello"u8.ToArray());
        Assert.Equal(InspectErrorKind.NotValidExecutable, EntryInspector.Inspect(path).Error?.Kind);
    }

    [Fact]
    public void Truncated_pe_is_not_a_program()
    {
        var path = _temp.File("broken.exe", [(byte)'M', (byte)'Z', 0, 0]);
        Assert.Equal(InspectErrorKind.NotValidExecutable, EntryInspector.Inspect(path).Error?.Kind);
    }

    [Theory]
    [InlineData("start.bat")]
    [InlineData("game.url")]
    [InlineData("readme")]
    public void Unsupported_types_are_rejected(string name)
    {
        var path = _temp.File(name, "@echo off"u8.ToArray());
        Assert.Equal(InspectErrorKind.UnsupportedType, EntryInspector.Inspect(path).Error?.Kind);
    }

    [Fact]
    public void Shortcut_content_with_exe_extension_is_flagged()
    {
        var lnk = _temp.Shortcut("real.lnk", Notepad);
        var disguised = Path.Combine(_temp.Path, "looks_like.exe");
        File.Copy(lnk, disguised);
        var result = EntryInspector.Inspect(disguised);
        Assert.Equal(InspectErrorKind.UnsupportedType, result.Error?.Kind);
        Assert.Contains("快捷方式", result.Error!.Message);
    }

    [Fact]
    public void Shortcut_target_arguments_and_start_folder_are_resolved()
    {
        var lnk = _temp.Shortcut("game.lnk", Notepad, "-popupwindow --x=\"a b\"", _temp.Path);
        var result = EntryInspector.Inspect(lnk);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(EntryKind.Shortcut, result.Entry!.Kind);
        var shortcut = result.Entry.Shortcut!;
        Assert.Equal(Notepad, shortcut.TargetPath, ignoreCase: true);
        Assert.Equal("-popupwindow --x=\"a b\"", shortcut.Arguments);
        Assert.Equal(_temp.Path, shortcut.WorkingDirectory, ignoreCase: true);
        Assert.Equal(Notepad, result.Entry.ResolvedExecutable!.Path, ignoreCase: true);
    }

    [Fact]
    public void Shortcut_with_missing_target_is_reported()
    {
        var target = _temp.File("gone.exe", File.ReadAllBytes(Notepad));
        var lnk = _temp.Shortcut("gone.lnk", target);
        File.Delete(target);
        Assert.Equal(InspectErrorKind.ShortcutTargetMissing, EntryInspector.Inspect(lnk).Error?.Kind);
    }

    [Fact]
    public void Shortcut_to_non_exe_is_rejected()
    {
        var doc = _temp.File("notes.txt", "x"u8.ToArray());
        var lnk = _temp.Shortcut("notes.lnk", doc);
        Assert.Equal(InspectErrorKind.ShortcutTargetNotExecutable, EntryInspector.Inspect(lnk).Error?.Kind);
    }

    [Fact]
    public void Shortcut_with_missing_start_folder_gets_a_note()
    {
        var lnk = _temp.Shortcut("nodir.lnk", Notepad, "", Path.Combine(_temp.Path, "missing-dir"));
        var result = EntryInspector.Inspect(lnk);
        Assert.True(result.Ok);
        Assert.Contains(result.Entry!.Notes, n => n.Contains("起始位置"));
    }

    [Fact]
    public void Corrupt_lnk_is_unreadable()
    {
        var bytes = new byte[] { 0x4C, 0, 0, 0, 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46, 1, 2, 3 };
        var path = _temp.File("corrupt.lnk", bytes);
        Assert.Equal(InspectErrorKind.ShortcutUnreadable, EntryInspector.Inspect(path).Error?.Kind);
    }

    [Fact]
    public void Shell_request_keeps_shortcut_settings_inside_the_shortcut()
    {
        var lnk = _temp.Shortcut("args.lnk", Notepad, "--flag", _temp.Path);
        var entry = EntryInspector.Inspect(lnk).Entry!;
        var request = ShellLauncher.BuildRequest(entry);
        Assert.Equal(lnk, request.File, ignoreCase: true);
        Assert.Null(request.Directory);

        var exeRequest = ShellLauncher.BuildRequest(EntryInspector.Inspect(Notepad).Entry!);
        Assert.Equal(Notepad, exeRequest.File, ignoreCase: true);
        Assert.Equal(Environment.SystemDirectory, exeRequest.Directory, ignoreCase: true);
    }
}
