using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>The screen cues the event reader uses, and the labels each one can have.</summary>
public static class CueNames
{
    /// <summary>The mark in the notch on the current cell's left edge.</summary>
    public const string Marker = "marker";

    /// <summary>The bottom-right box "我方行动中" / "敌方行动中".</summary>
    public const string PhaseText = "phase-text";

    /// <summary>The target reticle on a selected enemy (a square around its center).</summary>
    public const string Reticle = "reticle";

    /// <summary>
    /// Big texts at the top center: "战斗开始" at the start of every wave ("战斗开始 2/2" when there are several),
    /// "结束战斗" when the battle ends, then "挑战成功" (with a mode subtitle above it) on the result screen.
    /// </summary>
    public const string Banner = "banner";

    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Labels = new Dictionary<string, IReadOnlyList<string>>
    {
        // ally-main: teal dot; ally-extra: teal four-point star (ultimates, "小叉号"); enemy-main: red dot;
        // boss-mechanism: red diamond (e.g. 陷入【蕾创】状态); none: no marker visible (flash, cut-in, hidden axis).
        [Marker] = ["ally-main", "ally-extra", "enemy-main", "boss-mechanism", "none"],
        [PhaseText] = ["ally", "enemy", "none"],
        [Reticle] = ["main"],
        [Banner] = ["battle-start", "battle-end", "challenge-success"],
    };

    public static string Describe(string cue, string label) => (cue, label) switch
    {
        (Marker, "ally-main") => "我方主轴行动（青色圆点）",
        (Marker, "ally-extra") => "我方额外行动（青色四角星）",
        (Marker, "enemy-main") => "敌方行动（红色圆点）",
        (Marker, "boss-mechanism") => "boss 机制条目（红色菱形）",
        (PhaseText, "ally") => "我方行动中",
        (PhaseText, "enemy") => "敌方行动中",
        (Reticle, "main") => "主目标圈",
        (Banner, "battle-start") => "“战斗开始”横幅",
        (Banner, "battle-end") => "“结束战斗”横幅",
        (Banner, "challenge-success") => "结算“挑战成功”",
        (_, "none") => "无",
        _ => label,
    };

    public static void Check(string cue, string label)
    {
        if (!Labels.TryGetValue(cue, out var labels))
        {
            throw new ArgumentException($"未知线索 {cue}（可用：{string.Join("、", Labels.Keys)}）。");
        }

        if (!labels.Contains(label))
        {
            throw new ArgumentException($"线索 {cue} 没有标签 {label}（可用：{string.Join("、", labels)}）。");
        }
    }
}

/// <summary>Best label for a cue crop; <see cref="Label"/> is null when the reading is not confident.</summary>
public sealed record CueReading(string Cue, string? Label, string? Best, double Score, string? Second, double SecondScore);

/// <summary>Everything the event reader takes from one frame besides the avatars.</summary>
public sealed record ScreenCueFrame(
    /// <summary>Null when the action axis was not found.</summary>
    CueReading? Marker,
    CueReading PhaseText,
    /// <summary>Mean brightness (0–255) and saturation of the current cell; a turn start flashes it white.</summary>
    double CellBrightness,
    double CellSaturation,
    /// <summary>0–1; high on the battle-start frame (black screen with a thin white line across the middle).</summary>
    double BattleStart,
    /// <summary>Selected button while waiting for input: basic, skill, single, or null.</summary>
    string? Selection = null,
    /// <summary>Best banner text (battle-start, battle-end, challenge-success), its 0–1 match and center (content-height units).</summary>
    string? BannerLabel = null,
    double Banner = 0,
    double BannerX = 0,
    /// <summary>Lit boss phase dots (remaining phases); 0 when no boss bar is shown.</summary>
    int PhaseDots = 0);

/// <summary>Reads screen cues by comparing crops with the knowledge base's labeled cue samples (no model calls).</summary>
public sealed class CueReader : IDisposable
{
    /// <summary>Edge maps of the reticle samples.</summary>
    private readonly List<Mat> _reticles = [];

    /// <summary>Gray banner text samples, taken from 1080p frames (rescaled for other resolutions).</summary>
    private readonly List<(string Label, byte[] Pixels, int Width, int Height)> _banners = [];

    private const double BannerSampleHeight = 1080;

    /// <summary>Band at the top center where "战斗开始" and "结束战斗" appear, relative to game height (16:9).</summary>
    public static readonly RelRect BannerBand = new(0.55, 0.11, 0.70, 0.10);

    /// <summary>Band of the result screen's "挑战成功" (below the mode subtitle).</summary>
    public static readonly RelRect ResultBand = new(0.55, 0.22, 0.70, 0.12);

