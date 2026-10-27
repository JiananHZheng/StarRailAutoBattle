using System.Globalization;
using System.Text;

namespace AxisRecognizer;

/// <summary>Where the local data lives: <c>&lt;repo&gt;/axis-data</c> by default (KB location for end users is decided later).</summary>
public static class AxisData
{
    public const string EnvironmentVariable = "AXIS_DATA";

    /// <summary>Walks up from <paramref name="startDirectory"/> to the repository root; falls back to a folder next to it.</summary>
    public static string DefaultRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StarRailAutoBattle.slnx")))
            {
                return Path.Combine(dir.FullName, "axis-data");
            }
        }

        return Path.Combine(startDirectory, "axis-data");
    }

    public static string Resolve(string startDirectory) =>
        Path.GetFullPath(Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnv ? fromEnv : DefaultRoot(startDirectory));

    public static string KbRoot(string dataRoot) => Path.Combine(dataRoot, "kb");
}

public sealed record VideoRecognitionResult(
    string Text,
    /// <summary>TSV rows (no header) in the raw_detail.tsv format.</summary>
    string Detail,
    int Frames,
    int AxisFrames,
    /// <summary>Median height in pixels of the game picture (black bars removed); 0 when no frame was read.</summary>
    int ContentHeight,
    bool LowResolution);

/// <summary>Runs the existing per-frame recognizer and sequence analyzer over one local video file.</summary>
public static class VideoRecognition
{
    /// <summary>
    /// Provisional: below this game-picture height the result is flagged as low confidence. The real threshold
    /// comes from the planned high/low resolution comparison on non-test clips.
    /// </summary>
    public const int ProvisionalMinContentHeight = 720;

    public const string DetailHeader = "frame\tslot\tx\ty\tw\th\tempty\tunit\tscore\tcandidates\n";

    public static VideoRecognitionResult Run(
        KnowledgeBase kb,
        Recognizer recognizer,
        string videoPath,
        double every,
        double start,
        double? end,
        string sourceLabel,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var (_, seconds, _, _) = VideoFrames.Probe(videoPath);
        var last = end is { } e && (seconds <= 0 || e < seconds) ? e : seconds;
        var expected = Math.Max(1, (int)Math.Ceiling((last - start) / every));

        var detail = new StringBuilder();
        var frames = new List<(double, FrameResult)>();
        var heights = new List<int>();
        foreach (var (time, mat) in VideoFrames.Sample(videoPath, every, start, end))
        {
            using (mat)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var r = recognizer.Recognize(mat);
                detail.Append(ResultText.DetailRows($"{Path.GetFileName(videoPath)}@{time.ToString("0.000", CultureInfo.InvariantCulture)}", r));
                frames.Add((time, r));
                heights.Add(r.Content.Height);
                progress?.Report(Math.Min(1, frames.Count / (double)expected));
            }
        }

        heights.Sort();
        var contentHeight = heights.Count > 0 ? heights[heights.Count / 2] : 0;
        var lowResolution = contentHeight is > 0 and < ProvisionalMinContentHeight;

        var text = new StringBuilder();
        if (lowResolution)
        {
            text.AppendLine(LowResolutionNote(contentHeight));
            text.AppendLine();
        }

        var analyzer = SequenceAnalyzer.ForInterval(kb, every);
        text.AppendLine(analyzer.ToText(analyzer.Analyze(frames), sourceLabel, SamplingText(every)));
        return new VideoRecognitionResult(text.ToString(), detail.ToString(), frames.Count, frames.Count(f => f.Item2.AxisFound), contentHeight, lowResolution);
    }

    /// <summary>Default sampling for videos: 15 frames per second of video.</summary>
    public const double DefaultEvery = 1.0 / 15;

    public static string SamplingText(double every) =>
        string.Create(CultureInfo.InvariantCulture, $"每 {every:0.###} 秒取一帧（约 {1 / every:0.#} 帧/秒）");

    public static string LowResolutionNote(int contentHeight) =>
        $"注意：画面清晰度不足（游戏画面高 {contentHeight} 像素，暂定门槛 {ProvisionalMinContentHeight}），识别结果可信度低。建议改用更清晰的自录视频文件。";
}

/// <summary>Files of one launcher video run: the raw result stays read-only, edits go to a separate copy.</summary>
public static class VideoRunFiles
{
    public const string ResultFile = "result.txt";
    public const string DetailFile = "result_detail.tsv";
    public const string EditFile = "result_edit.txt";
    public const string InfoFile = "run_info.txt";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static string CreateRunDirectory(string dataRoot, string label, DateTime now)
    {
        var safe = new string(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray());
        safe = safe.Length == 0 ? "video" : safe[..Math.Min(safe.Length, 40)];
        var baseDir = Path.Combine(dataRoot, "video-runs", $"{now:yyyyMMdd-HHmmss}_{safe}");
        var dir = baseDir;
        for (var i = 2; Directory.Exists(dir); i++)
        {
            dir = $"{baseDir}_{i}";
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void WriteInfo(string runDir, string info) => File.WriteAllText(Path.Combine(runDir, InfoFile), info, Utf8);

    /// <summary>Writes the tool's first output and marks it read-only; it is never rewritten.</summary>
    public static string WriteResult(string runDir, VideoRecognitionResult result)
    {
        var resultPath = Path.Combine(runDir, ResultFile);
        var detailPath = Path.Combine(runDir, DetailFile);
        File.WriteAllText(resultPath, result.Text, Utf8);
        File.WriteAllText(detailPath, VideoRecognition.DetailHeader + result.Detail, Utf8);
        File.SetAttributes(resultPath, FileAttributes.ReadOnly);
        File.SetAttributes(detailPath, FileAttributes.ReadOnly);
        return resultPath;
    }

    /// <summary>Returns the editable copy, creating it from the raw result the first time.</summary>
    public static string EnsureEditCopy(string runDir)
    {
        var editPath = Path.Combine(runDir, EditFile);
        if (!File.Exists(editPath))
        {
            File.Copy(Path.Combine(runDir, ResultFile), editPath);
            File.SetAttributes(editPath, File.GetAttributes(editPath) & ~FileAttributes.ReadOnly);
        }

        return editPath;
    }
}
