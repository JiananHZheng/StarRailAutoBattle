using GameLauncher.Core;

namespace GameLauncher.Tests;

public sealed class AppStorageTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Settings_round_trip()
    {
        var storage = new AppStorage(_temp.Path);
        var settings = new UserSettings { EntryPath = @"D:\a.lnk", GameExePath = @"D:\b.exe", Mode = LaunchMode.IdentifyRunning };
        storage.SaveSettings(settings);
        Assert.Equal(settings, storage.LoadSettings());
    }

    [Fact]
    public void Damaged_settings_fall_back_to_defaults()
    {
        var storage = new AppStorage(_temp.Path);
        File.WriteAllText(storage.SettingsPath, "{not json");
        Assert.Equal(new UserSettings(), storage.LoadSettings());
    }

    [Fact]
    public void Log_lines_are_single_line()
    {
        var storage = new AppStorage(_temp.Path);
        storage.Log("stage", "a\nb");
        var lines = File.ReadAllLines(storage.LogPath);
        Assert.Single(lines);
        Assert.Contains("a b", lines[0]);
    }
}
