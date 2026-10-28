using System.Diagnostics;
using OpenCvSharp;

namespace AxisRecognizer;

public sealed record Candidate(string UnitId, double Score);

public sealed record SlotResult(
    string Slot,
    SlotKind Kind,
    int Order,
    Rect PixelRect,
    bool IsEmpty,
    /// <summary>Confident answer, or null when the tool is not sure.</summary>
    string? UnitId,
    double Score,
    IReadOnlyList<Candidate> Candidates)
{
    /// <summary>
    /// Feature of the detected cell itself (<see cref="FeatureRect"/>), used to group avatars the KB does not know.
    /// Not the refined crop: for an unknown avatar the refinement follows whichever KB sample happens to score best,
    /// so the same unit would be cropped differently from frame to frame.
    /// </summary>
    public AvatarFeature? Feature { get; init; }

    public Rect FeatureRect { get; init; }

    /// <summary>How well the cell's outline matched the action-axis frame (chamfer distance; lower is better, 0 when fixed).</summary>
    public double FrameScore { get; init; }
}

public sealed record FrameResult(
    int Width,
    int Height,
    Rect Content,
    string? LayoutId,
    /// <summary>Set when the frame could not be processed at all (e.g. no calibrated layout for this aspect ratio).</summary>
    string? Problem,
    IReadOnlyList<SlotResult> Slots,
    double ElapsedMs,
    /// <summary>
    /// False when the action axis is not on screen (loading, battle-start animation, menus): the current cell's
    /// frame did not match or fewer than two main cells were found. Always true for fixed-slot layouts.
    /// </summary>
    bool AxisFound = true)
{
    public IEnumerable<SlotResult> MainAxis => Slots.Where(s => s.Kind is SlotKind.Current or SlotKind.Main).OrderBy(s => s.Order);
    public IEnumerable<SlotResult> InsertSlots => Slots.Where(s => s.Kind == SlotKind.Insert).OrderBy(s => s.Order);
}

/// <summary>
/// Recognizes the action axis in one game frame using only the knowledge base (no model calls).
/// This is the entry point for a future automatic screenshot pipeline: pass any BGR frame of the game.
/// </summary>
public sealed class Recognizer
{
    private readonly KnowledgeBase _kb;
    private readonly List<(string UnitId, AvatarFeature Feature)> _index = [];

    public Recognizer(KnowledgeBase kb)
    {
        _kb = kb;
        foreach (var sample in kb.Samples)
        {
            using var crop = kb.ReadSample(sample);
            _index.Add((sample.UnitId, CellLocator.Feature(crop, sample.Feature, sample.Ignore, kb.Settings)));
        }
    }

    public int IndexedSamples => _index.Count;

    public FrameResult Recognize(Mat frame)
    {
        var watch = Stopwatch.StartNew();
        var content = Geometry.DetectContent(frame);
        var aspect = content.Width / (double)content.Height;
        var layout = _kb.LayoutFor(aspect);
        if (layout is null)
        {
            return new FrameResult(frame.Width, frame.Height, content, null,
                $"没有适用于宽高比 {aspect:0.000} 的行动轴布局，需要先用 axis layout calibrate 标定。", [], watch.Elapsed.TotalMilliseconds);
        }

        var cells = CellLocator.Locate(frame, content, layout);
        var axisFound = layout.Axis is not { } axis
            || (cells.Any(c => c.Kind == SlotKind.Current && c.FrameScore <= axis.MaxFrameScore)
                && cells.Count(c => c.Kind == SlotKind.Main) >= 2);
        var slots = cells.Select(cell => RecognizeCell(frame, cell)).ToList();
        return new FrameResult(frame.Width, frame.Height, content, layout.Id, null, slots, watch.Elapsed.TotalMilliseconds, axisFound);
    }

