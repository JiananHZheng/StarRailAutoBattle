using AxisRecognizer;
using OpenCvSharp;

namespace AxisRecognizer.Tests;

public class ScreenCueTests : IDisposable
{
    private const int Side = 39;   // marker crop at 1080p
    private static readonly SourceInfo Source = new() { Type = "synthetic", Origin = "test" };
    private readonly TempDir _dir = new("线索");

    public void Dispose() => _dir.Dispose();

    private static Scalar Hsv(double h, double s, double v)
    {
        using var one = new Mat(1, 1, MatType.CV_8UC3, new Scalar(h, s, v));
        using var bgr = new Mat();
        Cv2.CvtColor(one, bgr, ColorConversionCodes.HSV2BGR);
        var c = bgr.At<Vec3b>(0, 0);
        return new Scalar(c.Item0, c.Item1, c.Item2);
    }

    private static readonly Scalar Teal = Hsv(92, 200, 230);
    private static readonly Scalar Red = Hsv(176, 190, 225);

    /// <summary>A marker crop: glyph on a flat background, with the notch ring the game draws around it.</summary>
    private static Mat Marker(string shape, Scalar color, Scalar background, int dx = 0)
    {
        var mat = new Mat(Side, Side, MatType.CV_8UC3, background);
        var c = new Point(Side / 2 + dx, Side / 2);
        Cv2.Circle(mat, c, 9, new Scalar(150, 150, 150), 1);
        switch (shape)
        {
            case "dot":
                Cv2.Circle(mat, c, 3, color, -1);
                break;
            case "diamond":
                Cv2.FillConvexPoly(mat, new[] { new Point(c.X, c.Y - 7), new Point(c.X + 7, c.Y), new Point(c.X, c.Y + 7), new Point(c.X - 7, c.Y) }, color);
                break;
            default:
                // Four-point star: long spikes on the axes, a narrow waist on the diagonals.
                Cv2.FillPoly(mat, new[] { new[]
                {
                    new Point(c.X, c.Y - 14), new Point(c.X + 3, c.Y - 3), new Point(c.X + 14, c.Y), new Point(c.X + 3, c.Y + 3),
                    new Point(c.X, c.Y + 14), new Point(c.X - 3, c.Y + 3), new Point(c.X - 14, c.Y), new Point(c.X - 3, c.Y - 3),
                } }, color);
                break;
        }

        return mat;
    }

    private KnowledgeBase KbWithMarkers()
    {
        var kb = KnowledgeBase.Create(Path.Combine(_dir.Path, "kb"));
        var dark = new Scalar(40, 30, 90);
        foreach (var (label, shape, color) in new[]
                 {
                     ("ally-main", "dot", Teal), ("ally-extra", "star", Teal),
                     ("enemy-main", "dot", Red), ("boss-mechanism", "diamond", Red),
                 })
        {
            using var crop = Marker(shape, color, dark);
            kb.AddCueSample(CueNames.Marker, label, crop, Source, "user", null);
        }

        kb.Commit("测试线索", ["合成标记"]);
        return kb;
    }

    [Theory]
    [InlineData("dot", true, "ally-main")]
    [InlineData("star", true, "ally-extra")]
    [InlineData("dot", false, "enemy-main")]
    [InlineData("diamond", false, "boss-mechanism")]
    public void Marker_is_read_by_color_and_shape_on_an_unseen_background(string shape, bool ally, string expected)
    {
        var reader = new CueReader(KbWithMarkers());
        // Purple background and a one-pixel shift: neither appears in the samples.
        using var crop = Marker(shape, ally ? Teal : Red, new Scalar(150, 60, 110), dx: 1);
        var reading = reader.ReadMarker(crop);
        Assert.Equal(expected, reading.Label);
    }

    [Fact]
    public void Marker_without_a_visible_glyph_is_not_labeled()
    {
        var reader = new CueReader(KbWithMarkers());
        using var flash = new Mat(Side, Side, MatType.CV_8UC3, new Scalar(235, 235, 240));
        var reading = reader.ReadMarker(flash);
        Assert.Null(reading.Label);
        Assert.Equal("none", reading.Best);
    }

