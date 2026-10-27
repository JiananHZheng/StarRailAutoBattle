using AxisRecognizer;
using OpenCvSharp;

namespace AxisRecognizer.Tests;

public sealed class RecognitionTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static readonly Dictionary<string, int?> TrainSlots = new()
    {
        ["current"] = 1, ["main2"] = 2, ["main3"] = 3, ["main4"] = 4, ["main5"] = 5, ["insert1"] = 6, ["insert2"] = null,
    };

    private static string UnitFor(int seed) => seed == 6 ? "CH-测试角色" : $"M000{seed}";

    /// <summary>Builds a KB from one synthetic 1920×1080 training frame.</summary>
    private KnowledgeBase BuildKb(bool labelEmpty = true)
    {
        var kb = KnowledgeBase.Create(Path.Combine(_dir.Path, "kb"));
        for (var i = 1; i <= 5; i++)
        {
            kb.AddUnit(UnitKind.Monster, null, null, "none", null, null);
        }

        kb.AddUnit(UnitKind.Character, null, "测试角色", "confirmed", "test", null);
        kb.UpsertLayout(Synth.Layout16x9());

        var path = Path.Combine(_dir.Path, "train.png");
        using (var frame = Synth.Frame(1920, 1080, TrainSlots, backgroundSeed: 100))
        {
            ImageIO.WritePng(path, frame);
        }

        var shot = kb.AddScreenshot(path, new SourceInfo { Type = "synthetic", Origin = "train.png" }, "test", "train", null, out _);
        using var image = kb.ReadScreenshot(shot);
        var labels = TrainSlots.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => UnitFor(kv.Value!.Value));
        if (labelEmpty)
        {
            labels["insert2"] = KnowledgeBase.EmptyUnitId;
        }

        KbLabeler.LabelFrame(kb, shot, image, labels, new Dictionary<string, Rect>(), "user", "confirmed", null);
        kb.Commit("test", ["synthetic"]);
        return kb;
    }

    [Fact]
    public void Units_get_stable_sequential_ids()
    {
        var kb = BuildKb();
        Assert.Equal("M0006", kb.NextId(UnitKind.Monster));
        Assert.Equal("MS0001", kb.NextId(UnitKind.MonsterSummon));
        Assert.Equal("CS001", kb.NextId(UnitKind.CharacterSummon));
        Assert.NotNull(kb.FindUnit("CH-测试角色"));
    }

    [Fact]
    public void Recognizes_other_resolution_background_transparency_and_letterbox()
    {
        var kb = BuildKb();
        var recognizer = new Recognizer(kb);
        var slots = new Dictionary<string, int?>
        {
            ["current"] = 3, ["main2"] = 1, ["main3"] = 5, ["main4"] = 2, ["main5"] = null, ["insert1"] = null, ["insert2"] = null,
        };

        // 1280x720 game picture inside a 1280x800 video frame (letterbox), different background, more transparency, small offsets.
        using var frame = Synth.Frame(1280, 720, slots, backgroundSeed: 7, alpha: 0.8, letterbox: 40, jitter: 0.04);
        var result = recognizer.Recognize(frame);

        Assert.Null(result.Problem);
        Assert.Equal(40, result.Content.Y);
        var bySlot = result.Slots.ToDictionary(s => s.Slot);
        Assert.Equal("M0003", bySlot["current"].UnitId);
        Assert.Equal("M0001", bySlot["main2"].UnitId);
        Assert.Equal("M0005", bySlot["main3"].UnitId);
        Assert.Equal("M0002", bySlot["main4"].UnitId);
        // Empty cells on an unseen background must never be reported as some unit.
        Assert.Null(bySlot["main5"].UnitId);
        Assert.Null(bySlot["insert1"].UnitId);
        Assert.True(result.ElapsedMs < 2000);
    }

    [Fact]
    public void Optional_occupancy_threshold_marks_low_scoring_cells_empty()
    {
        var kb = BuildKb();
        kb.Settings.EmptyBelowScore = 0.45;
        var recognizer = new Recognizer(kb);
        using var frame = Synth.Frame(1280, 720, new Dictionary<string, int?> { ["current"] = 3 }, backgroundSeed: 7, alpha: 0.8);
        var result = recognizer.Recognize(frame).Slots.ToDictionary(s => s.Slot);
        Assert.Equal("M0003", result["current"].UnitId);
        Assert.True(result["main5"].IsEmpty, Describe(result["main5"]));
    }

    private static string Describe(SlotResult s) =>
        $"{s.Slot}: unit={s.UnitId ?? "-"} score={s.Score:0.000} candidates={string.Join(",", s.Candidates.Select(c => $"{c.UnitId}:{c.Score:0.000}"))}";

    [Fact]
    public void Unseen_avatar_is_unknown_not_guessed()
    {
        var kb = BuildKb();
        var recognizer = new Recognizer(kb);
        using var frame = Synth.Frame(1920, 1080, new Dictionary<string, int?> { ["current"] = 42 }, backgroundSeed: 9);
        var current = recognizer.Recognize(frame).Slots.Single(s => s.Slot == "current");
        Assert.False(current.IsEmpty);
        Assert.Null(current.UnitId);
        var text = ResultText.Frame(kb, recognizer.Recognize(frame), "x.png");
        Assert.Contains("未知/待确认", text);
    }

    [Fact]
    public void Missing_layout_is_reported_instead_of_guessing()
    {
        var kb = BuildKb();
        var recognizer = new Recognizer(kb);
        using var frame = Synth.Frame(1024, 768, new Dictionary<string, int?>(), backgroundSeed: 1);
        var result = recognizer.Recognize(frame);
        Assert.NotNull(result.Problem);
        Assert.Empty(result.Slots);
        Assert.Contains("未完成", ResultText.Frame(kb, result, "a.png"));
    }

    [Fact]
    public void Empty_fallback_works_without_empty_samples()
    {
        var kb = BuildKb(labelEmpty: false);
        var recognizer = new Recognizer(kb);
        using var frame = Synth.Frame(1920, 1080, new Dictionary<string, int?> { ["current"] = 1 }, backgroundSeed: 100);
        var result = recognizer.Recognize(frame);
        Assert.Equal("M0001", result.Slots.Single(s => s.Slot == "current").UnitId);
    }

    [Fact]
    public void Duplicate_samples_are_not_stored_twice()
    {
        var kb = BuildKb();
        var before = kb.Samples.Count;
        var path = Path.Combine(_dir.Path, "same.png");
        using (var frame = Synth.Frame(1920, 1080, new Dictionary<string, int?> { ["current"] = 1 }, backgroundSeed: 100))
        {
            ImageIO.WritePng(path, frame);
        }

        var shot = kb.AddScreenshot(path, new SourceInfo { Type = "synthetic", Origin = "same" }, null, "train", null, out _);
        using var image = kb.ReadScreenshot(shot);
        var details = KbLabeler.LabelFrame(kb, shot, image, new Dictionary<string, string> { ["current"] = "M0001" }, new Dictionary<string, Rect>(), "user", "confirmed", null);
        Assert.Equal(before, kb.Samples.Count);
        Assert.Contains("未重复保存", details[0]);
    }

    [Fact]
    public void Holdout_screenshots_get_truth_but_no_samples()
    {
        var kb = BuildKb();
        var before = kb.Samples.Count;
        var path = Path.Combine(_dir.Path, "holdout.png");
        using (var frame = Synth.Frame(1920, 1080, new Dictionary<string, int?> { ["current"] = 2 }, backgroundSeed: 55))
        {
            ImageIO.WritePng(path, frame);
        }

        var shot = kb.AddScreenshot(path, new SourceInfo { Type = "synthetic", Origin = "holdout" }, null, "holdout", null, out _);
        using var image = kb.ReadScreenshot(shot);
        KbLabeler.LabelFrame(kb, shot, image, new Dictionary<string, string> { ["current"] = "M0002" }, new Dictionary<string, Rect>(), "user", "confirmed", null);
        Assert.Equal(before, kb.Samples.Count);
        Assert.Equal("M0002", shot.Truth["current"]);
    }

    [Fact]
    public void Commit_bumps_version_and_survives_reload()
    {
        var kb = BuildKb();
        var version = kb.Version;
        var hash = kb.ContentHash();
        var reloaded = KnowledgeBase.Load(kb.Root);
        Assert.Equal(version, reloaded.Version);
        Assert.Equal(hash, reloaded.ContentHash());
        Assert.Equal(kb.Samples.Count, reloaded.Samples.Count);
        Assert.Contains($"## v{version}", File.ReadAllText(Path.Combine(kb.Root, "changelog.md")));
    }

    [Fact]
    public void Merge_moves_samples_and_keeps_alias()
    {
        var kb = BuildKb();
        var moved = kb.Merge("M0005", "M0004");
        Assert.Equal(1, moved);
        Assert.Equal("M0004", kb.ResolveUnit("M0005").Id);
        Assert.All(kb.Samples.Where(s => s.UnitId == "M0004"), s => Assert.True(File.Exists(Path.Combine(kb.Root, s.File))));
    }

    [Fact]
    public void Text_lists_main_axis_and_never_pending_inserts()
    {
        var kb = BuildKb();
        var recognizer = new Recognizer(kb);
        using var frame = Synth.Frame(1920, 1080, new Dictionary<string, int?> { ["current"] = 6, ["main2"] = 1, ["insert1"] = 2 }, backgroundSeed: 3);
        var text = ResultText.Frame(kb, recognizer.Recognize(frame), "a.png");
        Assert.Contains("1. 测试角色 —— 当前行动格", text);
        Assert.Contains("2. M0001", text);
        Assert.DoesNotContain("M0002", text);
        Assert.Contains("[插入行动]", text);
    }

    [Fact]
    public void Marker_entries_are_recognized_but_left_out_of_results()
    {
        var kb = BuildKb();
        kb.FindUnit("M0002")!.Kind = UnitKind.Marker;
        var recognizer = new Recognizer(kb);
        using var frame = Synth.Frame(1920, 1080, new Dictionary<string, int?> { ["current"] = 1, ["main2"] = 2, ["main3"] = 3 }, backgroundSeed: 11);
        var result = recognizer.Recognize(frame);
        Assert.Equal("M0002", result.Slots.Single(s => s.Slot == "main2").UnitId);

        var text = ResultText.Frame(kb, result, "a.png");
        Assert.DoesNotContain("M0002", text);
        Assert.Contains("2. M0003", text);

        var state = SequenceAnalyzer.ToState(0, result, kb.IsMarker)!;
        Assert.Equal(["M0003"], state.Main.Take(1));
    }

    [Fact]
    public void Attachment_cell_is_folded_into_the_entry_above()
    {
        var kb = BuildKb();
        var unit = kb.FindUnit("M0002")!;
        unit.Kind = UnitKind.Attachment;
        unit.Name = "附加行动格";
        var recognizer = new Recognizer(kb);
        using var frame = Synth.Frame(1920, 1080, new Dictionary<string, int?> { ["current"] = 1, ["main2"] = 2, ["main3"] = 3 }, backgroundSeed: 11);
        var result = recognizer.Recognize(frame);

        var text = ResultText.Frame(kb, result, "a.png");
        Assert.Contains("1. M0001（含附加行动格） —— 当前行动格", text);
        Assert.Contains("2. M0003", text);
        Assert.Equal(["M0003"], SequenceAnalyzer.ToState(0, result, kb.IsNonAction)!.Main.Take(1));
    }

    [Fact]
    public void Rel_rect_round_trips_and_scales_with_height()
    {
        var content = new Rect(0, 40, 1280, 720);
        var rel = RelRect.FromPixels(new Rect(26, 112, 64, 64), content);
        Assert.Equal(new Rect(26, 112, 64, 64), rel.ToPixels(content));
        var big = rel.ToPixels(new Rect(0, 0, 1920, 1080));
        Assert.Equal(96, big.Width);
    }

    [Fact]
    public void Content_detection_trims_letterbox_only()
    {
        using var boxed = Synth.Frame(1280, 720, new Dictionary<string, int?>(), 5, letterbox: 60);
        Assert.Equal(new Rect(0, 60, 1280, 720), Geometry.DetectContent(boxed));
        using var plain = Synth.Frame(1280, 720, new Dictionary<string, int?>(), 5);
        Assert.Equal(new Rect(0, 0, 1280, 720), Geometry.DetectContent(plain));
    }
}
