using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>The round and remaining action value read from the bar under the action axis ("⌛2 | 194").</summary>
public sealed record AvBarReading(int Round, int ActionValue, string Text, double Score, Rect Rect);

/// <summary>
/// Reads the bar under the action axis. The bar's left end (cap and hourglass) locates it, wherever the axis ends;
/// the white glyphs right of the hourglass are split into characters and matched against glyphs learned from labeled
/// bar samples (a sample "2|194" teaches 2, 1, 9 and 4).
/// </summary>
public sealed partial class CueReader
{
    private const int GlyphWidth = 12;
    private const int GlyphHeight = 16;

    /// <summary>Bar samples are taken from 1080p frames (rescaled for other resolutions).</summary>
    private const double BarSampleHeight = 1080;

    /// <summary>Share of the bar's width taken by its left cap and the hourglass (measured: hourglass ends at 0.27).</summary>
    private const double BarHeadShare = 0.285;

    /// <summary>
    /// Minimum match for one digit. Digits without a sample yet matched 0.67–0.72 against the wrong digit on 绘世
    /// (0 read as 8), known ones 0.88–1.00; below this the whole reading is left unclear.
    /// </summary>
    private const double DigitAcceptScore = 0.8;

    /// <summary>"|" is much thinner than any digit ("1" is about 0.33 of its height wide).</summary>
    private const double SeparatorMaxAspect = 0.22;

    private const int MaxBarHeads = 2;

    /// <summary>Where the bar can be: the action-axis column, relative to game height (16:9).</summary>
    public static readonly RelRect AvBarSearch = new(0.0, 0.08, 0.24, 0.82);

    private readonly List<(char Char, float[] Glyph)> _glyphs = [];
    private readonly List<(byte[] Pixels, int Width, int Height, int BarWidth, int BarHeight)> _barHeads = [];
    private readonly List<string> _avBarProblems = [];

    /// <summary>Bar samples whose glyphs did not match their label (wrong crop or label); they teach nothing.</summary>
    public IReadOnlyList<string> AvBarProblems => _avBarProblems;

    public int DigitGlyphs => _glyphs.Count;

