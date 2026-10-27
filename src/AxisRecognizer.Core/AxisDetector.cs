using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>
/// Geometry of the action axis column, in game-height-relative units (see <see cref="RelRect"/>).
/// Main-axis cells are not on a fixed grid in the real game (the round counter bar is shorter than a cell,
/// cells slide during animations), so they are found by matching the cell frame outline.
/// </summary>
public sealed class AxisColumn
{
    /// <summary>Nominal rect of the current (top) cell; the detector searches around it.</summary>
    public required RelRect CurrentCell { get; init; }

    /// <summary>Size and left edge of a main-axis cell; Y is where the search for main cells starts.</summary>
    public required RelRect MainCell { get; init; }

    /// <summary>Bottom of the search area.</summary>
    public required double SearchBottom { get; init; }

    /// <summary>How far a cell may sit to the right of its nominal left edge (sliding / highlighted cells).</summary>
    public double MaxShiftRight { get; init; } = 0.016;

    public double MaxShiftLeft { get; init; } = 0.004;

    /// <summary>Radius of the semicircular notch on the left edge, relative to game height.</summary>
    public double NotchRadius { get; init; } = 0.0106;

    /// <summary>Radius of the rounded top-right corner, relative to game height.</summary>
    public double CornerRadius { get; init; } = 0.0106;

    /// <summary>Mean chamfer distance (pixels, at 1419 px game height) at or below which a frame counts as a cell.</summary>
    public double MaxFrameScore { get; init; } = 1.6;

    /// <summary>Minimum vertical distance between two main cells, in main-cell heights (real cells never overlap).</summary>
    public double MinCellGap { get; init; } = 1.1;

    /// <summary>
    /// Portrait region compared between cells, as fractions of the cell (X, Y, W, H). Both regions have the same
    /// aspect ratio so a unit seen in the current cell and in the main axis is framed alike.
    /// </summary>
    public RelRect MainFeature { get; init; } = new(0.16, 0, 0.62, 1);

    /// <summary>Parts of the main feature region to ignore (fractions of that region): the action-value box.</summary>
    public List<RelRect> MainIgnore { get; init; } = [new(0.66, 0.6, 0.34, 0.4)];

    public RelRect CurrentFeature { get; init; } = new(0.12, 0, 0.74, 1);

    public List<RelRect> CurrentIgnore { get; init; } = [];
}

public sealed record DetectedCell(SlotKind Kind, int Order, Rect Rect, double FrameScore);

public static class AxisDetector
{
    private const double ReferenceHeight = 1419;
    private const float DistanceCap = 8f;

    public static List<DetectedCell> Detect(Mat frame, Rect content, AxisColumn axis)
    {
        var h = content.Height;
        var scale = h / ReferenceHeight;
        var main = axis.MainCell.ToPixels(content);
        var current = axis.CurrentCell.ToPixels(content);

        var roiLeft = Math.Max(0, Math.Min(main.X, current.X) - (int)Math.Round(axis.MaxShiftLeft * h) - 4);
        var roiRight = Math.Min(frame.Width, Math.Max(main.Right, current.Right) + (int)Math.Round(axis.MaxShiftRight * h) + 4);
        var roiTop = Math.Max(0, current.Y - current.Height / 3);
        var roiBottom = Math.Min(frame.Height, content.Y + (int)Math.Round(axis.SearchBottom * h) + main.Height);
        var roi = new Rect(roiLeft, roiTop, roiRight - roiLeft, roiBottom - roiTop);

        using var distance = DistanceMap(frame, roi);
        var cells = new List<DetectedCell>();

        // Current cell: best frame in a small window around its nominal position.
        var currentTemplate = FrameTemplate(current.Width, current.Height, axis.NotchRadius * h, axis.CornerRadius * h);
        var currentBest = BestMatch(distance, roi, currentTemplate, current.Width, current.Height,
            current.X - (int)Math.Round(axis.MaxShiftLeft * h) - 3, current.X + (int)Math.Round(axis.MaxShiftRight * h) + 3,
            current.Y - current.Height / 4, current.Y + current.Height / 4);
        var currentRect = currentBest.Score / scale <= axis.MaxFrameScore
            ? new Rect(currentBest.X, currentBest.Y, current.Width, current.Height)
            : current;
        cells.Add(new DetectedCell(SlotKind.Current, 1, currentRect, currentBest.Score / scale));

        // Main cells: scan downwards, keep local minima of the frame score.
        var template = FrameTemplate(main.Width, main.Height, axis.NotchRadius * h, axis.CornerRadius * h);
        var xMin = main.X - (int)Math.Round(axis.MaxShiftLeft * h);
        var xMax = main.X + (int)Math.Round(axis.MaxShiftRight * h);
        var yStart = Math.Max(currentRect.Bottom + 1, content.Y + (int)Math.Round(axis.MainCell.Y * h) - main.Height / 2);
        var yEnd = Math.Min(roi.Bottom - main.Height, content.Y + (int)Math.Round(axis.SearchBottom * h));

        var rows = new List<(int Y, int X, double Score)>();
        for (var y = yStart; y <= yEnd; y++)
        {
            var best = BestMatch(distance, roi, template, main.Width, main.Height, xMin, xMax, y, y);
            rows.Add((y, best.X, best.Score / scale));
        }

        var minGap = (int)Math.Round(main.Height * axis.MinCellGap);
        var accepted = rows
            .Where(r => r.Score <= axis.MaxFrameScore)
            .OrderBy(r => r.Score)
            .Aggregate(new List<(int Y, int X, double Score)>(), (list, r) =>
            {
                if (list.All(a => Math.Abs(a.Y - r.Y) >= minGap))
                {
                    list.Add(r);
                }

                return list;
            })
            .OrderBy(r => r.Y)
            .ToList();

        var order = 2;
        foreach (var (y, x, score) in accepted)
        {
            cells.Add(new DetectedCell(SlotKind.Main, order++, new Rect(x, y, main.Width, main.Height), score));
        }

        return cells;
    }

