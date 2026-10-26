using System.Runtime.InteropServices;
using GameLauncher.Core.Native;

namespace GameLauncher.Core;

public sealed record LaunchOutcome(
    bool Accepted,
    int? ProcessId,
    bool ExitedEarly,
    uint? ExitCode,
    LaunchFailure? Failure,
    string Method);

/// <summary>
/// Starts the user's entry the same way a double-click in Explorer does: ShellExecuteEx with the default verb.
/// No command string is built: the file path goes in lpFile, and parameters/working directory of a .lnk stay
/// inside the shortcut for Windows to apply. No "runas" verb is used; if the target's manifest requires
/// administrator rights, Windows itself shows the UAC prompt.
/// </summary>
public static class ShellLauncher
{
    public static Task<LaunchOutcome> LaunchAsync(EntryInfo entry, IntPtr ownerWindow, TimeSpan earlyExitWindow)
    {
        var request = BuildRequest(entry);
        return RunOnStaThread(() => Launch(request, ownerWindow, earlyExitWindow, entry.ResolvedExecutable?.ExecutionLevel));
    }

    internal sealed record ShellRequest(string File, string? Directory, string Method);

    internal static ShellRequest BuildRequest(EntryInfo entry) => entry.Kind switch
    {
        // The shortcut supplies its own target, arguments and "Start in" folder.
        EntryKind.Shortcut => new ShellRequest(entry.Path, null, "ShellExecuteEx(.lnk)"),
        _ => new ShellRequest(entry.Path, Path.GetDirectoryName(entry.Path), "ShellExecuteEx(.exe)"),
    };

    private static LaunchOutcome Launch(ShellRequest request, IntPtr ownerWindow, TimeSpan earlyExitWindow, ExecutionLevel? level)
    {
        var info = new NativeMethods.SHELLEXECUTEINFOW
        {
            cbSize = Marshal.SizeOf<NativeMethods.SHELLEXECUTEINFOW>(),
            fMask = NativeMethods.SEE_MASK_NOCLOSEPROCESS | NativeMethods.SEE_MASK_NOASYNC,
            hwnd = ownerWindow,
            lpVerb = null,
            lpFile = request.File,
            lpParameters = null,
            lpDirectory = request.Directory,
            nShow = NativeMethods.SW_SHOWNORMAL,
        };

        if (!NativeMethods.ShellExecuteExW(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            return new LaunchOutcome(false, null, false, null, LaunchErrorMapper.Map(error, level), request.Method);
        }

        if (info.hProcess == IntPtr.Zero)
        {
            // Windows accepted the request but handed it to an existing process (no new process handle).
            return new LaunchOutcome(true, null, false, null, null, request.Method);
        }

        try
        {
            var processId = NativeMethods.GetProcessId(info.hProcess);
            var waitMs = (uint)Math.Clamp(earlyExitWindow.TotalMilliseconds, 0, 60_000);
            if (NativeMethods.WaitForSingleObject(info.hProcess, waitMs) == NativeMethods.WAIT_OBJECT_0
                && NativeMethods.GetExitCodeProcess(info.hProcess, out var exitCode))
            {
                return new LaunchOutcome(true, processId, true, exitCode, null, request.Method);
            }

            return new LaunchOutcome(true, processId == 0 ? null : processId, false, null, null, request.Method);
        }
        finally
        {
            NativeMethods.CloseHandle(info.hProcess);
        }
    }

    internal static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ShellLauncher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
