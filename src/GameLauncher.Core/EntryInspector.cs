namespace GameLauncher.Core;

public enum EntryKind
{
    Executable,
    Shortcut,
}

public enum InspectErrorKind
{
    Empty,
    NotAbsolute,
    InvalidCharacters,
    NotFound,
    NotFoundButShortcutExists,
    IsDirectory,
    AccessDenied,
    UnsupportedType,
    NotValidExecutable,
    ShortcutUnreadable,
    ShortcutTargetMissing,
    ShortcutTargetNotExecutable,
}

public sealed record InspectError(InspectErrorKind Kind, string Message);

public sealed record ShortcutInfo(
    string? TargetPath,
    string Arguments,
    string WorkingDirectory,
    ExecutableInfo? TargetExecutable);

public sealed record EntryInfo(
    string Path,
    EntryKind Kind,
    ExecutableInfo? Executable,
    ShortcutInfo? Shortcut,
    IReadOnlyList<string> Notes)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>The program that will actually run: the .exe itself, or the shortcut's resolved .exe target.</summary>
    public ExecutableInfo? ResolvedExecutable => Kind == EntryKind.Executable ? Executable : Shortcut?.TargetExecutable;

    public string KindDisplay => Kind == EntryKind.Executable ? "Windows 程序（.exe）" : "Windows 快捷方式（.lnk）";
}

public sealed record InspectResult(EntryInfo? Entry, InspectError? Error)
{
    public bool Ok => Entry is not null;
}

/// <summary>
/// Validates a user-supplied entry path and determines what it really is (by file content, not by display name).
/// It only reads files; it never starts anything.
/// </summary>
public static class EntryInspector
{
    public const int MaxPathLength = 32_000;

    public static InspectResult Inspect(string? input)
    {
        var notes = new List<string>();
        var normalized = NormalizeInput(input, notes);
        if (normalized.Error is not null)
        {
            return Fail(normalized.Error);
        }

        var path = normalized.Path!;
        if (Directory.Exists(path))
        {
            return Fail(InspectErrorKind.IsDirectory, "这是一个文件夹，不是程序。请选择文件夹里的 .exe，或它的快捷方式。");
        }

        if (!File.Exists(path))
        {
            var withLnk = path + ".lnk";
            if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(withLnk))
            {
                return Fail(
                    InspectErrorKind.NotFoundButShortcutExists,
                    $"找不到“{Path.GetFileName(path)}”，但同一位置有快捷方式“{Path.GetFileName(withLnk)}”。" +
                    "资源管理器默认隐藏 .lnk 扩展名，显示名里的 .exe 只是名称的一部分。请用“浏览文件”选择它，或在路径末尾加上 .lnk。");
            }

            return Fail(InspectErrorKind.NotFound, "找不到这个文件。请确认路径和文件名正确，或用“浏览文件”重新选择。");
        }

        ExecutableInspector.FileSignature signature;
        try
        {
            signature = ExecutableInspector.DetectSignature(path);
        }
        catch (UnauthorizedAccessException)
        {
            return Fail(InspectErrorKind.AccessDenied, "本应用无法读取这个文件（访问被拒绝）。请确认文件所在文件夹允许当前用户读取。");
        }
        catch (IOException ex)
        {
            return Fail(InspectErrorKind.AccessDenied, $"读取文件失败：{ex.Message}");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (signature)
        {
            case ExecutableInspector.FileSignature.Shortcut when extension == ".lnk":
                return InspectShortcut(path, notes);
            case ExecutableInspector.FileSignature.Shortcut:
                return Fail(
                    InspectErrorKind.UnsupportedType,
                    "这个文件的内容是 Windows 快捷方式，但扩展名不是 .lnk。请把扩展名改回 .lnk，或直接选择真实的 .exe。");
            case ExecutableInspector.FileSignature.PortableExecutable when extension == ".exe":
                return InspectExecutable(path, notes);
        }

        if (extension == ".exe")
        {
            return Fail(InspectErrorKind.NotValidExecutable, "这个文件名以 .exe 结尾，但内容不是有效的 Windows 程序。请重新选择游戏或启动器的真实程序。");
        }

        return Fail(
            InspectErrorKind.UnsupportedType,
            $"不支持“{(extension.Length == 0 ? "无扩展名" : extension)}”类型。请选择 .exe 程序或指向 .exe 的 .lnk 快捷方式。");
    }

    internal sealed record NormalizedPath(string? Path, InspectError? Error);

