using AxisRecognizer;

namespace AxisRecognizer.Tests;

public class VideoRunFilesTests
{
    private static readonly VideoRecognitionResult Sample = new("# 结果\n1. 爻光\n", "a@0.000\tcurrent\n", 2, 1, 1080, false);

    [Fact]
    public void Raw_result_is_read_only_and_edits_go_to_a_separate_writable_copy()
    {
        using var dir = new TempDir("运行");
        var runDir = VideoRunFiles.CreateRunDirectory(dir.Path, "BV1Y3aq6VEY5", new DateTime(2026, 10, 27, 12, 0, 0));
        try
        {
            var raw = VideoRunFiles.WriteResult(runDir, Sample);
            Assert.True(File.GetAttributes(raw).HasFlag(FileAttributes.ReadOnly));
            Assert.StartsWith(VideoRecognition.DetailHeader, File.ReadAllText(Path.Combine(runDir, VideoRunFiles.DetailFile)));

            var edit = VideoRunFiles.EnsureEditCopy(runDir);
            Assert.False(File.GetAttributes(edit).HasFlag(FileAttributes.ReadOnly));
            File.AppendAllText(edit, "用户修改\n");

            // A second click opens the same copy instead of overwriting the user's edits.
            Assert.Equal(edit, VideoRunFiles.EnsureEditCopy(runDir));
            Assert.Contains("用户修改", File.ReadAllText(edit));
            Assert.Equal(Sample.Text, File.ReadAllText(raw));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(runDir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Run_directories_are_named_by_time_and_label_and_never_reused()
    {
        using var dir = new TempDir();
        var time = new DateTime(2026, 10, 27, 9, 5, 7);
        var first = VideoRunFiles.CreateRunDirectory(dir.Path, "my clip:1", time);
        var second = VideoRunFiles.CreateRunDirectory(dir.Path, "my clip:1", time);
        Assert.Equal("20261027-090507_my_clip_1", Path.GetFileName(first));
        Assert.NotEqual(first, second);
        Assert.Equal(Path.Combine(dir.Path, "video-runs"), Path.GetDirectoryName(first));
    }

    [Fact]
    public void Default_data_root_is_found_from_any_folder_inside_the_repository()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "StarRailAutoBattle.slnx"), "<Solution />");
        var nested = Directory.CreateDirectory(Path.Combine(dir.Path, "dist", "sub")).FullName;
        Assert.Equal(Path.Combine(dir.Path, "axis-data"), AxisData.DefaultRoot(nested));

        using var outside = new TempDir();
        Assert.Equal(Path.Combine(outside.Path, "axis-data"), AxisData.DefaultRoot(outside.Path));
    }
}
