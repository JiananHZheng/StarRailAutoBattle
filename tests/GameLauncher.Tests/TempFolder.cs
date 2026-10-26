namespace GameLauncher.Tests;

/// <summary>A throw-away directory for test files. Tests only create and read files; nothing is executed.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, byte[] content)
    {
        var path = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Creates a real .lnk with the Windows Shell (WScript.Shell). The target is never started.</summary>
    public string Shortcut(string name, string target, string arguments = "", string workingDirectory = "")
    {
        var path = System.IO.Path.Combine(Path, name);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = arguments;
        link.WorkingDirectory = workingDirectory;
        link.Save();
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
