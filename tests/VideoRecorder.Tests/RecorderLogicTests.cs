using VideoRecorder;

namespace VideoRecorder.Tests;

public class TimeTextTests
{
    [Theory]
    [InlineData("74", 74)]
    [InlineData("74.5", 74.5)]
    [InlineData("1:14", 74)]
    [InlineData("01:14", 74)]
    [InlineData("1：14", 74)]
    [InlineData("1:02:03", 3723)]
    [InlineData("0:05.5", 5.5)]
    [InlineData(" 2:00 ", 120)]
    public void Valid_times_parse(string input, double expected)
    {
        Assert.True(TimeText.TryParse(input, out var seconds, out var error), error);
        Assert.Equal(expected, seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_means_not_given(string? input)
    {
        Assert.True(TimeText.TryParse(input, out var seconds, out _));
        Assert.Null(seconds);
    }

    [Theory]
    [InlineData("1:75")]
    [InlineData("1:2:3:4")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5:00")]
    [InlineData("1::00")]
    [InlineData("25:00:00")]
    public void Invalid_times_are_rejected_with_a_message(string input)
    {
        Assert.False(TimeText.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(74, "01:14")]
    [InlineData(74.5, "01:14.5")]
    [InlineData(3723, "1:02:03")]
    [InlineData(0, "00:00")]
    public void Times_format(double seconds, string expected) => Assert.Equal(expected, TimeText.Format(seconds));
}

public class BilibiliLinkTests
{
    [Fact]
    public void Share_text_is_reduced_to_a_clean_video_link()
    {
        Assert.True(BilibiliLinks.TryParse(
            "【世界第一绯英！绯英&真珠4金0t王棋亚婆离】 https://www.bilibili.com/video/BV1Y3aq6VEY5/?share_source=copy_web&vd_source=0123456789abcdef0123456789abcdef",
            out var link, out var error), error);
        Assert.Equal("https://www.bilibili.com/video/BV1Y3aq6VEY5/", link!.Url.AbsoluteUri);
        Assert.Equal("BV1Y3aq6VEY5", link.VideoId);
        Assert.Null(link.Part);
    }

    [Fact]
    public void Part_number_is_kept()
    {
        Assert.True(BilibiliLinks.TryParse("https://m.bilibili.com/video/BV1Y3aq6VEY5?p=3&spm_id_from=x", out var link, out _));
        Assert.Equal("https://www.bilibili.com/video/BV1Y3aq6VEY5/?p=3", link!.Url.AbsoluteUri);
        Assert.Equal(3, link.Part);
    }

    [Fact]
    public void Short_links_and_bare_ids_are_accepted()
    {
        Assert.True(BilibiliLinks.TryParse("看这个 https://b23.tv/AbC123x", out var shortLink, out _));
        Assert.True(shortLink!.IsShortLink);
        Assert.Equal("https://b23.tv/AbC123x", shortLink.Url.AbsoluteUri);

        Assert.True(BilibiliLinks.TryParse("BV1Y3aq6VEY5", out var bare, out _));
        Assert.Equal("https://www.bilibili.com/video/BV1Y3aq6VEY5/", bare!.Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://www.douyin.com/video/123")]
    [InlineData("https://www.bilibili.com/")]
    [InlineData("https://space.bilibili.com/12345")]
    [InlineData("https://evil.example/www.bilibili.com/video/BV1Y3aq6VEY5")]
    [InlineData("https://www.bilibili.com.evil.example/video/BV1Y3aq6VEY5")]
    [InlineData("https://user:pw@www.bilibili.com/video/BV1Y3aq6VEY5")]
    [InlineData("没有网址")]
    [InlineData("")]
    public void Other_pages_are_rejected_with_a_message(string input)
    {
        Assert.False(BilibiliLinks.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("https://www.bilibili.com/video/BV1Y3aq6VEY5/", true)]
    [InlineData("https://passport.bilibili.com/login", true)]
    [InlineData("https://b23.tv/abc", true)]
    [InlineData("https://bilibili.com.evil.example/", false)]
    [InlineData("https://example.com/", false)]
    [InlineData("file:///C:/x", false)]
    public void Recorder_window_only_navigates_within_bilibili(string url, bool allowed) =>
        Assert.Equal(allowed, BilibiliLinks.IsAllowedNavigation(new Uri(url)));
}

public class BilibiliAccountTests
{
    private static readonly DateTime Now = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Signed_in_needs_both_session_cookies_unexpired()
    {
        var later = Now.AddDays(30);
        Assert.True(BilibiliAccount.IsSignedIn([("SESSDATA", later, false), ("DedeUserID", later, false), ("buvid3", later, false)], Now));
        Assert.True(BilibiliAccount.IsSignedIn([("SESSDATA", DateTime.MinValue, true), ("DedeUserID", later, false)], Now));
    }

    [Fact]
    public void Anonymous_or_expired_cookies_are_not_signed_in()
    {
        Assert.False(BilibiliAccount.IsSignedIn([("buvid3", Now.AddDays(30), false)], Now));
        Assert.False(BilibiliAccount.IsSignedIn([("SESSDATA", Now.AddDays(30), false)], Now));
        Assert.False(BilibiliAccount.IsSignedIn([("SESSDATA", Now.AddDays(-1), false), ("DedeUserID", Now.AddDays(30), false)], Now));
    }
}

public class CropRectTests
{
    // A 1536×826 CSS viewport captured at 1920×1033 (125 % scaling), 1080p picture letterboxed to the full height.
    private static BilibiliRecorder.StatusInfo Status(double x, double y, double w, double h, int vw = 1920, int vh = 1080) =>
        new(true, 0, false, false, false, 4, 100, vw, vh, x, y, w, h, 1536, 826.4, false, false, 1.25, 1);

    [Fact]
    public void Picture_inside_the_capture_with_the_video_aspect_is_cropped_in_capture_pixels()
    {
        var rect = BilibiliRecorder.CropRect(Status(33.42, 0, 1469.16, 826.4), 1920, 1033);
        Assert.NotNull(rect);
        Assert.Equal(42, rect.Value.X);
        Assert.Equal(1836, rect.Value.Width);
        Assert.Equal(1033, rect.Value.Height);
    }

    [Theory]
    [InlineData(0, 760, 1468.4, 826)]   // mostly below the capture: the 676×61 strip of the first logged-in run
    [InlineData(0, 0, 1536, 120)]       // not 16:9: a bar, not the picture
    [InlineData(0, 0, 40, 22)]          // tiny element
    public void Untrustworthy_positions_are_rejected(double x, double y, double w, double h) =>
        Assert.Null(BilibiliRecorder.CropRect(Status(x, y, w, h), 1920, 1033));
}

public class PlaybackRateTests
{
    [Theory]
    [InlineData(20, 15, 1.0)]     // 50 captures/s: real time is enough
    [InlineData(69, 15, 0.6)]     // 1080p JPEG on the developer machine
    [InlineData(150, 15, 0.25)]   // ~6.7 captures/s → 0.25× still gives 15 per video-second with spare
    [InlineData(400, 15, 0.15)]   // very slow captures: never below the floor
    [InlineData(150, 10, 0.4)]
    public void Rate_leaves_enough_captures_per_second_of_video(double captureMs, double fps, double expected) =>
        Assert.Equal(expected, BilibiliRecorder.PlaybackRate(captureMs, fps), 3);
}

public class FrameTimelineTests
{
    [Fact]
    public void Regular_captures_write_one_frame_each()
    {
        var timeline = new FrameTimeline(10, 10);
        Assert.Equal((0, 1), timeline.Add(10.0));
        Assert.Equal((0, 1), timeline.Add(10.1));
        Assert.Equal((0, 1), timeline.Add(10.2));
        Assert.Equal(3, timeline.Written);
    }

    [Fact]
    public void Gaps_are_filled_with_the_nearest_capture()
    {
        var timeline = new FrameTimeline(0, 10);
        timeline.Add(0.0);
        // Frames 0.1 … 0.5 are owed; 0.1 and 0.2 are closer to the old capture (midpoint 0.25).
        Assert.Equal((2, 3), timeline.Add(0.5));
        Assert.Equal(6, timeline.Written);
    }

    [Fact]
    public void Captures_between_output_times_and_going_backwards_write_nothing()
    {
        var timeline = new FrameTimeline(0, 10);
        timeline.Add(0.0);
        Assert.Equal((0, 0), timeline.Add(0.05));
        Assert.Equal((0, 0), timeline.Add(0.02));
        Assert.Equal((0, 1), timeline.Add(0.1));
    }

    [Fact]
    public void Finish_pads_to_the_end_of_the_range()
    {
        var timeline = new FrameTimeline(0, 10);
        timeline.Add(0.0);
        timeline.Add(0.1);
        Assert.Equal(8, timeline.Finish(1.0));
        Assert.Equal(10, timeline.Written);
        Assert.Equal(0, new FrameTimeline(0, 10).Finish(1.0));
    }
}
