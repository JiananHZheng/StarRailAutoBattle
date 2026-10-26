using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameLauncher.Core.Native;

namespace GameLauncher.Core;

public sealed record ActivationResult(bool WindowGone, bool WasMinimized, bool StillMinimized, bool ForegroundConfirmed);

/// <summary>Reads the live process/window list and performs the restore + foreground request.</summary>
public static class WindowService
{
    public static MatchResult FindGameWindow(string targetExePath)
    {
        var processes = GetProcessesByName(Path.GetFileNameWithoutExtension(targetExePath));
        var windows = processes.Count == 0 ? [] : GetTopLevelWindows(processes.Select(p => p.Id).ToHashSet());
        return WindowMatcher.Match(targetExePath, processes, windows);
    }

    public static IReadOnlyList<ProcessRecord> GetProcessesByName(string processName)
    {
        var result = new List<ProcessRecord>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                var path = TryGetImagePath(process.Id, out var error);
                result.Add(new ProcessRecord(process.Id, process.ProcessName, path, path is null ? error : null));
            }
        }

        return result;
    }

    /// <summary>Full image path via PROCESS_QUERY_LIMITED_INFORMATION; null when Windows denies it.</summary>
    public static string? TryGetImagePath(int processId, out int error)
    {
        error = 0;
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == IntPtr.Zero)
        {
            error = Marshal.GetLastWin32Error();
            return null;
        }

        try
        {
            var buffer = new StringBuilder(32_768);
            var size = buffer.Capacity;
            if (!NativeMethods.QueryFullProcessImageNameW(handle, 0, buffer, ref size))
            {
                error = Marshal.GetLastWin32Error();
                return null;
            }

            return buffer.ToString(0, size);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    public static IReadOnlyList<WindowRecord> GetTopLevelWindows(IReadOnlySet<int> processIds)
    {
        var windows = new List<WindowRecord>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (processIds.Contains(pid))
            {
                windows.Add(Describe(hwnd, pid));
            }

            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static WindowRecord Describe(IntPtr hwnd, int pid)
    {
        var title = new StringBuilder(512);
        NativeMethods.GetWindowTextW(hwnd, title, title.Capacity);
        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, className, className.Capacity);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        return new WindowRecord(
            hwnd,
            pid,
            title.ToString(),
            className.ToString(),
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.IsIconic(hwnd),
            NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero,
            (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0,
            rect.Right - rect.Left,
            rect.Bottom - rect.Top);
    }

    /// <summary>
    /// Restores a minimized window and asks Windows to bring it to the foreground. It does not use input
    /// injection or other tricks to defeat the foreground lock; the result reports what Windows actually did.
    /// </summary>
    public static async Task<ActivationResult> ActivateAsync(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd))
        {
            return new ActivationResult(true, false, false, false);
        }

        var wasMinimized = NativeMethods.IsIconic(hwnd);
        if (!wasMinimized && NativeMethods.GetForegroundWindow() == hwnd)
        {
            return new ActivationResult(false, false, false, true);
        }

        if (wasMinimized)
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        }

        NativeMethods.SetForegroundWindow(hwnd);

        var foreground = false;
        for (var i = 0; i < 10 && !foreground; i++)
        {
            await Task.Delay(100).ConfigureAwait(false);
            foreground = NativeMethods.GetForegroundWindow() == hwnd && !NativeMethods.IsIconic(hwnd);
        }

        return new ActivationResult(!NativeMethods.IsWindow(hwnd), wasMinimized, NativeMethods.IsIconic(hwnd), foreground);
    }
}
