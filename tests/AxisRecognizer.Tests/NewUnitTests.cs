using AxisRecognizer;
using static AxisRecognizer.NewUnitPromotion;

namespace AxisRecognizer.Tests;

public class NewUnitTests
{
    private static readonly RecognitionSettings Settings = new();

    private static AvatarFeature Feature(int seed)
    {
        using var avatar = Synth.Avatar(seed);
        return AvatarFeature.Compute(avatar, Settings);
    }

    [Fact]
    public void Same_avatar_keeps_one_temporary_id_and_rare_ones_are_dropped_as_noise()
    {
        var clusterer = new NewUnitClusterer(Settings) { MinCells = 5 };
        var a = Feature(1);
        var b = Feature(2);
        var rare = Feature(3);
        var t = 0.0;
        foreach (var feature in new[] { b, a, b, a, a, b, a, b, a, b, rare, rare })
        {
            clusterer.Assign(feature, t += 0.1, "main2", null, out _);
        }

        var groups = clusterer.Finish();
        Assert.Equal(["新01", "新02"], groups.Select(g => g.TempId));
        Assert.Equal(5, groups[0].Cells);   // b appeared first
        Assert.Equal(5, groups[1].Cells);
        Assert.Equal((1, 2), clusterer.Dropped);
    }

    [Fact]
    public void Flicker_back_to_the_same_group_links_the_two_groups()
    {
        var clusterer = new NewUnitClusterer(Settings) { MinCells = 1 };
        var a = clusterer.Assign(Feature(1), 0, "main3", null, out _);
        var b = clusterer.Assign(Feature(2), 0, "main4", null, out _);
        var c = clusterer.Assign(Feature(3), 0, "main5", null, out _);
        // main3 shows a, b, a, b, b, a: twice "b for a moment, then a again". main5 only switches once (a real move).
        var cells = new List<(int, string, NewUnitGroup)>
        {
            (0, "main3", a), (1, "main3", b), (2, "main3", a), (3, "main3", b), (4, "main3", b), (5, "main3", a),
            (0, "main5", c), (1, "main5", c), (2, "main5", a), (3, "main5", a),
        };

        var pairs = VideoRecognition.FlickerPairs(cells.Select(c => (c.Item1, c.Item2, (object)c.Item3)));
        var pair = Assert.Single(pairs);
        Assert.Equal(new HashSet<object> { a, b }, new HashSet<object> { pair.A, pair.B });
        Assert.Equal(3, pair.Times);   // a-b-a, b-a-b, a-bb-a

        clusterer.Link((NewUnitGroup)pair.A, (NewUnitGroup)pair.B);
        Assert.Equal(2, clusterer.Finish().Count);   // a+b together, c on its own
    }

    [Fact]
    public void Flicker_with_a_known_unit_is_noted_but_not_merged()
    {
        var clusterer = new NewUnitClusterer(Settings) { MinCells = 1 };
        var g = clusterer.Assign(Feature(1), 0, "main7", null, out _);
        var cells = new List<(int, string, object)>
        {
            (0, "main7", "U0007"), (1, "main7", g), (2, "main7", "U0007"), (3, "main7", g), (4, "main7", g), (5, "main7", "U0007"),
        };

        var (a, b, times) = Assert.Single(VideoRecognition.FlickerPairs(cells));
        Assert.Same(g, a);
        Assert.Equal("U0007", b);
        clusterer.NoteKnownFlicker(g, "U0007", times);
        Assert.Equal(("U0007", 3), Assert.Single(clusterer.Finish()).FlickersWithKnown);
    }

    [Fact]
    public void Marker_answers_create_non_unit_entries()
    {
        var target = Assert.Single(Plan(Parse("新22 = marker 剩余行动条")));
        Assert.Equal(UnitKind.Marker, target.Kind);
        Assert.Equal("剩余行动条", target.NewName);
    }