    [Fact]
    public void Geometry_separates_dot_diamond_and_star_by_area()
    {
        foreach (var (shape, ally, color) in new[] { ("dot", true, Teal), ("star", true, Teal), ("dot", false, Red), ("diamond", false, Red) })
        {
            using var crop = Marker(shape, color, new Scalar(40, 30, 90));
            var g = CueReader.GlyphGeometry(crop, ally);
            Assert.NotNull(g);
            Assert.Equal(shape, CueReader.ShapeByGeometry(ally, g.Value.Area));
        }
    }

    [Fact]
    public void Cue_samples_are_saved_relabeled_and_reloaded()
    {
        var kb = KbWithMarkers();
        var id = kb.Cues.Single(c => c.Label == "enemy-main").Id;
        kb.RelabelCue(id, "boss-mechanism");
        kb.Commit("改标签", [id]);

        var again = KnowledgeBase.Load(kb.Root);
        var sample = again.Cues.Single(c => c.Id == id);
        Assert.Equal("boss-mechanism", sample.Label);
        Assert.True(File.Exists(Path.Combine(kb.Root, sample.File)));
        Assert.Throws<ArgumentException>(() => again.AddCueSample(CueNames.Marker, "大招", new Mat(4, 4, MatType.CV_8UC3), Source, "user", null));
        Assert.Throws<ArgumentException>(() => again.AddCueSample("血条", "none", new Mat(4, 4, MatType.CV_8UC3), Source, "user", null));
    }

    /// <summary>A phase-text crop (259×59 at 1080p): dark box, two "side" glyphs, then the shared rest of the text.</summary>
    private static Mat PhaseBox(string side, int dx = 0)
    {
        var mat = new Mat(59, 259, MatType.CV_8UC3, new Scalar(60, 40, 80));
        Cv2.Rectangle(mat, new Rect(8, 6, 243, 47), new Scalar(35, 25, 30), -1);
        var x = 72 + dx;
        if (side == "ally")
        {
            Cv2.Line(mat, new Point(x, 18), new Point(x + 16, 42), Scalar.White, 2);
            Cv2.Line(mat, new Point(x + 16, 18), new Point(x, 42), Scalar.White, 2);
        }
        else
        {
            Cv2.Rectangle(mat, new Rect(x, 18, 16, 24), Scalar.White, 2);
        }

        Cv2.Circle(mat, new Point(x + 32, 30), 9, Scalar.White, 2);
        for (var i = 0; i < 3; i++)
        {
            Cv2.Rectangle(mat, new Rect(x + 56 + i * 24, 20, 14, 20), Scalar.White, 2);   // "行动中", the same for both
        }

        return mat;
    }

    [Fact]
    public void Phase_text_side_is_read_from_the_first_characters_and_tolerates_a_shift()
    {
        var kb = KnowledgeBase.Create(Path.Combine(_dir.Path, "kb"));
        foreach (var label in new[] { "ally", "enemy" })
        {
            using var box = PhaseBox(label);
            kb.AddCueSample(CueNames.PhaseText, label, box, Source, "user", null);
        }

        using (var none = new Mat(59, 259, MatType.CV_8UC3, new Scalar(60, 40, 80)))
        {
            kb.AddCueSample(CueNames.PhaseText, "none", none, Source, "user", null);
        }

        kb.Commit("测试", ["合成状态文字"]);
        var reader = new CueReader(kb);
        foreach (var label in new[] { "ally", "enemy" })
        {
            using var shifted = PhaseBox(label, dx: 3);
            Assert.Equal(label, reader.ReadPhaseText(shifted).Label);
        }
    }

    [Fact]
    public void Battle_start_is_a_black_screen_with_a_thin_white_line_across_the_middle()
    {
        var content = new Rect(0, 0, 320, 180);
        using var start = new Mat(180, 320, MatType.CV_8UC3, Scalar.Black);
        Cv2.Line(start, new Point(0, 90), new Point(319, 90), Scalar.White, 1);
        Assert.True(CueReader.BattleStartScore(start, content) > 0.9);

        using var black = new Mat(180, 320, MatType.CV_8UC3, Scalar.Black);
        Assert.Equal(0, CueReader.BattleStartScore(black, content));

        using var bright = new Mat(180, 320, MatType.CV_8UC3, new Scalar(90, 60, 160));
        Cv2.Line(bright, new Point(0, 90), new Point(319, 90), Scalar.White, 1);
        Assert.Equal(0, CueReader.BattleStartScore(bright, content));
    }

