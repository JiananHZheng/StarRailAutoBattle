using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>One action-axis cell to read: where it is and which part of it is the portrait.</summary>
public sealed record CellSpec(
    string Name,
    SlotKind Kind,
    int Order,
    Rect Rect,
    /// <summary>Portrait region as fractions of the cell; null = whole cell.</summary>
    RelRect? Feature,
    /// <summary>Areas of the portrait region left out of comparison.</summary>
    IReadOnlyList<RelRect>? Ignore,
    /// <summary>Search around the nominal rect (fixed slots); detected cells are already precise.</summary>
    bool Refine,
    double? FrameScore);

public static class CellLocator
{
    public static List<CellSpec> Locate(Mat frame, Rect content, LayoutProfile layout)
    {
        var cells = new List<CellSpec>();
        if (layout.Axis is { } axis)
        {
            foreach (var d in AxisDetector.Detect(frame, content, axis))
            {
                var current = d.Kind == SlotKind.Current;
                cells.Add(new CellSpec(
                    current ? "current" : $"main{d.Order}",
                    d.Kind,
                    d.Order,
                    d.Rect,
                    current ? axis.CurrentFeature : axis.MainFeature,
                    current ? axis.CurrentIgnore : axis.MainIgnore,
                    false,
                    d.FrameScore));
            }

            cells.AddRange(layout.Slots
                .Where(s => s.Kind == SlotKind.Insert)
                .Select(s => new CellSpec(s.Name, s.Kind, s.Order, s.Rect.ToPixels(content), null, null, true, null)));
        }
        else
        {
            cells.AddRange(layout.Slots
                .OrderBy(s => s.Kind).ThenBy(s => s.Order)
                .Select(s => new CellSpec(s.Name, s.Kind, s.Order, s.Rect.ToPixels(content), null, null, true, null)));
        }

        return cells;
    }

    /// <summary>Feature for a cell crop, honouring its portrait region and ignored areas.</summary>
    public static AvatarFeature Feature(Mat cellCrop, RelRect? feature, IReadOnlyList<RelRect>? ignore, RecognitionSettings settings)
    {
        if (feature is null)
        {
            return AvatarFeature.Compute(cellCrop, settings, ignore);
        }

        var rect = Geometry.Clamp(
            new Rect(
                (int)Math.Round(feature.X * cellCrop.Width),
                (int)Math.Round(feature.Y * cellCrop.Height),
                (int)Math.Round(feature.W * cellCrop.Width),
                (int)Math.Round(feature.H * cellCrop.Height)),
            cellCrop.Width,
            cellCrop.Height);
        using var region = new Mat(cellCrop, rect);
        return AvatarFeature.Compute(region, settings, ignore);
    }
}