    private void LearnAvBar(string label, Mat bar)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bar, gray, ColorConversionCodes.BGR2GRAY);
        // The bar's left end looks the same in every sample, and sliding each one over the axis column was most of the
        // reading time (31 ms of 9 samples in Release); two are enough to locate it.
        var headWidth = Math.Max(4, (int)(bar.Width * BarHeadShare));
        if (_barHeads.Count < MaxBarHeads)
        {
            using var head = new Mat(gray, new Rect(0, 0, headWidth, bar.Height)).Clone();
            head.GetArray(out byte[] pixels);
            _barHeads.Add((pixels, head.Width, head.Height, bar.Width, bar.Height));
        }

        var glyphs = SegmentGlyphs(bar);
        if (glyphs.Count != label.Length)
        {
            _avBarProblems.Add($"{label}：切出 {glyphs.Count} 个字符，标签有 {label.Length} 个");
            return;
        }

        for (var i = 0; i < label.Length; i++)
        {
            var isSeparator = glyphs[i].Box.Width <= glyphs[i].Box.Height * SeparatorMaxAspect;
            if (isSeparator != (label[i] == '|'))
            {
                _avBarProblems.Add($"{label}：第 {i + 1} 个字符的形状与标签不符");
                return;
            }
        }

        for (var i = 0; i < label.Length; i++)
        {
            if (label[i] != '|')
            {
                _glyphs.Add((label[i], glyphs[i].Glyph));
            }
        }
    }

    /// <summary>
    /// White glyphs right of the hourglass, left to right: each one's box and a normalized gray patch.
    /// </summary>
    public static List<(Rect Box, float[] Glyph)> SegmentGlyphs(Mat bar)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bar, hsv, ColorConversionCodes.BGR2HSV);
        using var white = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 110, 255), white);
        using var gray = new Mat();
        Cv2.CvtColor(bar, gray, ColorConversionCodes.BGR2GRAY);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(white, labels, stats, centroids, PixelConnectivity.Connectivity8);
        int barWidth = bar.Width, barHeight = bar.Height;
        var boxes = new List<Rect>();
        for (var i = 1; i < count; i++)
        {
            var box = new Rect(stats.At<int>(i, (int)ConnectedComponentsTypes.Left), stats.At<int>(i, (int)ConnectedComponentsTypes.Top),
                stats.At<int>(i, (int)ConnectedComponentsTypes.Width), stats.At<int>(i, (int)ConnectedComponentsTypes.Height));
            var center = box.X + box.Width / 2.0;
            // Digits are taller than wide; the bar's pale outline can come out as one wide piece, and its right end as
            // a one-pixel sliver at the edge of the crop.
            if (box.Height >= barHeight * 0.35 && box.Height <= barHeight * 0.85 && box.Width <= box.Height
                && box.Width >= 2 && box.Right < barWidth - 2 && center >= barWidth * BarHeadShare
                && stats.At<int>(i, (int)ConnectedComponentsTypes.Area) >= 4)
            {
                boxes.Add(box);
            }
        }

        return boxes.OrderBy(b => b.X).Select(b => (b, GlyphPatch(gray, b))).ToList();
    }

    private static float[] GlyphPatch(Mat gray, Rect box)
    {
        using var part = new Mat(gray, box);
        using var small = new Mat();
        Cv2.Resize(part, small, new Size(GlyphWidth, GlyphHeight), 0, 0, InterpolationFlags.Area);
        var values = new float[GlyphWidth * GlyphHeight];
        for (var y = 0; y < GlyphHeight; y++)
        {
            for (var x = 0; x < GlyphWidth; x++)
            {
                values[y * GlyphWidth + x] = small.At<byte>(y, x);
            }
        }

        var mean = values.Average();
        double norm = 0;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] -= mean;
            norm += values[i] * values[i];
        }

        norm = Math.Sqrt(norm);
        for (var i = 0; i < values.Length && norm > 1e-6; i++)
        {
            values[i] = (float)(values[i] / norm);
        }

        return values;
    }

    /// <summary>Where the bar is: the best match of the samples' left ends in the axis column, or null.</summary>
    public Rect? LocateAvBar(Mat frame, Rect content)
    {
        if (_barHeads.Count == 0)
        {
            return null;
        }

        var area = Geometry.Clamp(AvBarSearch.ToPixels(content), frame.Width, frame.Height);
        using var crop = new Mat(frame, area);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        var factor = content.Height / BarSampleHeight;
        (double Score, Rect Rect)? best = null;
        foreach (var (pixels, width, height, barWidth, barHeight) in _barHeads)
        {
            using var template = new Mat(height, width, MatType.CV_8UC1);
            template.SetArray(pixels);
            using var sized = new Mat();
            Cv2.Resize(template, sized, new Size(Math.Max(4, (int)Math.Round(width * factor)), Math.Max(4, (int)Math.Round(height * factor))));
            if (gray.Width < sized.Width || gray.Height < sized.Height)
            {
                continue;
            }

            using var result = new Mat();
            Cv2.MatchTemplate(gray, sized, result, TemplateMatchModes.CCoeffNormed);
            Cv2.MinMaxLoc(result, out _, out double max, out _, out Point at);
            if (best is null || max > best.Value.Score)
            {
                best = (max, new Rect(area.X + at.X, area.Y + at.Y, (int)Math.Round(barWidth * factor), (int)Math.Round(barHeight * factor)));
            }
        }

        return best is { } b && b.Score >= Settings.AcceptScore ? Geometry.Clamp(b.Rect, frame.Width, frame.Height) : null;
    }

    /// <summary>
    /// The round and remaining action value, or null when the bar is not found, a glyph is unclear, or the text is not
    /// "digits|digits". Score is the weakest glyph's match.
    /// </summary>
    public AvBarReading? ReadAvBar(Mat frame, Rect content)
    {
        if (_glyphs.Count == 0 || LocateAvBar(frame, content) is not { } rect)
        {
            return null;
        }

        using var bar = new Mat(frame, rect);
        var text = new System.Text.StringBuilder();
        var weakest = 1.0;
        foreach (var (box, glyph) in SegmentGlyphs(bar))
        {
            if (box.Width <= box.Height * SeparatorMaxAspect)
            {
                text.Append('|');
                continue;
            }

            var ranked = _glyphs
                .GroupBy(g => g.Char)
                .Select(g => (Char: g.Key, Score: g.Max(s => Dot(glyph, s.Glyph))))
                .OrderByDescending(r => r.Score)
                .ToList();
            var (ch, score) = ranked[0];
            var margin = score - (ranked.Count > 1 ? ranked[1].Score : 0);
            if (score < DigitAcceptScore || margin < Settings.AcceptMargin)
            {
                return null;
            }

            weakest = Math.Min(weakest, score);
            text.Append(ch);
        }

        var reading = text.ToString();
        return CueNames.IsAvBarLabel(reading)
            ? new AvBarReading(int.Parse(reading.Split('|')[0]), int.Parse(reading.Split('|')[1]), reading, weakest, rect)
            : null;
    }
}