    /// <summary>Side of the glyph mask compared between markers.</summary>
    private const int MaskSize = 20;

    private readonly KnowledgeBase _kb;
    private readonly RecognitionSettings _feature;
    private readonly Dictionary<string, List<(string Label, AvatarFeature Feature)>> _index = [];
    private readonly List<(string Shape, float[] Mask)> _markerShapes = [];
    /// <summary>Gray templates of "我方"/"敌方", trimmed so they can slide a few pixels inside a frame's crop.</summary>
    private readonly List<(string Label, byte[] Pixels, int Width, int Height)> _phaseSides = [];

    /// <summary>Share of the side crop trimmed on each side of a template (thin strokes need the search room).</summary>
    private const double SideTemplateTrim = 0.15;

    public CueReader(KnowledgeBase kb)
    {
        _kb = kb;
        _feature = kb.Meta.Cues.AsFeatureSettings();
        foreach (var sample in kb.Cues)
        {
            using var crop = kb.ReadCue(sample);
            if (sample.Cue == CueNames.Marker)
            {
                if (ShapeOf(sample.Label) is { } shape && GlyphMask(crop) is { Mask: { } mask })
                {
                    _markerShapes.Add((shape, mask));
                }

                continue;
            }

            if (sample.Cue == CueNames.Reticle)
            {
                _reticles.Add(ReticleTemplate(crop));
                continue;
            }

            if (sample.Cue == CueNames.Banner)
            {
                using var gray = new Mat();
                Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
                gray.GetArray(out byte[] pixels);
                _banners.Add((sample.Label, pixels, gray.Width, gray.Height));
                continue;
            }

            Index(sample.Cue).Add((sample.Label, AvatarFeature.Compute(crop, _feature)));
            if (sample.Cue == CueNames.PhaseText && sample.Label != "none")
            {
                using var side = SideCrop(crop);
                using var gray = new Mat();
                Cv2.CvtColor(side, gray, ColorConversionCodes.BGR2GRAY);
                var trim = new Rect((int)(gray.Width * SideTemplateTrim), (int)(gray.Height * SideTemplateTrim),
                    gray.Width - 2 * (int)(gray.Width * SideTemplateTrim), gray.Height - 2 * (int)(gray.Height * SideTemplateTrim));
                using var template = new Mat(gray, trim).Clone();
                template.GetArray(out byte[] pixels);
                _phaseSides.Add((sample.Label, pixels, template.Width, template.Height));
            }
        }
    }

    /// <summary>
    /// The best-matching banner text (label, score 0–1) and where its center is (content-height units; the screen
    /// center is 0.889 at 16:9). "战斗开始 2/2" puts the same glyphs left of the center; plain "战斗开始" (a single
    /// wave) centers them.
    /// </summary>
    public (string? Label, double Score, double CenterX) ReadBanner(Mat frame, Rect content)
    {
        var best = (Label: (string?)null, Score: 0.0, CenterX: 0.0);
        if (_banners.Count == 0)
        {
            return best;
        }

        var factor = content.Height / BannerSampleHeight;
        foreach (var bandRel in new[] { BannerBand, ResultBand })
        {
            var band = Geometry.Clamp(bandRel.ToPixels(content), frame.Width, frame.Height);
            using var crop = new Mat(frame, band);
            using var gray = new Mat();
            Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
            foreach (var (label, pixels, width, height) in _banners.Where(b => (b.Label == "challenge-success") == (bandRel == ResultBand)))
            {
                using var template = new Mat(height, width, MatType.CV_8UC1);
                template.SetArray(pixels);
                using var sized = new Mat();
                var size = new Size(Math.Max(4, (int)Math.Round(width * factor)), Math.Max(4, (int)Math.Round(height * factor)));
                Cv2.Resize(template, sized, size, 0, 0, InterpolationFlags.Area);
                if (gray.Width < sized.Width || gray.Height < sized.Height)
                {
                    continue;
                }

                using var result = new Mat();
                Cv2.MatchTemplate(gray, sized, result, TemplateMatchModes.CCoeffNormed);
                Cv2.MinMaxLoc(result, out _, out double max, out _, out Point at);
                if (max > best.Score)
                {
                    best = (label, max, (band.X + at.X + sized.Width / 2.0 - content.X) / content.Height);
                }
            }
        }

        return best;
    }

    /// <summary>Best normalized correlation of a template anywhere inside the gray crop.</summary>
    private static double MatchAnywhere(Mat gray, byte[] pixels, int width, int height)
    {
        if (gray.Width < width || gray.Height < height)
        {
            return 0;
        }

        using var template = new Mat(height, width, MatType.CV_8UC1);
        template.SetArray(pixels);
        using var result = new Mat();
        Cv2.MatchTemplate(gray, template, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out _, out double max);
        return max;
    }