    /// <summary>Per-row best frame scores, for calibration and diagnostics.</summary>
    public static List<(int Y, int X, double Score)> ScoreRows(Mat frame, Rect content, AxisColumn axis)
    {
        var h = content.Height;
        var scale = h / ReferenceHeight;
        var main = axis.MainCell.ToPixels(content);
        var roi = new Rect(Math.Max(0, main.X - 20), content.Y, Math.Min(frame.Width - Math.Max(0, main.X - 20), main.Width + 60), Math.Min(content.Height, (int)(axis.SearchBottom * h) + main.Height));
        using var distance = DistanceMap(frame, roi);
        var template = FrameTemplate(main.Width, main.Height, axis.NotchRadius * h, axis.CornerRadius * h);
        var rows = new List<(int, int, double)>();
        for (var y = roi.Y; y <= roi.Bottom - main.Height - 1; y++)
        {
            var best = BestMatch(distance, roi, template, main.Width, main.Height,
                main.X - (int)Math.Round(axis.MaxShiftLeft * h), main.X + (int)Math.Round(axis.MaxShiftRight * h), y, y);
            rows.Add((y, best.X, best.Score / scale));
        }

        return rows;
    }

    private static Mat DistanceMap(Mat frame, Rect roi)
    {
        using var region = new Mat(frame, roi);
        using var gray = new Mat();
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 50, 130);
        using var inverted = new Mat();
        Cv2.BitwiseNot(edges, inverted);
        var distance = new Mat();
        Cv2.DistanceTransform(inverted, distance, DistanceTypes.L2, DistanceTransformMasks.Mask3);
        return distance;
    }

    /// <summary>Points on the cell outline: top/bottom/right edges, left edge with a semicircular notch, rounded top-right corner.</summary>
    internal static List<Point> FrameTemplate(int w, int h, double notchRadius, double cornerRadius)
    {
        var points = new List<Point>();
        var cy = h / 2.0;
        var nr = Math.Max(3, notchRadius);
        var cr = Math.Max(2, cornerRadius);
        const int step = 2;

        for (var x = 0; x < w - cr; x += step)
        {
            points.Add(new Point(x, 0));
        }

        for (var x = 0; x < w; x += step)
        {
            points.Add(new Point(x, h - 1));
        }

        for (var y = (int)cr; y < h; y += step)
        {
            points.Add(new Point(w - 1, y));
        }

        for (var y = 0; y < h; y += step)
        {
            if (Math.Abs(y - cy) > nr + 1)
            {
                points.Add(new Point(0, y));
            }
        }

        for (var a = -90; a <= 90; a += 10)
        {
            var rad = a * Math.PI / 180;
            points.Add(new Point((int)Math.Round(nr * Math.Cos(rad)), (int)Math.Round(cy + nr * Math.Sin(rad))));
        }

        for (var a = 0; a <= 90; a += 15)
        {
            var rad = a * Math.PI / 180;
            points.Add(new Point((int)Math.Round(w - 1 - cr + cr * Math.Sin(rad)), (int)Math.Round(cr - cr * Math.Cos(rad))));
        }

        return points;
    }

    private static (int X, int Y, double Score) BestMatch(Mat distance, Rect roi, List<Point> template, int w, int h, int xMin, int xMax, int yMin, int yMax)
    {
        var best = (X: xMin, Y: yMin, Score: double.MaxValue);
        for (var y = yMin; y <= yMax; y++)
        {
            for (var x = xMin; x <= xMax; x++)
            {
                var ox = x - roi.X;
                var oy = y - roi.Y;
                if (ox < 0 || oy < 0 || ox + w > roi.Width || oy + h > roi.Height)
                {
                    continue;
                }

                double sum = 0;
                foreach (var p in template)
                {
                    sum += Math.Min(DistanceCap, distance.At<float>(oy + p.Y, ox + p.X));
                }

                var score = sum / template.Count;
                if (score < best.Score)
                {
                    best = (x, y, score);
                }
            }
        }

        return best;
    }
}