    internal static NormalizedPath NormalizeInput(string? input, List<string> notes)
    {
        var text = input?.Trim() ?? string.Empty;
        if (text.Length >= 2 && text[0] == '"' && text[^1] == '"')
        {
            text = text[1..^1].Trim();
            notes.Add("已去掉路径两端的引号（资源管理器“复制文件地址”会自动加引号）。");
        }

        if (text.Length == 0)
        {
            return new(null, new InspectError(InspectErrorKind.Empty, "请填写游戏程序或快捷方式的完整路径，或点“浏览文件”选择。"));
        }

        if (text.Length > MaxPathLength || text.Any(char.IsControl) || text.Contains('"') || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return new(null, new InspectError(InspectErrorKind.InvalidCharacters, "路径里有无效字符（例如引号或换行）。请只填写一个文件路径。"));
        }

        if (!Path.IsPathFullyQualified(text))
        {
            return new(null, new InspectError(InspectErrorKind.NotAbsolute, "请填写完整路径，例如 D:\\Games\\Launcher.exe。"));
        }

        try
        {
            return new(Path.GetFullPath(text), null);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, new InspectError(InspectErrorKind.InvalidCharacters, "路径格式无效，请检查后重试。"));
        }
    }

    private static InspectResult InspectExecutable(string path, List<string> notes)
    {
        var architecture = ExecutableInspector.ReadArchitecture(path);
        if (architecture is null)
        {
            return Fail(InspectErrorKind.NotValidExecutable, "这个 .exe 文件不完整或不是有效的 Windows 程序。请重新选择。");
        }

        var executable = ExecutableInspector.Inspect(path, architecture.Value);
        return new InspectResult(new EntryInfo(path, EntryKind.Executable, executable, null, notes), null);
    }

    private static InspectResult InspectShortcut(string path, List<string> notes)
    {
        ShortcutData data;
        try
        {
            data = ShortcutReader.Read(path);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException or InvalidCastException)
        {
            return Fail(InspectErrorKind.ShortcutUnreadable, $"Windows 无法读取这个快捷方式（{ex.Message.Trim()}）。请重新创建快捷方式，或直接选择真实的 .exe。");
        }

        if (data.TargetPath is null)
        {
            // Advertised (installer) or shell-namespace shortcuts have no file target; Windows can still open them.
            notes.Add("这个快捷方式没有普通文件目标（可能是安装程序或应用商店创建的特殊快捷方式），启动时完全交给 Windows 处理。“只识别已运行游戏”需在下方另行指定游戏程序。");
            return new InspectResult(new EntryInfo(path, EntryKind.Shortcut, null, new ShortcutInfo(null, data.Arguments, data.WorkingDirectory, null), notes), null);
        }

        if (Directory.Exists(data.TargetPath))
        {
            return Fail(InspectErrorKind.ShortcutTargetNotExecutable, "这个快捷方式指向的是文件夹，不是程序。请在快捷方式属性中检查“目标”。");
        }

        if (!File.Exists(data.TargetPath))
        {
            return Fail(
                InspectErrorKind.ShortcutTargetMissing,
                $"快捷方式的目标“{Path.GetFileName(data.TargetPath)}”不存在。游戏可能已被移动、卸载，或所在磁盘未连接。请右键快捷方式 → 属性，检查“目标”。");
        }

        if (!Path.GetExtension(data.TargetPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Fail(
                InspectErrorKind.ShortcutTargetNotExecutable,
                $"这个快捷方式的目标是“{Path.GetFileName(data.TargetPath)}”，不是 .exe 程序。请选择游戏或官方启动器的快捷方式。");
        }

        ExecutableInfo? target = null;
        try
        {
            var architecture = ExecutableInspector.ReadArchitecture(data.TargetPath);
            if (architecture is null)
            {
                return Fail(InspectErrorKind.ShortcutTargetNotExecutable, "快捷方式的目标不是有效的 Windows 程序。");
            }

            target = ExecutableInspector.Inspect(data.TargetPath, architecture.Value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            notes.Add("本应用无法读取快捷方式目标的文件内容，未能检查其权限要求；启动时仍由 Windows 处理。");
        }

        if (data.WorkingDirectory.Length > 0 && !Directory.Exists(data.WorkingDirectory))
        {
            notes.Add("快捷方式的“起始位置”文件夹不存在，程序可能无法正常启动。请在快捷方式属性中检查。");
        }

        var shortcut = new ShortcutInfo(data.TargetPath, data.Arguments, data.WorkingDirectory, target);
        return new InspectResult(new EntryInfo(path, EntryKind.Shortcut, null, shortcut, notes), null);
    }

    private static InspectResult Fail(InspectErrorKind kind, string message) => Fail(new InspectError(kind, message));

    private static InspectResult Fail(InspectError error) => new(null, error);
}
