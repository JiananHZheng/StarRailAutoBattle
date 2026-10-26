using System.Diagnostics;
using GameLauncher.Core;

namespace GameLauncher.Tests;

public class WindowServiceTests
{
    [Fact]
    public void Reads_own_process_image_path()
    {
        using var self = Process.GetCurrentProcess();
        var path = WindowService.TryGetImagePath(self.Id, out var error);
        Assert.Equal(0, error);
        Assert.True(WindowMatcher.PathsEqual(self.MainModule!.FileName, path!));
    }

    [Fact]
    public void Program_that_is_not_running_is_reported_as_not_running()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"not-running-{Guid.NewGuid():N}.exe");
        Assert.Equal(MatchStatus.NotRunning, WindowService.FindGameWindow(fake).Status);
    }
}
