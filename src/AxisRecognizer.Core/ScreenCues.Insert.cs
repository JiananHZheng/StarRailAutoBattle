using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>
/// The ultimate's insert card: a white-outlined portrait with an arched top, just right of the current cell, as tall
/// as it. An ultimate pressed during another unit's action can be released straight away: then it only shows as this
/// card (about a second) and the cut-in, never in the current cell.
/// </summary>
public sealed partial class CueReader
{
    /// <summary>Card width, relative to game height (measured on 铁墓 0:18.7: 51 px at 1080p).</summary>
    public const double InsertCardWidth = 0.047;

    /// <summary>How far right of the current cell the card may start (measured: about 8 px at 1080p).</summary>
    public const double InsertCardGap = 0.025;

    /// <summary>Mean chamfer distance (pixels at 1419 px game height, as for axis cells) at or below which the outline is a card.</summary>
    public const double InsertCardMaxScore = 1.6;

    private const double ReferenceHeight = 1419;

    /// <summary>All insert cards in a row right of the current cell (several ultimates can wait), left to right.</summary>
    public static List<Rect> FindInsertCards(Mat frame, Rect content, Rect currentCell)
    {
        var cards = new List<Rect>();
        var anchor = currentCell;
        while (cards.Count < 4 && FindInsertCard(frame, content, anchor) is { } card)
        {
            cards.Add(card);
            anchor = card;
        }

        return cards;
    }

    /// <summary>The first insert card right of <paramref name="currentCell"/> (or of the previous card), or null.</summary>
    public static Rect? FindInsertCard(Mat frame, Rect content, Rect currentCell)
    {
        var h = content.Height;
        var w = Math.Max(8, (int)Math.Round(InsertCardWidth * h));
        var ch = currentCell.Height;
        var slack = Math.Max(3, (int)Math.Round(0.012 * h));
        var xMin = currentCell.Right + 1;
        var xMax = currentCell.Right + (int)Math.Round(InsertCardGap * h);
        var roi = Geometry.Clamp(new Rect(xMin - 4, currentCell.Y - slack - 4, xMax - xMin + w + 8, ch + 2 * slack + 8), frame.Width, frame.Height);
        if (roi.Width < w + 2 || roi.Height < ch + 2)
        {
            return null;
        }

        using var distance = AxisDetector.DistanceMap(frame, roi);
        var best = AxisDetector.BestMatch(distance, roi, ArchTemplate(w, ch), w, ch, xMin, xMax, currentCell.Y - slack, currentCell.Y + slack);
        return best.Score / (h / ReferenceHeight) <= InsertCardMaxScore ? new Rect(best.X, best.Y, w, ch) : null;
    }

    /// <summary>Outline points of a card: straight sides and bottom, a half-circle top.</summary>
    internal static List<Point> ArchTemplate(int w, int h)
    {
        var points = new List<Point>();
        var r = w / 2.0;
        for (var x = 0; x < w; x += 2)
        {
            points.Add(new Point(x, h - 1));
        }

        for (var y = (int)r; y < h; y += 2)
        {
            points.Add(new Point(0, y));
            points.Add(new Point(w - 1, y));
        }

        for (var a = 180; a <= 360; a += 10)
        {
            var rad = a * Math.PI / 180;
            points.Add(new Point((int)Math.Round(r + (r - 1) * Math.Cos(rad)), (int)Math.Round(r + (r - 1) * Math.Sin(rad))));
        }

        return points;
    }
}