    public Mat SideCrop(Mat phaseCrop)
    {
        var s = Settings.PhaseSide;
        var rect = Geometry.Clamp(new Rect((int)Math.Round(s.X * phaseCrop.Width), (int)Math.Round(s.Y * phaseCrop.Height),
            (int)Math.Round(s.W * phaseCrop.Width), (int)Math.Round(s.H * phaseCrop.Height)), phaseCrop.Width, phaseCrop.Height);
        return new Mat(phaseCrop, rect);
    }

    /// <summary>
    /// "我方行动中" / "敌方行动中" / none: whether the box is there from the whole crop, which side from the first two
    /// characters. Both must be clear for a label.
    /// </summary>
    public CueReading ReadPhaseText(Mat crop)
    {
        var presence = Read(CueNames.PhaseText, crop);
        if (presence.Best is null or "none" || _phaseSides.Count == 0)
        {
            return presence;
        }

        // Box present: ally and enemy samples both say so, so the presence score is the better of the two.
        var noneScore = presence.Second == "none" ? presence.SecondScore : presence.Best == "none" ? presence.Score : 0;
        var present = presence.Score >= Settings.AcceptScore && presence.Score - noneScore >= Settings.AcceptMargin;
        using var side = SideCrop(crop);
        using var gray = new Mat();
        Cv2.CvtColor(side, gray, ColorConversionCodes.BGR2GRAY);
        var ranked = _phaseSides
            .GroupBy(s => s.Label)
            .Select(g => (Label: g.Key, Score: g.Max(s => MatchAnywhere(gray, s.Pixels, s.Width, s.Height))))
            .OrderByDescending(r => r.Score)
            .ToList();
        var (best, score) = ranked[0];
        var (second, secondScore) = ranked.Count > 1 ? ranked[1] : (null, 0);
        var confident = present && score >= Settings.AcceptScore && score - secondScore >= Settings.AcceptMargin;
        return new CueReading(CueNames.PhaseText, confident ? best : null, best, score, second, secondScore);
    }

    public int MarkerShapeSamples => _markerShapes.Count;

    public void Dispose() => _reticles.ForEach(r => r.Dispose());

    /// <summary>The square stored as a reticle sample around a center given in content-height units.</summary>
    public Rect ReticleRect(Rect content, double x, double y)
    {
        var side = (int)Math.Round(Settings.ReticleSide * content.Height);
        return new Rect(content.X + (int)Math.Round(x * content.Height - side / 2.0),
            content.Y + (int)Math.Round(y * content.Height - side / 2.0), side, side);
    }

    /// <summary>dot (main action), star (extra action), diamond (boss mechanism).</summary>
    private static string? ShapeOf(string markerLabel) => markerLabel switch
    {
        "ally-main" or "enemy-main" => "dot",
        "ally-extra" => "star",
        "boss-mechanism" => "diamond",
        _ => null,
    };

    private static string? LabelOf(bool ally, string shape) => (ally, shape) switch
    {
        (true, "dot") => "ally-main",
        (true, "star") => "ally-extra",
        (false, "dot") => "enemy-main",
        (false, "diamond") => "boss-mechanism",
        _ => null,
    };