    [Fact]
    public void Current_cell_group_is_linked_to_the_unit_that_came_from_main_cell_two()
    {
        var clusterer = new NewUnitClusterer(Settings) { MinCells = 1 };
        clusterer.Assign(Feature(1), 0, "current", null, out _);
        var groups = clusterer.Finish();
        var current = groups.Single().TempId;

        static FrameResult Frame(string? current, string? main2) => new(100, 100, new OpenCvSharp.Rect(0, 0, 100, 100), "L", null,
            [
                new SlotResult("current", SlotKind.Current, 1, default, false, current, 1, []),
                new SlotResult("main2", SlotKind.Main, 2, default, false, main2, 1, []),
            ], 1);

        // Twice: U0007 waits in main cell 2, then the current cell shows the new group for a while.
        var frames = new List<(double, FrameResult)>();
        for (var round = 0; round < 2; round++)
        {
            frames.Add((frames.Count, Frame("U0001", "U0007")));
            frames.AddRange(Enumerable.Range(0, 6).Select(_ => ((double)frames.Count, Frame(current, "U0002"))));
        }

        VideoRecognition.LinkCurrentCellGroups(frames, groups);
        Assert.Equal(("U0007", 2, 2), groups.Single().EnteredFrom);
    }

    [Fact]
    public void Examples_are_requested_only_for_new_looks_and_up_to_the_limit()
    {
        var clusterer = new NewUnitClusterer(Settings) { MaxExamples = 2 };
        var feature = Feature(4);
        var group = clusterer.Assign(feature, 0, "current", null, out var first);
        clusterer.AddExample(group, new NewUnitExample(0, "current", new OpenCvSharp.Rect(0, 0, 10, 10), "frames/a.jpg"));
        clusterer.Assign(feature, 0.1, "current", null, out var repeat);
        Assert.True(first);
        Assert.False(repeat);   // identical look: nothing new to keep
    }

    [Fact]
    public void Known_unit_resemblance_is_reported_only_above_the_suspect_score()
    {
        var clusterer = new NewUnitClusterer(Settings) { MinCells = 1 };
        clusterer.Assign(Feature(5), 0, "main2", new Candidate("U0012", 0.72), out _);
        clusterer.Assign(Feature(6), 0, "main3", new Candidate("U0013", 0.55), out _);
        var groups = clusterer.Finish();
        Assert.Equal("U0012", groups[0].SuspectedKnown!.UnitId);
        Assert.Null(groups[1].SuspectedKnown);
    }

    [Fact]
    public void New_units_table_round_trips()
    {
        var clusterer = new NewUnitClusterer(Settings) { MinCells = 1 };
        var group = clusterer.Assign(Feature(7), 12.5, "current", null, out _);
        clusterer.AddExample(group, new NewUnitExample(12.5, "current", new OpenCvSharp.Rect(1, 2, 30, 40), "frames/t0012.500.jpg"));
        var rows = NewUnitFiles.ParseTable(NewUnitFiles.Table(clusterer.Finish()));
        var row = Assert.Single(rows);
        Assert.Equal("新01", row.TempId);
        Assert.Equal(new NewUnitExample(12.5, "current", new OpenCvSharp.Rect(1, 2, 30, 40), "frames/t0012.500.jpg"), row.Examples.Single());
    }

    [Fact]
    public void Promotion_answers_join_same_as_chains_into_one_target()
    {
        var plan = Plan(Parse("""
            # 用户核对
            新01 = new 昔涟
            新02 = U0012
            新03 = 新01
            新04 ＝ skip
            新05 = new
            新06 = 新03
            """));

        Assert.Equal(3, plan.Count);
        var xilian = plan.Single(t => t.NewName == "昔涟");
        Assert.Equal(["新01", "新03", "新06"], xilian.TempIds);
        Assert.Equal("U0012", plan.Single(t => t.TempIds.SequenceEqual(["新02"])).ExistingUnit);
        Assert.Contains(plan, t => t.TempIds.SequenceEqual(["新05"]) && t.NewName is null && t.ExistingUnit is null);
    }

    [Theory]
    [InlineData("新01 = U0001\n新01 = new")]   // the same temporary id twice
    [InlineData("U0001 = new")]                // left side is not a temporary id
    [InlineData("新01x = new")]
    [InlineData("新01 =")]                      // nothing after =
    [InlineData("新01 U0001")]                  // no =
    public void Malformed_lines_are_rejected(string text) =>
        Assert.Throws<FormatException>(() => Parse(text));

    [Theory]
    [InlineData("新01 = 新02")]                  // 新02 has no line of its own
    [InlineData("新01 = 新02\n新02 = 新01")]      // a loop with no outcome
    [InlineData("新01 = skip\n新02 = 新01")]      // "same as noise"
    public void Inconsistent_answers_are_rejected(string text) =>
        Assert.Throws<FormatException>(() => Plan(Parse(text)));
}
