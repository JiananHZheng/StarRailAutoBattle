using OpenCvSharp;

namespace AxisRecognizer;

public static class KbLabeler
{
    /// <summary>
    /// Records the correct unit for each labeled slot of a screenshot. On train screenshots the slot is also cropped
    /// and stored as a sample, unless it is a near-duplicate of an existing sample of the same unit.
    /// Holdout screenshots only get ground truth. The caller commits the KB.
    /// </summary>
    public static List<string> LabelFrame(
        KnowledgeBase kb,
        ScreenshotRecord shot,
        Mat frame,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, Rect> pixelRects,
        string labelSource,
        string status,
        string? note)
    {
        var content = Geometry.DetectContent(frame);
        var layout = kb.LayoutFor(content.Width / (double)content.Height);
        var cells = layout is null ? [] : CellLocator.Locate(frame, content, layout);
        var details = new List<string>();

        foreach (var (slotName, unitText) in labels)
        {
            var unit = kb.ResolveUnit(unitText);
            shot.Truth[slotName] = unit.Id;
            if (shot.Split == "holdout")
            {
                details.Add($"{shot.Id}/{slotName} = {unit.Id}（留出截图：只记录正确答案，不生成样本）");
                continue;
            }

            var cell = cells.FirstOrDefault(c => c.Name.Equals(slotName, StringComparison.OrdinalIgnoreCase));
            var (kind, _) = SlotNames.Parse(slotName);
            Rect rect;
            if (pixelRects.TryGetValue(slotName, out var given))
            {
                rect = given;
            }
            else
            {
                rect = cell?.Rect ?? throw new InvalidOperationException(
                    $"{shot.Id} 没有可用布局，或没有检测到格 {slotName}（检测到：{string.Join("、", cells.Select(c => c.Name))}）；请用 --rect 指定像素矩形。");
            }

            // Portrait region and ignored areas follow the cell kind, so samples and live cells are compared alike.
            var axis = layout?.Axis;
            var featureRegion = cell?.Feature ?? (axis is null ? null : kind == SlotKind.Current ? axis.CurrentFeature : kind == SlotKind.Main ? axis.MainFeature : null);
            var ignore = cell?.Ignore ?? (axis is null ? null : kind == SlotKind.Current ? axis.CurrentIgnore : kind == SlotKind.Main ? axis.MainIgnore : null);

            rect = Geometry.Clamp(rect, frame.Width, frame.Height);
            using var crop = new Mat(frame, rect);
            var feature = CellLocator.Feature(crop, featureRegion, ignore, kb.Settings);
            var duplicate = kb.Samples
                .Where(s => s.UnitId == unit.Id)
                .Select(s =>
                {
                    using var other = kb.ReadSample(s);
                    return (s.Id, Score: feature.Similarity(CellLocator.Feature(other, s.Feature, s.Ignore, kb.Settings), kb.Settings));
                })
                .Where(x => x.Score >= kb.Settings.DuplicateScore)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (duplicate.Id is not null)
            {
                details.Add($"{shot.Id}/{slotName} = {unit.Id}：与样本 {duplicate.Id} 几乎相同（{duplicate.Score:0.000}），未重复保存");
                continue;
            }

            var sample = kb.AddSample(unit.Id, shot, slotName, RelRect.FromPixels(rect, content), crop, labelSource, status, note, featureRegion, ignore?.ToList());
            details.Add($"{shot.Id}/{slotName} = {unit.Id}：新增样本 {sample.Id}（标注来源 {labelSource}，{status}）");
        }

        return details;
    }
}
