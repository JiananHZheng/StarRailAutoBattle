using System.Globalization;
using System.Text;
using OpenCvSharp;

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
    bool LowResolution)
{
    /// <summary>Avatars the KB did not know, grouped under temporary ids (新01…).</summary>
    public IReadOnlyList<NewUnitGroup> NewUnits { get; init; } = [];
}

/// <summary>Runs the existing per-frame recognizer and sequence analyzer over one local video file.</summary>
public static class VideoRecognition
{
    /// <summary>
    /// Provisional: below this game-picture height the result is flagged as low confidence. The real threshold
    /// comes from the planned high/low resolution comparison on non-test clips.
    /// </summary>
    public const int ProvisionalMinContentHeight = 720;

    /// <summary>
    /// Main-axis cells whose outline matched the axis frame worse than this are not grouped as new units (they stay
    /// unknown). Measured on a checked run (2026-10-27): main cells covered by the video's caption text scored a median
    /// 1.10, normal main cells 0.62 (75 % at most 0.71, 95 % at most about 1.2). The larger current cell scores
    /// differently and is not filtered (applying this to it left most actions unknown).
    /// </summary>
    public const double MaxGroupingFrameScore = 0.9;

    public const string DetailHeader ="frame\tslot\tx\ty\tw\th\tempty\tunit\tscore\tcandidates\n";

    public static VideoRecognitionResult Run(
        KnowledgeBase kb,
        Recognizer recognizer,
        string videoPath,
        double every,
        double start,
        double? end,
        string sourceLabel,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        string? runDirectory = null,
        double timeOffset = 0)
    {
        // timeOffset: video time of the file's time 0 (a recording that starts mid-video). Results are written in video
        // time; raw_detail.tsv keeps file time, which corrections use to find frames.
        var (_, seconds, _, _) = VideoFrames.Probe(videoPath);
        var last = end is { } e && (seconds <= 0 || e < seconds) ? e : seconds;
        var expected = Math.Max(1, (int)Math.Ceiling((last - start) / every));
        var newUnitsDir = runDirectory is null ? null : Path.Combine(runDirectory, NewUnitFiles.Folder);

        var clusterer = new NewUnitClusterer(kb.Settings);
        var assigned = new List<(int Frame, int Slot, NewUnitGroup Group)>();
        var savedFrames = new Dictionary<double, string>();
        var frames = new List<(double Time, FrameResult Result)>();
        var heights = new List<int>();
        foreach (var (time, mat) in VideoFrames.Sample(videoPath, every, start, end))
        {
            using (mat)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var r = recognizer.Recognize(mat);
                for (var i = 0; i < r.Slots.Count && r.AxisFound && r.Problem is null; i++)
                {
                    var slot = r.Slots[i];
                    if (slot.IsEmpty || slot.UnitId is not null || slot.Feature is null
                        || (slot.Kind == SlotKind.Main && slot.FrameScore > MaxGroupingFrameScore))
                    {
                        continue;
                    }

                    var group = clusterer.Assign(slot.Feature, time + timeOffset, slot.Slot, slot.Candidates.FirstOrDefault(), out var wantExample);
                    assigned.Add((frames.Count, i, group));
                    if (wantExample && newUnitsDir is not null)
                    {
                        if (!savedFrames.TryGetValue(time, out var file))
                        {
                            file = Path.Combine("frames", FormattableString.Invariant($"t{time:0000.000}.jpg"));
                            ImageIO.WriteJpeg(Path.Combine(newUnitsDir, file), mat);
                            savedFrames[time] = file;
                        }

                        clusterer.AddExample(group, new NewUnitExample(time + timeOffset, slot.Slot, slot.FeatureRect, file));
                    }
                }

                // Features are only needed for grouping; dropping them keeps memory flat on long clips.
                frames.Add((time, r with { Slots = r.Slots.Select(s => s with { Feature = null }).ToList() }));
                heights.Add(r.Content.Height);
                progress?.Report(Math.Min(1, frames.Count / (double)expected));
            }
        }