    /// <summary>A 1080p frame with round buttons drawn at content-height positions (x, y, r).</summary>
    private static Mat Buttons(params (double X, double Y, double R)[] buttons)
    {
        var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(70, 40, 60));
        foreach (var (x, y, r) in buttons)
        {
            var c = new Point((int)(x * 1080), (int)(y * 1080));
            Cv2.Circle(frame, c, (int)(r * 1080), new Scalar(30, 25, 30), -1);
            Cv2.Circle(frame, c, (int)(r * 1080), new Scalar(230, 230, 230), 3);
        }

        return frame;
    }

    [Fact]
    public void Selected_button_is_the_enlarged_one_wherever_its_icon_and_key_are()
    {
        var content = new Rect(0, 0, 1920, 1080);
        using (var basic = Buttons((1.525, 0.853, 0.070), (1.680, 0.777, 0.050)))
        {
            Assert.Equal("basic", CueReader.ReadSelection(basic, content));
        }

        using (var skill = Buttons((1.551, 0.874, 0.049), (1.680, 0.756, 0.070)))
        {
            Assert.Equal("skill", CueReader.ReadSelection(skill, content));
        }

        using (var single = Buttons((1.607, 0.853, 0.083)))
        {
            Assert.Equal("single", CueReader.ReadSelection(single, content));
        }

        using (var same = Buttons((1.525, 0.853, 0.060), (1.680, 0.777, 0.060)))
        {
            Assert.Null(CueReader.ReadSelection(same, content));
        }

        using var none = Buttons();
        Assert.Null(CueReader.ReadSelection(none, content));
    }

    /// <summary>Banner-like text drawn into a 1080p frame at a content-height position.</summary>
    private static void DrawText(Mat frame, string text, double x, double y) =>
        Cv2.PutText(frame, text, new Point((int)(x * 1080), (int)(y * 1080)), HersheyFonts.HersheyDuplex, 1.6, Scalar.White, 3);

    [Fact]
    public void Banners_are_found_anywhere_in_their_band_with_their_center()
    {
        var kb = KnowledgeBase.Create(Path.Combine(_dir.Path, "kb"));
        var content = new Rect(0, 0, 1920, 1080);
        foreach (var (label, text, y) in new[] { ("battle-start", "START", 0.17), ("battle-end", "FINISH", 0.17), ("challenge-success", "CLEAR", 0.29) })
        {
            using var source = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(60, 30, 90));
            DrawText(source, text, 0.80, y);
            using var crop = new Mat(source, new RelRect(0.79, y - 0.045, 0.20, 0.06).ToPixels(content));
            kb.AddCueSample(CueNames.Banner, label, crop, Source, "user", null);
        }

        kb.Commit("测试", ["合成横幅"]);
        using var reader = new CueReader(kb);

        // "START 2/2": the same word moved left to make room for the wave count.
        using var wave = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(90, 50, 40));
        DrawText(wave, "START  2/2", 0.70, 0.17);
        var (label1, score1, center1) = reader.ReadBanner(wave, content);
        Assert.Equal("battle-start", label1);
        Assert.True(score1 > 0.8 && center1 < 0.85, $"{score1} {center1}");

        using var result = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(20, 10, 30));
        DrawText(result, "CLEAR", 0.80, 0.29);
        Assert.Equal("challenge-success", reader.ReadBanner(result, content).Label);

        using var plain = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(90, 50, 40));
        Assert.True(reader.ReadBanner(plain, content).Score < 0.5);
    }

    /// <summary>A 1080p frame with lit phase dots (white with a red glow) and used ones (dark rings) on the dot row.</summary>
    private static Mat Dots(int lit, int used)
    {
        var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(40, 30, 160));
        for (var i = 0; i < lit + used; i++)
        {
            var c = new Point((int)((0.595 + 0.016 * i) * 1080), (int)(0.040 * 1080));
            if (i < lit)
            {
                Cv2.Circle(frame, c, 7, new Scalar(60, 40, 230), -1);
                Cv2.Circle(frame, c, 4, new Scalar(245, 240, 255), -1);
            }
            else
            {
                Cv2.Circle(frame, c, 5, new Scalar(20, 15, 40), 2);
            }
        }

        // The HP bar below the dots: long and white, never a dot.
        Cv2.Rectangle(frame, new Rect((int)(0.60 * 1080), (int)(0.056 * 1080), 300, 4), Scalar.White, -1);
        return frame;
    }

    [Theory]
    [InlineData(3, 0, 3)]
    [InlineData(1, 1, 1)]
    [InlineData(0, 2, 0)]
    public void Phase_dots_count_the_lit_ones_from_the_portrait(int lit, int used, int expected)
    {
        using var frame = Dots(lit, used);
        Assert.Equal(expected, CueReader.PhaseDots(frame, new Rect(0, 0, 1920, 1080)));
    }

    /// <summary>
    /// A synthetic round / action-value bar (121×35 at 1080p): pale cap, an hourglass-like mark, then the text with a
    /// thin "|". Digits are drawn one by one at fixed positions, like the game's fixed-width font.
    /// </summary>
    private static Mat Bar(string text)
    {
        var bar = new Mat(35, 121, MatType.CV_8UC3, new Scalar(120, 60, 110));
        Cv2.Rectangle(bar, new Rect(20, 9, 8, 17), new Scalar(230, 230, 235), -1);   // hourglass
        var x = 38;
        foreach (var ch in text)
        {
            if (ch == '|')
            {
                Cv2.Line(bar, new Point(x + 1, 8), new Point(x + 1, 27), new Scalar(235, 235, 240), 2);
                x += 12;
                continue;
            }

            Cv2.PutText(bar, ch.ToString(), new Point(x, 27), HersheyFonts.HersheySimplex, 0.65, new Scalar(240, 240, 245), 2);
            x += 14;
        }

        return bar;
    }

    [Fact]
    public void Round_and_action_value_are_read_from_learned_digits_and_unknown_digits_stay_unclear()
    {
        var kb = KnowledgeBase.Create(Path.Combine(_dir.Path, "kb"));
        // Digits 5, 6 and 7 are never seen.
        foreach (var label in new[] { "2|194", "2|380" })
        {
            using var sample = Bar(label);
            kb.AddCueSample(CueNames.AvBar, label, sample, Source, "user", null);
        }

        kb.Commit("测试", ["合成轮次行动值条"]);
        using var reader = new CueReader(kb);
        Assert.Empty(reader.AvBarProblems);

        // The bar somewhere down the axis column of a 1080p frame.
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(70, 30, 50));
        using (var bar = Bar("2|839"))
        using (var place = new Mat(frame, new Rect(55, 610, 121, 35)))
        {
            bar.CopyTo(place);
        }

        var reading = reader.ReadAvBar(frame, new Rect(0, 0, 1920, 1080));
        Assert.NotNull(reading);
        Assert.Equal((2, 839), (reading.Round, reading.ActionValue));
        Assert.InRange(reading.Rect.Y, 608, 612);

        using var unknown = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(70, 30, 50));
        using (var bar = Bar("2|567"))
        using (var place = new Mat(unknown, new Rect(55, 610, 121, 35)))
        {
            bar.CopyTo(place);
        }

        Assert.Null(reader.ReadAvBar(unknown, new Rect(0, 0, 1920, 1080)));
    }

    [Fact]
    public void Bar_labels_must_be_round_bar_value()
    {
        Assert.True(CueNames.IsAvBarLabel("2|194"));
        Assert.True(CueNames.IsAvBarLabel("10|0"));
        Assert.False(CueNames.IsAvBarLabel("2 194"));
        Assert.False(CueNames.IsAvBarLabel("2|"));
        Assert.Throws<ArgumentException>(() => CueNames.Check(CueNames.AvBar, "x|1"));
    }

    [Fact]
    public void Turn_start_flash_shows_as_low_saturation_in_the_current_cell()
    {
        using var frame = new Mat(100, 100, MatType.CV_8UC3, new Scalar(60, 40, 160));
        var normal = CueReader.CellBrightness(frame, new Rect(10, 10, 60, 40));
        frame.SetTo(new Scalar(235, 235, 240));
        var flash = CueReader.CellBrightness(frame, new Rect(10, 10, 60, 40));
        Assert.True(normal.Saturation > 100);
        Assert.True(flash.Saturation < 20 && flash.Value > normal.Value);
    }
}