    /// <summary>
    /// The marker glyph: bright teal (ally) or bright red (enemy) pixels, resized to a normalized mask so shapes can
    /// be compared whatever the background. Null mask when too few glyph pixels are visible (flash, cut-in).
    /// </summary>
    public static (float[]? Mask, int Teal, int Red) GlyphMask(Mat crop)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        using var teal = new Mat();
        // Measured on the open videos: the teal dot is about H 92–95, S 110–130, V 190–205; the red dot on a purple
        // background about H 174, S 130, V 190. Purple backgrounds sit near H 150, the dark red battlefield below V 160.
        Cv2.InRange(hsv, new Scalar(78, 90, 150), new Scalar(106, 255, 255), teal);
        using var redLow = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 105, 165), new Scalar(12, 255, 255), redLow);
        using var redHigh = new Mat();
        Cv2.InRange(hsv, new Scalar(168, 105, 165), new Scalar(180, 255, 255), redHigh);
        using var red = new Mat();
        Cv2.BitwiseOr(redLow, redHigh, red);
        var tealCount = Cv2.CountNonZero(teal);
        var redCount = Cv2.CountNonZero(red);
        // The main-action dot is small: about 15–25 pixels of a 39×39 crop at 1080p.
        if (Math.Max(tealCount, redCount) < crop.Width * crop.Height * 0.006)
        {
            return (null, tealCount, redCount);
        }

        using var glyph = new Mat();
        Cv2.BitwiseOr(teal, red, glyph);
        using var small = new Mat();
        Cv2.Resize(glyph, small, new Size(MaskSize, MaskSize), 0, 0, InterpolationFlags.Area);
        var values = new float[MaskSize * MaskSize];
        for (var y = 0; y < MaskSize; y++)
        {
            for (var x = 0; x < MaskSize; x++)
            {
                values[y * MaskSize + x] = small.At<byte>(y, x) / 255f;
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
        if (norm < 1e-6)
        {
            return (null, tealCount, redCount);
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = (float)(values[i] / norm);
        }

        return (values, tealCount, redCount);
    }

    /// <summary>
    /// Geometry of the glyph's core (the colored blob nearest the crop center): its area as a share of the crop, and
    /// how far it reaches along the axes compared with the diagonals — about 1 for a dot, 1.4 for a diamond (corners
    /// on the axes) and well above that for the four-point star.
    /// </summary>
    public static (double Area, double AxisRatio)? GlyphGeometry(Mat crop)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        using var teal = new Mat();
        Cv2.InRange(hsv, new Scalar(78, 90, 150), new Scalar(106, 255, 255), teal);
        using var redLow = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 105, 165), new Scalar(12, 255, 255), redLow);
        using var redHigh = new Mat();
        Cv2.InRange(hsv, new Scalar(168, 105, 165), new Scalar(180, 255, 255), redHigh);
        using var glyph = new Mat();
        Cv2.BitwiseOr(redLow, redHigh, glyph);
        Cv2.BitwiseOr(glyph, teal, glyph);

        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(glyph, labels, stats, centroids, PixelConnectivity.Connectivity8);
        double cx0 = crop.Width / 2.0, cy0 = crop.Height / 2.0;
        var reach = crop.Width * 0.22;
        var core = -1;
        var bestDistance = double.MaxValue;
        for (var i = 1; i < count; i++)
        {
            var d = Math.Sqrt(Math.Pow(centroids.At<double>(i, 0) - cx0, 2) + Math.Pow(centroids.At<double>(i, 1) - cy0, 2));
            if (d <= reach && d < bestDistance && stats.At<int>(i, (int)ConnectedComponentsTypes.Area) >= 4)
            {
                (core, bestDistance) = (i, d);
            }
        }

        if (core < 0)
        {
            return null;
        }

        double cx = centroids.At<double>(core, 0), cy = centroids.At<double>(core, 1);
        double axis = 0, diagonal = 0;
        var area = 0;
        for (var y = 0; y < crop.Height; y++)
        {
            for (var x = 0; x < crop.Width; x++)
            {
                if (labels.At<int>(y, x) != core)
                {
                    continue;
                }

                area++;
                double dx = x - cx, dy = y - cy;
                var r = Math.Sqrt(dx * dx + dy * dy) + 0.5;
                var angle = Math.Abs(Math.Atan2(dy, dx) * 180 / Math.PI) % 90;
                if (angle <= 12 || angle >= 78)
                {
                    axis = Math.Max(axis, r);
                }
                else if (angle is >= 33 and <= 57)
                {
                    diagonal = Math.Max(diagonal, r);
                }
            }
        }

        return (area / (double)(crop.Width * crop.Height), diagonal > 0 ? axis / diagonal : 9);
    }

    /// <summary>
    /// Shape from the core's area (share of the crop) and color, or null in the gaps between. Measured on 43 samples
    /// from two open videos: dots 0.020–0.037, red diamonds 0.036–0.143, teal stars 0.088–0.30. The axis ratio turned
    /// out too noisy at this size to separate dot from diamond, so it is only reported.
    /// </summary>
    public static string? ShapeByGeometry(bool ally, double area) => ally
        ? area <= 0.042 ? "dot" : area >= 0.07 ? "star" : null
        : area <= 0.034 ? "dot" : area >= 0.042 ? "diamond" : null;

    /// <summary>
    /// Side by glyph color; shape by the closest sample glyph, which must agree with the core's geometry. Anything
    /// unclear stays unlabeled (the check prefers "待定" to a wrong reading).
    /// </summary>
    public CueReading ReadMarker(Mat crop)
    {
        var (mask, teal, red) = GlyphMask(crop);
        if (mask is null || _markerShapes.Count == 0)
        {
            return new CueReading(CueNames.Marker, null, mask is null ? "none" : null, 0, null, 0);
        }

        bool? ally = teal >= 2 * red ? true : red >= 2 * teal ? false : null;
        var ranked = _markerShapes
            .GroupBy(s => s.Shape)
            .Select(g => (Shape: g.Key, Score: g.Max(s => Dot(mask, s.Mask))))
            .OrderByDescending(r => r.Score)
            .ToList();
        var (shape, score) = ranked[0];
        var secondScore = ranked.Count > 1 ? ranked[1].Score : 0;
        var geometric = ally is { } a && GlyphGeometry(crop) is { } g ? ShapeByGeometry(a, g.Area) : null;
        var label = ally is { } side ? LabelOf(side, shape) : null;
        // The geometry is an independent check, so agreeing readings need no extra margin over the second shape.
        var confident = label is not null && geometric == shape && score >= Settings.AcceptScore;
        return new CueReading(CueNames.Marker, confident ? label : null, label ?? shape, score,
            ranked.Count > 1 ? ranked[1].Shape : null, secondScore);
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    public CueSettings Settings => _kb.Meta.Cues;

    public int Count(string cue) => _index.TryGetValue(cue, out var list) ? list.Count : 0;

    private List<(string Label, AvatarFeature Feature)> Index(string cue) =>
        _index.TryGetValue(cue, out var list) ? list : _index[cue] = [];

    public CueReading Read(string cue, Mat crop)
    {
        var feature = AvatarFeature.Compute(crop, _feature);
        var ranked = Index(cue)
            .GroupBy(s => s.Label)
            .Select(g => (Label: g.Key, Score: g.Max(s => feature.Similarity(s.Feature, _feature))))
            .OrderByDescending(r => r.Score)
            .ToList();
        if (ranked.Count == 0)
        {
            return new CueReading(cue, null, null, 0, null, 0);
        }

        var (best, score) = ranked[0];
        var (second, secondScore) = ranked.Count > 1 ? ranked[1] : (null, 0);
        var confident = score >= Settings.AcceptScore && score - secondScore >= Settings.AcceptMargin;
        return new CueReading(cue, confident ? best : null, best, score, second, secondScore);
    }

    /// <summary>The marker crop: a square around the notch in the current cell's left edge.</summary>
    public static Rect MarkerRect(Rect currentCell, Rect content, AxisColumn axis, CueSettings settings)
    {
        var radius = axis.NotchRadius * content.Height;
        var side = Math.Max(6, (int)Math.Round(radius * settings.MarkerSize));
        var cy = currentCell.Y + currentCell.Height / 2.0;
        return new Rect((int)Math.Round(currentCell.X - side / 2.0), (int)Math.Round(cy - side / 2.0), side, side);
    }

    /// <summary>The marker crop for a recognized frame, or null when the axis was not found.</summary>
    public Rect? MarkerRect(FrameResult axisResult)
    {
        var layout = _kb.Layouts.FirstOrDefault(l => l.Id == axisResult.LayoutId);
        var current = axisResult.Slots.FirstOrDefault(s => s.Kind == SlotKind.Current);
        if (!axisResult.AxisFound || layout?.Axis is not { } axis || current is null)
        {
            return null;
        }

        return Geometry.Clamp(MarkerRect(current.FeatureRect, axisResult.Content, axis, Settings), axisResult.Width, axisResult.Height);
    }

    public Rect PhaseTextRect(Rect content, int width, int height) =>
        Geometry.Clamp(Settings.PhaseText.ToPixels(content), width, height);

    /// <param name="lastCurrentCell">
    /// Where the current cell was in the latest frame that showed it. The turn-start flash washes out the cell's
    /// outline, so the axis is often not found on exactly the frames that matter; the brightness is then measured here.
    /// </param>
    public ScreenCueFrame ReadFrame(Mat frame, FrameResult axisResult, Rect? lastCurrentCell = null)
    {
        CueReading? marker = null;
        if (MarkerRect(axisResult) is { } markerRect)
        {
            using var crop = new Mat(frame, markerRect);
            marker = ReadMarker(crop);
        }

        CueReading phase;
        using (var text = new Mat(frame, PhaseTextRect(axisResult.Content, frame.Width, frame.Height)))
        {
            phase = ReadPhaseText(text);
        }

        var (brightness, saturation) = (0.0, 0.0);
        if (axisResult.AxisFound && axisResult.Slots.FirstOrDefault(s => s.Kind == SlotKind.Current) is { } current)
        {
            (brightness, saturation) = CellBrightness(frame, current.FeatureRect);
        }
        else if (lastCurrentCell is { } last)
        {
            (brightness, saturation) = CellBrightness(frame, last);
        }

        var (bannerLabel, banner, bannerX) = ReadBanner(frame, axisResult.Content);
        return new ScreenCueFrame(marker, phase, brightness, saturation, BattleStartScore(frame, axisResult.Content),
            ReadSelection(frame, axisResult.Content), bannerLabel, banner, bannerX, PhaseDots(frame, axisResult.Content));
    }

    /// <summary>Area of the basic-attack and skill buttons (bottom right), relative to game height (16:9).</summary>
    public static readonly RelRect ButtonArea = new(1.40, 0.62, 0.38, 0.37);

    /// <summary>
    /// Round buttons found in the button area, in content-height units relative to the content's top-left corner.
    /// Developer view for now; the selection reader is built on these measurements.
    /// </summary>
    public static List<(double X, double Y, double R)> ButtonCircles(Mat frame, Rect content)
    {
        var h = content.Height;
        var area = Geometry.Clamp(ButtonArea.ToPixels(content), frame.Width, frame.Height);
        using var crop = new Mat(frame, area);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 1.5);
        var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient, 1, h * 0.06, 110, 40, (int)(h * 0.035), (int)(h * 0.09));
        return circles
            .Select(c => ((area.X + c.Center.X - content.X) / (double)h, (area.Y + c.Center.Y - content.Y) / (double)h, c.Radius / (double)h))
            .ToList();
    }

    // Button centers and radii measured on the open videos (keyboard Q/E and controller X/Y look the same), in
    // content-height units: the selected button is enlarged in place, r ≈ 0.070 against ≈ 0.050 for the other.
    private static readonly (double X, double Y) BasicButton = (1.525, 0.853);
    private static readonly (double X, double Y) SkillButton = (1.677, 0.770);

    /// <summary>One enlarged button between the two (e.g. a memosprite's or an enhanced turn), r ≈ 0.083.</summary>
    private static readonly (double X, double Y) SingleButton = (1.607, 0.853);

    /// <summary>
    /// Which button is selected while the game waits for input: "basic", "skill", "single" (one enlarged button
    /// only), or null (not the selection screen, or unclear). It does not depend on the input device or the
    /// character's icons: only on where the circles are and which one is larger.
    /// </summary>
    public static string? ReadSelection(Mat frame, Rect content)
    {
        var circles = ButtonCircles(frame, content);
        (double X, double Y, double R)? Near((double X, double Y) anchor) =>
            // A shrunken button sits a little off its enlarged center (basic: 1.551,0.874 small vs 1.525,0.853 big).
            circles.Where(c => Math.Abs(c.X - anchor.X) <= 0.035 && Math.Abs(c.Y - anchor.Y) <= 0.035)
                .Select(c => ((double X, double Y, double R)?)c).FirstOrDefault();

        var basic = Near(BasicButton);
        var skill = Near(SkillButton);
        if (basic is { } b && skill is { } s)
        {
            return b.R >= 0.062 && s.R <= 0.057 ? "basic"
                : s.R >= 0.062 && b.R <= 0.057 ? "skill"
                : null;
        }

        return basic is null && skill is null && Near(SingleButton) is { R: >= 0.077 } ? "single" : null;
    }

    /// <summary>The battlefield where enemies stand, relative to game height (16:9): right of the axis, above the team bar.</summary>
    public static readonly RelRect Field = new(0.25, 0.06, 1.30, 0.66);

    /// <summary>Scales tried when matching a reticle sample (adjacent targets of a blast attack get smaller reticles).</summary>
    private static readonly double[] ReticleScales = [0.6, 0.75, 0.9, 1.0, 1.15];

    /// <summary>
    /// Target reticles on the battlefield, in content-height units with their matched scale: a thin light ring with
    /// small triangles and a small white ring at the center. The selected enemy is also outlined in orange and the
    /// scenes can be bright white, so circle searches fire everywhere; the reticle samples are matched by their
    /// edges instead (blurred, so a pixel or two of offset does not matter).
    /// </summary>
    public List<(double X, double Y, double Scale, double Score)> TargetReticles(Mat frame, Rect content)
    {
        var found = new List<(double X, double Y, double Scale, double Score)>();
        if (_reticles.Count == 0)
        {
            return found;
        }

        var h = content.Height;
        var area = Geometry.Clamp(Field.ToPixels(content), frame.Width, frame.Height);
        using var crop = new Mat(frame, area);
        using var edges = EdgeMap(crop);

        // Candidates: the small white center ring (cheap to find, many false ones in bright scenes).
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var smooth = new Mat();
        Cv2.GaussianBlur(gray, smooth, new Size(3, 3), 1);
        var centers = Cv2.HoughCircles(smooth, HoughModes.Gradient, 1, h * 0.03, 120, 14,
            Math.Max(2, (int)(h * 0.005)), Math.Max(4, (int)(h * 0.013)));

        // Verify each candidate with the masked templates, searching only a few pixels around it (masked matching
        // has no fast path, so it must stay small).
        var baseSide = Settings.ReticleSide * h;
        var slack = Math.Max(3, (int)(h * 0.004));
        foreach (var candidate in centers)
        {
            foreach (var template in _reticles)
            {
                foreach (var scale in ReticleScales)
                {
                    var side = (int)Math.Round(baseSide * scale);
                    var roi = new Rect((int)Math.Round(candidate.Center.X - side / 2.0) - slack, (int)Math.Round(candidate.Center.Y - side / 2.0) - slack,
                        side + 2 * slack, side + 2 * slack);
                    if (side < 12 || roi.X < 0 || roi.Y < 0 || roi.Right > edges.Width || roi.Bottom > edges.Height)
                    {
                        continue;
                    }

                    using var window = new Mat(edges, roi);
                    using var sized = new Mat();
                    Cv2.Resize(template, sized, new Size(side, side), 0, 0, InterpolationFlags.Area);
                    using var mask = ReticleMask(side);
                    using var result = new Mat();
                    Cv2.MatchTemplate(window, sized, result, TemplateMatchModes.CCorrNormed, mask);
                    Cv2.MinMaxLoc(result, out _, out double max, out _, out Point at);
                    if (max >= Settings.ReticleScore)
                    {
                        found.Add(((area.X + roi.X + at.X + side / 2.0 - content.X) / h, (area.Y + roi.Y + at.Y + side / 2.0 - content.Y) / h, scale, max));
                    }
                }
            }
        }

        // One reticle per place: keep the best-scoring match within half a reticle of another.
        var kept = new List<(double X, double Y, double Scale, double Score)>();
        foreach (var r in found.OrderByDescending(f => f.Score))
        {
            if (kept.All(k => Math.Abs(k.X - r.X) > Settings.ReticleSide / 2 || Math.Abs(k.Y - r.Y) > Settings.ReticleSide / 2))
            {
                kept.Add(r);
            }
        }

        return kept;
    }

    /// <summary>
    /// A reticle sample's edges, keeping only the outer ring (with its triangles) and the center ring: the enemy
    /// behind the reticle in the sample must not be learned as part of it.
    /// </summary>
    private static Mat ReticleTemplate(Mat sample) => EdgeMap(sample);

    /// <summary>Where a reticle template counts: the outer ring band (with its triangles) and the center ring.</summary>
    private static Mat ReticleMask(int side)
    {
        var keep = new Mat(side, side, MatType.CV_8UC1, Scalar.All(0));
        var c = new Point(side / 2, side / 2);
        Cv2.Circle(keep, c, (int)(side * 0.44), Scalar.All(255), -1);
        Cv2.Circle(keep, c, (int)(side * 0.28), Scalar.All(0), -1);
        Cv2.Circle(keep, c, (int)(side * 0.12), Scalar.All(255), -1);
        return keep;
    }

    /// <summary>Blurred edge map: the reticle's thin lines without the colors around them.</summary>
    private static Mat EdgeMap(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 60, 150);
        var blurred = new Mat();
        Cv2.GaussianBlur(edges, blurred, new Size(5, 5), 1.5);
        return blurred;
    }

    /// <summary>Where the boss's phase dots sit, right of its portrait above the HP bar (16:9, content-height units).</summary>
    public static readonly RelRect PhaseDotArea = new(0.565, 0.022, 0.13, 0.036);

    /// <summary>
    /// Lit phase dots above the boss HP bar: one per remaining phase (white center with a red glow); a used dot
    /// turns into a dark ring. A phase change is the count dropping by one; no boss bar (or no dots) reads 0.
    /// </summary>
    public static int PhaseDots(Mat frame, Rect content)
    {
        // Dots sit in one row starting just right of the portrait, about 0.016 of game height apart (measured on
        // 绘世 and 铁墓: first dot x ≈ 0.595, row y ≈ 0.040). Count the regular chain from the first one.
        var dots = PhaseDotCandidates(frame, content).Where(c => c.Lit).OrderBy(c => c.X).ToList();
        var first = dots.FindIndex(d => d.X is >= 0.585 and <= 0.605);
        if (first < 0)
        {
            return 0;
        }

        var n = 1;
        for (var i = first + 1; i < dots.Count && dots[i].X - dots[i - 1].X is >= 0.012 and <= 0.021; i++)
        {
            n++;
        }

        return n;
    }

    /// <summary>Every bright blob in the dot area with its geometry and whether it counts as a lit dot (developer view).</summary>
    public static List<(double X, double Y, int W, int H, int Area, int Glow, bool Lit)> PhaseDotCandidates(Mat frame, Rect content)
    {
        var area = Geometry.Clamp(PhaseDotArea.ToPixels(content), frame.Width, frame.Height);
        using var crop = new Mat(frame, area);
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        using var white = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 0, 225), new Scalar(180, 110, 255), white);
        using var redLow = new Mat();
        Cv2.InRange(hsv, new Scalar(0, 90, 150), new Scalar(12, 255, 255), redLow);
        using var red = new Mat();
        Cv2.InRange(hsv, new Scalar(160, 90, 150), new Scalar(180, 255, 255), red);
        Cv2.BitwiseOr(red, redLow, red);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(white, labels, stats, centroids, PixelConnectivity.Connectivity8);
        var h = content.Height;
        var found = new List<(double X, double Y, int W, int H, int Area, int Glow, bool Lit)>();
        for (var i = 1; i < count; i++)
        {
            var w = stats.At<int>(i, (int)ConnectedComponentsTypes.Width);
            var ht = stats.At<int>(i, (int)ConnectedComponentsTypes.Height);
            var a = stats.At<int>(i, (int)ConnectedComponentsTypes.Area);
            var x = stats.At<int>(i, (int)ConnectedComponentsTypes.Left);
            var y = stats.At<int>(i, (int)ConnectedComponentsTypes.Top);
            var ring = Geometry.Clamp(new Rect(x - w, y - ht, w * 3, ht * 3), crop.Width, crop.Height);
            using var glow = new Mat(red, ring);
            var glowCount = Cv2.CountNonZero(glow);
            // A lit dot's white core is a small filled round blob (about 0.007 of game height across) on the dot row.
            // The red glow is reported only: on a red battlefield it can't be told from the background.
            var cy = (area.Y + y + ht / 2.0 - content.Y) / h;
            var lit = w >= h * 0.004 && w <= h * 0.012 && ht >= h * 0.004 && ht <= h * 0.012 && w <= 1.5 * ht && ht <= 1.5 * w
                && a >= 0.55 * w * ht && cy is >= 0.032 and <= 0.048;
            found.Add(((area.X + x + w / 2.0 - content.X) / h, cy, w, ht, a, glowCount, lit));
        }

        return found;
    }

    /// <summary>Mean value and saturation (HSV, 0–255) of a rect's inner part.</summary>
    public static (double Value, double Saturation) CellBrightness(Mat frame, Rect cell)
    {
        var inner = Geometry.Clamp(new Rect(cell.X + cell.Width / 6, cell.Y + cell.Height / 6, cell.Width * 2 / 3, cell.Height * 2 / 3),
            frame.Width, frame.Height);
        using var crop = new Mat(frame, inner);
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);
        var mean = Cv2.Mean(hsv);
        return (mean.Val2, mean.Val1);
    }

    /// <summary>
    /// The battle-start frame: after the technique the screen goes black and a thin white line appears across the
    /// middle. Score = share of the brightest middle row that is white, when the rest of the picture is dark; else 0.
    /// </summary>
    public static double BattleStartScore(Mat frame, Rect content)
    {
        using var region = new Mat(frame, content);
        using var gray = new Mat();
        Cv2.CvtColor(region, gray, ColorConversionCodes.BGR2GRAY);
        using var white = new Mat();
        Cv2.Threshold(gray, white, 170, 1, ThresholdTypes.Binary);
        using var rowWhite = new Mat();
        Cv2.Reduce(white, rowWhite, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_64F);

        var h = gray.Rows;
        int bandTop = (int)(h * 0.4), bandBottom = (int)(h * 0.6);
        var bestRow = -1;
        var best = 0.0;
        for (var y = bandTop; y < bandBottom; y++)
        {
            var share = rowWhite.At<double>(y, 0);
            if (share > best)
            {
                (best, bestRow) = (share, y);
            }
        }

        if (bestRow < 0 || best < 0.2)
        {
            return 0;
        }

        // Everything away from the line (and away from the top-right corner, where video sites put watermarks) is dark.
        var margin = Math.Max(3, h / 40);
        using var dark = new Mat();
        Cv2.Threshold(gray, dark, 40, 1, ThresholdTypes.BinaryInv);
        double darkCount = 0, total = 0;
        foreach (var (top, bottom) in new[] { (0, bestRow - margin), (bestRow + margin, h) })
        {
            if (bottom <= top)
            {
                continue;
            }

            var rect = new Rect(0, top, gray.Cols, bottom - top);
            using var part = new Mat(dark, rect);
            darkCount += Cv2.Sum(part).Val0;
            total += rect.Width * rect.Height;
            if (top == 0)
            {
                // Leave out the top-right watermark corner.
                var corner = new Rect(gray.Cols * 3 / 4, 0, gray.Cols / 4, Math.Min(bottom, h / 8));
                using var cornerPart = new Mat(dark, corner);
                darkCount -= Cv2.Sum(cornerPart).Val0;
                total -= corner.Width * corner.Height;
            }
        }

        return total > 0 && darkCount / total >= 0.9 ? Math.Min(1, best) : 0;
    }
}
