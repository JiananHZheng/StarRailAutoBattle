using AxisRecognizer;
using OpenCvSharp;

namespace AxisRecognizer.Tests;

public class FormatAndVideoTests
{
    [Theory]
    [InlineData("current", SlotKind.Current, 1)]
    [InlineData("main2", SlotKind.Main, 2)]
    [InlineData("MAIN7", SlotKind.Main, 7)]
    [InlineData("insert1", SlotKind.Insert, 1)]
    public void Slot_names_parse(string name, SlotKind kind, int order)
    {
        Assert.Equal((kind, order), SlotNames.Parse(name));
    }

    [Theory]
    [InlineData("main1")]
    [InlineData("insert0")]
    [InlineData("left")]
    public void Bad_slot_names_are_rejected(string name)
    {
        Assert.Throws<FormatException>(() => SlotNames.Parse(name));
    }

    [Fact]
    public void Answers_file_parses_sections_comments_and_frame_keys()
    {
        var sections = AnswersFile.Parse("""
            # 第一张
            [a.png]
            current = CH-流萤
            main2 = M0003

            [clip.mp4@12.5]
            current=EMPTY
            """);
        Assert.Equal(2, sections.Count);
        Assert.Equal("CH-流萤", sections[0].Slots["current"]);
        Assert.Equal(("clip.mp4", 12.5), AnswersFile.SplitFrameKey(sections[1].FrameKey));
        Assert.Equal(("a.png", (double?)null), AnswersFile.SplitFrameKey("a.png"));
    }

    [Fact]
    public void Answers_file_rejects_lines_outside_sections()
    {
        Assert.Throws<FormatException>(() => AnswersFile.Parse("current = M0001"));
    }

    [Fact]
    public void Video_frames_can_be_sampled_from_a_non_ascii_path()
    {
        using var dir = new TempDir("视频");
        var asciiPath = Path.Combine(Path.GetTempPath(), $"axis_test_{Guid.NewGuid():N}.avi");
        try
        {
            using (var writer = new VideoWriter(asciiPath, FourCC.MJPG, 10, new Size(320, 180)))
            {
                Assert.True(writer.IsOpened());
                for (var i = 0; i < 30; i++)
                {
                    using var frame = new Mat(180, 320, MatType.CV_8UC3, new Scalar(i * 8, 100, 200));
                    writer.Write(frame);
                }
            }

            var target = Path.Combine(dir.Path, "测试录像.avi");
            File.Copy(asciiPath, target);
            var probe = VideoFrames.Probe(target);
            Assert.Equal(320, probe.Width);

            var times = new List<double>();
            foreach (var (time, frame) in VideoFrames.Sample(target, 0.5))
            {
                using (frame)
                {
                    Assert.Equal(180, frame.Height);
                    times.Add(time);
                }
            }

            Assert.Equal([0, 0.5, 1.0, 1.5, 2.0, 2.5], times.Select(t => Math.Round(t, 2)));
        }
        finally
        {
            File.Delete(asciiPath);
        }
    }
}
