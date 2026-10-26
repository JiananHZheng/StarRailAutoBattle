namespace GameLauncher.Core;

public sealed record ProcessRecord(int Id, string Name, string? ImagePath, int? PathError);

public sealed record WindowRecord(
    IntPtr Handle,
    int ProcessId,
    string Title,
    string ClassName,
    bool Visible,
    bool Minimized,
    bool HasOwner,
    bool IsToolWindow,
    int Width,
    int Height);

public sealed record WindowCandidate(ProcessRecord Process, WindowRecord Window, bool PathVerified);

public enum MatchStatus
{
    /// <summary>No process with the target's file name is running.</summary>
    NotRunning,
    /// <summary>Same-named processes exist, but their readable paths point to a different program.</summary>
    OnlyOtherProgramRunning,
    /// <summary>The process is running but has no switchable top-level window yet.</summary>
    RunningNoWindow,
    /// <summary>A window of a process whose full path matches the target was found.</summary>
    Found,
    /// <summary>Only processes whose path Windows would not reveal were found; the user must confirm.</summary>
    NeedsConfirmation,
}

public sealed record MatchResult(
    MatchStatus Status,
    WindowCandidate? Best,
    IReadOnlyList<WindowCandidate> Candidates,
    IReadOnlyList<ProcessRecord> NameMatches);

/// <summary>
/// Pure matching logic: decides which window belongs to the target program. It requires the process
/// file name to match and, when Windows allows reading it, the full image path. It never matches by title alone.
/// </summary>
public static class WindowMatcher
{
    public static MatchResult Match(string targetExePath, IReadOnlyList<ProcessRecord> processes, IReadOnlyList<WindowRecord> windows)
    {
        var processName = Path.GetFileNameWithoutExtension(targetExePath);
        var nameMatches = processes
            .Where(p => string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (nameMatches.Count == 0)
        {
            return new MatchResult(MatchStatus.NotRunning, null, [], nameMatches);
        }

        var relevant = new Dictionary<int, (ProcessRecord Process, bool Verified)>();
        foreach (var process in nameMatches)
        {
            if (process.ImagePath is null)
            {
                relevant[process.Id] = (process, false);
            }
            else if (PathsEqual(process.ImagePath, targetExePath))
            {
                relevant[process.Id] = (process, true);
            }
        }

        if (relevant.Count == 0)
        {
            return new MatchResult(MatchStatus.OnlyOtherProgramRunning, null, [], nameMatches);
        }

        var candidates = windows
            .Where(IsSwitchableWindow)
            .Where(w => relevant.ContainsKey(w.ProcessId))
            .Select(w => new WindowCandidate(relevant[w.ProcessId].Process, w, relevant[w.ProcessId].Verified))
            .OrderByDescending(c => c.PathVerified)
            .ThenByDescending(c => c.Window.Title.Length > 0)
            .ThenByDescending(c => (long)c.Window.Width * c.Window.Height)
            .ToList();

        if (candidates.Count == 0)
        {
            return new MatchResult(MatchStatus.RunningNoWindow, null, candidates, nameMatches);
        }

        var best = candidates[0];
        return new MatchResult(best.PathVerified ? MatchStatus.Found : MatchStatus.NeedsConfirmation, best, candidates, nameMatches);
    }

    /// <summary>A top-level, unowned, non-tool window that is shown (possibly minimized).</summary>
    public static bool IsSwitchableWindow(WindowRecord window) =>
        window.Visible
        && !window.HasOwner
        && !window.IsToolWindow
        && (window.Minimized || (window.Width > 1 && window.Height > 1))
        && (window.Title.Length > 0 || window.ClassName.Length > 0);

    public static bool PathsEqual(string a, string b) =>
        string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    internal static string NormalizePath(string path)
    {
        var value = path.Trim();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            value = @"\\" + value[8..];
        }
        else if (value.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            value = value[4..];
        }

        try
        {
            value = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Compare the raw string if it cannot be normalised.
        }

        return value.TrimEnd('\\', '/');
    }
}
