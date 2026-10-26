using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using GameLauncher.Core.Native;

namespace GameLauncher.Core;

public sealed record ShortcutData(string? TargetPath, string Arguments, string WorkingDirectory, int ShowCommand);

/// <summary>Reads a .lnk file with the Windows Shell's IShellLink. It never calls Resolve, so the file is not modified.</summary>
public static class ShortcutReader
{
    private const int StgmRead = 0;
    private const int BufferSize = 4096;

    public static ShortcutData Read(string shortcutPath)
    {
        var link = (IShellLinkW)new ShellLinkCoClass();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, StgmRead);

            var target = new StringBuilder(BufferSize);
            link.GetPath(target, target.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(BufferSize);
            link.GetArguments(arguments, arguments.Capacity);
            var directory = new StringBuilder(BufferSize);
            link.GetWorkingDirectory(directory, directory.Capacity);
            link.GetShowCmd(out var showCommand);

            var targetPath = Expand(target.ToString());
            return new ShortcutData(
                targetPath.Length == 0 ? null : targetPath,
                arguments.ToString(),
                Expand(directory.ToString()),
                showCommand);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value.Trim());
}
