using System.IO;

namespace VideoRecorder;

public enum RecordingFailure
{
    RuntimeMissing,
    NetworkError,
    NotBilibili,
    VideoUnavailable,
    Captcha,
    LoginRequired,
    PlayerNotFound,
    BlackScreen,
    Stalled,
    InvalidRange,
    WriteFailed,
    Cancelled,

    /// <summary>The stream is DRM-protected; it is never decrypted.</summary>
    Protected,

    /// <summary>Only downloading was requested and it did not work.</summary>
    DownloadFailed,
}

/// <summary>How a clip is obtained: download the stream inside the recorder's page, or capture its playback.</summary>
public enum RecordingMethod
{
    /// <summary>Download; capture the playback when downloading does not work.</summary>
    Auto,
    Download,
    Capture,
}

public enum RecordingStage
{
    Opening,
    Preparing,
    Downloading,
    Seeking,
    Recording,
    Finishing,
}

public static class RecorderPaths
{
    /// <summary>The recorder's own browser profile, separate from the user's Edge; nobody ever logs in to it.</summary>
    public static string DefaultBrowserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarRailAutoBattle", "WebView2Recorder");
}

public sealed record RecordingProgress(RecordingStage Stage, double MediaTime, double Start, double End);

/// <param name="Start">Seconds into the video where recording starts.</param>
/// <param name="End">Seconds where it stops; null = end of the video.</param>
/// <param name="OutputDirectory">Receives recording.mp4 (downloaded) or recording.avi (captured), and recording_info.txt.</param>
/// <param name="BrowserDataFolder">The recorder's own browser profile (never the user's Edge profile; nobody logs in).</param>
public sealed record RecordingRequest(BilibiliLink Link, double Start, double? End, string OutputDirectory, string BrowserDataFolder)
{
    /// <summary>Frames per second of the output file; its timeline equals the video's (file time 0 = <see cref="Start"/>).</summary>
    public double OutputFps { get; init; } = 15;

    public int JpegQuality { get; init; } = 95;

    public RecordingMethod Method { get; init; } = RecordingMethod.Auto;
}

public sealed record RecordingResult
{
    public bool Ok => Failure is null;

    public RecordingFailure? Failure { get; init; }

    /// <summary>User-facing explanation (Simplified Chinese).</summary>
    public string Message { get; init; } = string.Empty;

    public string? VideoPath { get; init; }

    public string? InfoPath { get; init; }

    public string VideoId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public int Width { get; init; }

    public int Height { get; init; }

    public double RecordedStart { get; init; }

    public double RecordedEnd { get; init; }

    /// <summary>Video time of the file's time 0. A download starts on the key frame at or before the requested start.</summary>
    public double FileStart { get; init; }

    /// <summary>True when the stream was downloaded, false when the playback was captured.</summary>
    public bool Downloaded { get; init; }

    /// <summary>Whether the recorder's own browser profile was logged in to Bilibili.</summary>
    public bool SignedIn { get; init; }

    public static RecordingResult Fail(RecordingFailure failure, string message) => new() { Failure = failure, Message = message };
}

public static class RecordingMessages
{
    public const string UseOwnFile = "可以改用“自录视频文件”导入你自己录好的视频。";

    public static string For(RecordingFailure failure) => failure switch
    {
        RecordingFailure.RuntimeMissing => "这台电脑没有安装 Microsoft Edge WebView2 运行时，无法录制链接。" + UseOwnFile,
        RecordingFailure.NetworkError => "无法打开视频页面，请检查网络连接后重试。",
        RecordingFailure.NotBilibili => "链接跳转到了 B 站以外的页面，已停止。" + UseOwnFile,
        RecordingFailure.VideoUnavailable => "B 站提示这个视频不存在或不可观看。",
        RecordingFailure.Captcha => "B 站要求进行人机验证。程序不会绕过验证，已停止录制。" + UseOwnFile,
        RecordingFailure.LoginRequired => "B 站要求登录后才能继续播放，已停止录制。请在视频页点“登录 B 站…”登录后重试；" + UseOwnFile,
        RecordingFailure.PlayerNotFound => "页面里找不到视频播放器（页面结构可能已变化）。" + UseOwnFile,
        RecordingFailure.BlackScreen => "截取到的画面是黑屏（视频可能受版权保护）。程序不会绕过保护，已停止录制。" + UseOwnFile,
        RecordingFailure.Stalled => "视频长时间没有继续播放（可能是网络缓慢或被页面暂停），已停止录制。" + UseOwnFile,
        RecordingFailure.InvalidRange => "所选时间段超出了视频长度。",
        RecordingFailure.WriteFailed => "无法写入录制文件，请检查磁盘空间和文件夹权限。",
        RecordingFailure.Protected => "这个视频受版权保护（DRM 加密）。程序不会解密，已停止。" + UseOwnFile,
        RecordingFailure.DownloadFailed => "无法下载这个视频。" + UseOwnFile,
        _ => "录制已取消。",
    };
}