        // Flicker evidence: between two new groups it merges them; between a new group and a known unit it is only
        // noted (疑似), since the known unit's samples may themselves be wrong.
        var tokens = assigned.Select(x => (x.Frame, frames[x.Frame].Result.Slots[x.Slot].Slot, (object)x.Group))
            .Concat(frames.SelectMany((f, i) => f.Result.Slots
                .Where(s => s.UnitId is not null && s.UnitId != KnowledgeBase.EmptyUnitId)
                .Select(s => (i, s.Slot, (object)s.UnitId!))));
        var pairs = FlickerPairs(tokens);
        foreach (var (a, b, _) in pairs.Where(p => p.A is NewUnitGroup && p.B is NewUnitGroup))
        {
            clusterer.Link((NewUnitGroup)a, (NewUnitGroup)b);
        }

        foreach (var (a, b, times) in pairs.Where(p => p.A is NewUnitGroup != p.B is NewUnitGroup))
        {
            var (group, known) = a is NewUnitGroup g ? (g, (string)b) : ((NewUnitGroup)b, (string)a);
            clusterer.NoteKnownFlicker(group, known, times);
        }

        var newUnits = clusterer.Finish();
        foreach (var (frameIndex, slotIndex, group) in assigned.Where(a => a.Group.Root.TempId.Length > 0))
        {
            var (time, result) = frames[frameIndex];
            var slots = result.Slots.ToList();
            slots[slotIndex] = slots[slotIndex] with { UnitId = group.Root.TempId };
            frames[frameIndex] = (time, result with { Slots = slots });
        }

        LinkCurrentCellGroups(frames, newUnits);

        var detail = new StringBuilder();
        foreach (var (time, result) in frames)
        {
            detail.Append(ResultText.DetailRows($"{Path.GetFileName(videoPath)}@{time.ToString("0.000", CultureInfo.InvariantCulture)}", result));
        }

        if (newUnitsDir is not null)
        {
            WriteNewUnits(newUnitsDir, newUnits, savedFrames.Values);
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

        if (timeOffset != 0)
        {
            text.AppendLine(FormattableString.Invariant($"时间为视频时间（录像文件从视频 {TimeSpan.FromSeconds(timeOffset):mm\\:ss\\.f} 开始；逐格明细 raw_detail.tsv 用的是文件时间）。"));
            text.AppendLine();
        }

        var analyzer = SequenceAnalyzer.ForInterval(kb, every);
        var videoTimeFrames = frames.Select(f => (f.Time + timeOffset, f.Result)).ToList();
        text.AppendLine(analyzer.ToText(analyzer.Analyze(videoTimeFrames), sourceLabel, SamplingText(every)));
        text.AppendLine(NewUnitFiles.Summary(kb, newUnits, clusterer.Dropped, newUnitsDir is not null));
        return new VideoRecognitionResult(text.ToString(), detail.ToString(), frames.Count, frames.Count(f => f.Result.AxisFound), contentHeight, lowResolution)
        {
            NewUnits = newUnits,
        };
    }

    /// <summary>
    /// Pairs of labels (new groups or known unit ids) that the same cell flickers between: A in one sampled frame, B for
    /// at most <paramref name="maxRun"/> frames, then A again. Units do not hop out of a cell and straight back, so this
    /// is an effect or animation briefly changing how one unit looks. A pair needs <paramref name="minTimes"/> flickers.
    /// </summary>
    internal static List<(object A, object B, int Times)> FlickerPairs(
        IEnumerable<(int Frame, string Slot, object Label)> cells, int maxRun = 8, int minTimes = 2)
    {
        var counts = new Dictionary<(object, object), int>();
        foreach (var slot in cells.GroupBy(c => c.Slot))
        {
            var byFrame = slot.GroupBy(c => c.Frame).ToDictionary(c => c.Key, c => c.First().Label);
            foreach (var (frame, a) in byFrame)
            {
                // Start of a run of something else right after a.
                if (!byFrame.TryGetValue(frame + 1, out var b) || b.Equals(a))
                {
                    continue;
                }

                for (var k = 1; k <= maxRun && byFrame.TryGetValue(frame + k, out var g); k++)
                {
                    if (g.Equals(a))
                    {
                        var key = Order(a, b);
                        counts[key] = counts.GetValueOrDefault(key) + 1;
                        break;
                    }

                    if (!g.Equals(b))
                    {
                        break;
                    }
                }
            }
        }

        return counts.Where(kv => kv.Value >= minTimes).Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();

        // One key per unordered pair: groups by creation order, ids by text, groups before ids.
        static (object, object) Order(object x, object y) => (x, y) switch
        {
            (NewUnitGroup gx, NewUnitGroup gy) => gx.Key <= gy.Key ? (x, y) : (y, x),
            (NewUnitGroup, _) => (x, y),
            (_, NewUnitGroup) => (y, x),
            _ => string.CompareOrdinal((string)x, (string)y) <= 0 ? (x, y) : (y, x),
        };
    }