    private SlotResult RecognizeCell(Mat frame, CellSpec cell)
    {
        var settings = _kb.Settings;
        (Rect Rect, List<Candidate> Ranked, AvatarFeature Feature)? best = null;
        (Rect Rect, AvatarFeature Feature)? detected = null;

        foreach (var rect in SearchRects(cell.Rect, settings, refine: cell.Refine && _index.Count > 0))
        {
            var clamped = Geometry.Clamp(rect, frame.Width, frame.Height);
            using var crop = new Mat(frame, clamped);
            var feature = CellLocator.Feature(crop, cell.Feature, cell.Ignore, settings);
            detected ??= (clamped, feature);   // the first rect is the detected cell itself
            var ranked = Rank(feature);
            var top = ranked.Count > 0 ? ranked[0].Score : 0;
            if (best is null || top > (best.Value.Ranked.Count > 0 ? best.Value.Ranked[0].Score : 0))
            {
                best = (clamped, ranked, feature);
            }
        }

        var (chosenRect, candidates, chosenFeature) = best!.Value;
        var top1 = candidates.Count > 0 ? candidates[0] : null;
        var top2Score = candidates.Count > 1 ? candidates[1].Score : 0;
        var confident = top1 is not null && top1.Score >= settings.AcceptScore && top1.Score - top2Score >= settings.AcceptMargin;

        bool isEmpty;
        if (_index.Any(i => i.UnitId == KnowledgeBase.EmptyUnitId))
        {
            isEmpty = confident && top1!.UnitId == KnowledgeBase.EmptyUnitId;
        }
        else
        {
            isEmpty = chosenFeature.GrayStd < settings.EmptyStdFallback;
        }

        if (!isEmpty && settings.EmptyBelowScore > 0)
        {
            var bestUnit = candidates.FirstOrDefault(c => c.UnitId != KnowledgeBase.EmptyUnitId)?.Score ?? 0;
            isEmpty = bestUnit < settings.EmptyBelowScore;
        }

        var listed = candidates
            .Where(c => c.Score >= settings.CandidateScore && c.UnitId != KnowledgeBase.EmptyUnitId)
            .Take(3)
            .ToList();
        return new SlotResult(
            cell.Name,
            cell.Kind,
            cell.Order,
            chosenRect,
            isEmpty,
            !isEmpty && confident ? top1!.UnitId : null,
            top1?.Score ?? 0,
            listed)
        {
            Feature = detected!.Value.Feature,
            FeatureRect = detected.Value.Rect,
            FrameScore = cell.FrameScore ?? 0,
        };
    }

    /// <summary>Best score per unit, highest first.</summary>
    internal List<Candidate> Rank(AvatarFeature feature)
    {
        var bestPerUnit = new Dictionary<string, double>();
        foreach (var (unitId, sampleFeature) in _index)
        {
            var score = feature.Similarity(sampleFeature, _kb.Settings);
            if (!bestPerUnit.TryGetValue(unitId, out var current) || score > current)
            {
                bestPerUnit[unitId] = score;
            }
        }

        return bestPerUnit.Select(kv => new Candidate(kv.Key, kv.Value)).OrderByDescending(c => c.Score).ToList();
    }

    private static IEnumerable<Rect> SearchRects(Rect baseRect, RecognitionSettings settings, bool refine)
    {
        yield return baseRect;
        if (!refine)
        {
            yield break;
        }

        foreach (var scale in new[] { 1 - settings.RefineScale, 1.0, 1 + settings.RefineScale })
        {
            var w = (int)Math.Round(baseRect.Width * scale);
            var h = (int)Math.Round(baseRect.Height * scale);
            var cx = baseRect.X + baseRect.Width / 2.0;
            var cy = baseRect.Y + baseRect.Height / 2.0;
            foreach (var dy in new[] { -1, 0, 1 })
            {
                foreach (var dx in new[] { -1, 0, 1 })
                {
                    if (dx == 0 && dy == 0 && scale == 1.0)
                    {
                        continue;
                    }

                    var ox = dx * settings.RefineOffset * baseRect.Width;
                    var oy = dy * settings.RefineOffset * baseRect.Height;
                    yield return new Rect((int)Math.Round(cx + ox - w / 2.0), (int)Math.Round(cy + oy - h / 2.0), w, h);
                }
            }
        }
    }
}