    /// <summary>
    /// For each new group seen in the current cell, counts which unit sat in main-axis cell 2 just before the group
    /// entered the current cell. A unit normally slides from main cell 2 into the current cell when it acts, so a clear
    /// majority suggests the two are one unit shown in two framings. Entries count only when the current cell then
    /// holds the group for at least 5 sampled frames, so animation flicker does not vote.
    /// </summary>
    internal static void LinkCurrentCellGroups(IReadOnlyList<(double Time, FrameResult Result)> frames, IReadOnlyList<NewUnitGroup> groups)
    {
        const int MinRun = 5;
        var byId = groups.ToDictionary(g => g.TempId);
        var rows = frames.Where(f => f.Result.AxisFound)
            .Select(f => (Current: f.Result.Slots.FirstOrDefault(s => s.Kind == SlotKind.Current)?.UnitId,
                          Main2: f.Result.MainAxis.FirstOrDefault(s => s.Kind == SlotKind.Main)?.UnitId))
            .ToList();
        var votes = new Dictionary<string, Dictionary<string, int>>();
        var entries = new Dictionary<string, int>();
        for (var i = 1; i < rows.Count; i++)
        {
            var id = rows[i].Current;
            if (id is null || !byId.ContainsKey(id) || rows[i - 1].Current == id)
            {
                continue;
            }

            var run = 0;
            while (i + run < rows.Count && rows[i + run].Current == id)
            {
                run++;
            }

            if (run < MinRun)
            {
                continue;
            }

            entries[id] = entries.GetValueOrDefault(id) + 1;
            if (rows[i - 1].Main2 is { } before && before != id)
            {
                var tally = votes.TryGetValue(id, out var t) ? t : votes[id] = [];
                tally[before] = tally.GetValueOrDefault(before) + 1;
            }
        }

        foreach (var (id, tally) in votes)
        {
            var (from, count) = tally.MaxBy(kv => kv.Value);
            if (count >= 2 && count * 2 >= entries[id])
            {
                byId[id].EnteredFrom = (from, count, entries[id]);
            }
        }
    }

    /// <summary>
    /// Writes each kept group's example avatars ({temp id}_1.png…) and new_units.tsv, and removes saved frames that
    /// only noise groups used. The table is read-only like the raw result; kept frames stay for axis kb promote.
    /// </summary>
    private static void WriteNewUnits(string dir, IReadOnlyList<NewUnitGroup> groups, IEnumerable<string> savedFrames)
    {
        Directory.CreateDirectory(dir);
        foreach (var group in groups)
        {
            for (var i = 0; i < group.Examples.Count; i++)
            {
                var example = group.Examples[i];
                using var frame = ImageIO.Read(Path.Combine(dir, example.FrameFile));
                using var crop = new Mat(frame, Geometry.Clamp(example.Rect, frame.Width, frame.Height));
                ImageIO.WritePng(Path.Combine(dir, $"{group.TempId}_{i + 1}.png"), crop);
            }
        }

        var used = groups.SelectMany(g => g.Examples).Select(e => e.FrameFile).ToHashSet();
        foreach (var file in savedFrames.Where(f => !used.Contains(f)))
        {
            File.Delete(Path.Combine(dir, file));
        }

        var table = Path.Combine(dir, NewUnitFiles.TableFile);
        File.WriteAllText(table, NewUnitFiles.Table(groups), new UTF8Encoding(false));
        File.SetAttributes(table, FileAttributes.ReadOnly);
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
