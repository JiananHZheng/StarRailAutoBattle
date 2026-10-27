using AxisRecognizer;
using OpenCvSharp;
using static AxisRecognizer.SequenceAnalyzer;

namespace AxisRecognizer.Tests;

public class SequenceTests
{
    private static readonly SlotResult Dummy = new("current", SlotKind.Current, 1, new Rect(), false, null, 0, []);

    private static AxisState S(double t, string current, string main, string inserts = "") =>
        new(t, current, Dummy, Split(main), Split(inserts));

    private static List<string> Split(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    [Fact]
    public void Normal_progression_numbers_main_actions_without_notes()
    {
        var actions = BuildActions([
            S(0, "A", "B C D"),
            S(1, "B", "C D A"),
            S(2, "C", "D A B"),
        ]);
        Assert.Equal(["1", "2", "3"], actions.Select(a => a.Label));
        Assert.All(actions.Skip(1), a => Assert.Empty(a.OrderChanges));
    }

    [Fact]
    public void Insert_from_right_side_is_numbered_under_current_main_action()
    {
        var actions = BuildActions([
            S(0, "A", "B C D"),
            S(1, "B", "C D A", "X"),
            S(2, "X", "C D A B"),
            S(3, "C", "D A B"),
        ]);
        Assert.Equal(["1", "2", "2-1", "3"], actions.Select(a => a.Label));
        Assert.Empty(actions[2].OrderChanges);
        Assert.Empty(actions[3].OrderChanges);
    }

    [Fact]
    public void Two_inserts_get_sequential_numbers_and_reset_after_next_main()
    {
        var actions = BuildActions([
            S(0, "A", "B C", "X Y"),
            S(1, "X", "B C A", "Y"),
            S(2, "Y", "B C A"),
            S(3, "B", "C A"),
            S(4, "C", "A B", "X"),
            S(5, "X", "A B C"),
        ]);
        Assert.Equal(["1", "1-1", "1-2", "2", "3", "3-1"], actions.Select(a => a.Label));
    }

    [Fact]
    public void Unit_jumping_ahead_in_queue_is_an_order_change_with_places_moved()
    {
        // During A's action, D moves from 3rd to 1st place in the queue.
        var actions = BuildActions([
            S(0, "A", "B C D E"),
            S(1, "A", "D B C E"),
        ]);
        var change = Assert.Single(actions[0].OrderChanges);
        Assert.Equal("D", change.Unit);
        Assert.Equal(2, change.Shift);
    }

    [Fact]
    public void Delayed_unit_is_reported_as_moved_back()
    {
        var actions = BuildActions([
            S(0, "A", "B C D E"),
            S(1, "B", "D A C E"),
        ]);
        // Expected after B acts: C D E (+A somewhere). Actual: D C E → C moved back 1 (or D forward 1).
        var change = Assert.Single(actions[1].OrderChanges);
        Assert.Equal(1, Math.Abs(change.Shift));
    }

    [Fact]
    public void Next_actor_that_was_not_first_in_queue_is_flagged()
    {
        var actions = BuildActions([
            S(0, "A", "B C D"),
            S(1, "C", "B D A"),
        ]);
        Assert.Contains(actions[1].OrderChanges, c => c.Unit == "C" && c.Shift == 1);
    }

    [Fact]
    public void Duplicate_icons_are_handled_without_false_moves()
    {
        var actions = BuildActions([
            S(0, "M1", "M1 A M2 M1"),
            S(1, "M1", "A M2 M1 M1"),
        ]);
        // Same current token: is this a new action? Treated as order observation during the same action, with no move of A.
        Assert.DoesNotContain(actions[0].OrderChanges, c => c.Unit == "A");
    }

    [Fact]
    public void Unknown_current_is_flagged()
    {
        var actions = BuildActions([
            S(0, "A", "B C"),
            S(1, UnknownToken, "C A"),
        ]);
        Assert.Contains(actions[1].Notes, n => n.Contains("未识别"));
    }

    [Fact]
    public void Insert_type_unit_from_main_queue_is_numbered_as_insert_without_order_change()
    {
        // 阿哈时刻 (AHA) waits in the main queue like an ordinary cell but its turn is an inserted one.
        var actions = BuildActions(
            [
                S(0, "A", "B AHA C"),
                S(1, "B", "AHA C A"),
                S(2, "AHA", "C A B"),
                S(3, "C", "A B"),
            ],
            actsAsInsert: id => id == "AHA");
        Assert.Equal(["1", "2", "2-1", "3"], actions.Select(a => a.Label));
        Assert.Empty(actions[2].OrderChanges);
        Assert.Empty(actions[3].OrderChanges);
    }

    [Fact]
    public void Battle_start_is_detected_when_axis_appears_after_frames_without_it()
    {
        var analyzer = new SequenceAnalyzer(null!, stableFrames: 2);
        FrameResult F(bool axis, string current = "A", params string[] main)
        {
            var slots = new List<SlotResult> { new("current", SlotKind.Current, 1, new Rect(), false, current, 1, []) };
            slots.AddRange(main.Select((m, i) => new SlotResult($"main{i + 2}", SlotKind.Main, i + 2, new Rect(), false, m, 1, [])));
            return new FrameResult(100, 100, new Rect(0, 0, 100, 100), "L", null, slots, 1, axis);
        }

        var analysis = analyzer.Analyze([
            (0.0, F(false)), (0.5, F(false)), (1.0, F(false)),            // opening animation: no axis
            (1.5, F(true, "A", "B", "C")), (2.0, F(true, "A", "B", "C")),
            (2.5, F(true, "B", "C", "A")), (3.0, F(true, "B", "C", "A")),
        ]);
        Assert.Equal(1.5, analysis.BattleStart);
        Assert.Equal(["1", "2"], analysis.Actions.Select(a => a.Label));
        Assert.Contains("战斗开始", analysis.Actions[0].Notes[0]);

        var noStart = analyzer.Analyze([(0.0, F(true, "A", "B")), (0.5, F(true, "A", "B"))]);
        Assert.Null(noStart.BattleStart);
        Assert.Contains("未检测到战斗开始", noStart.Actions[0].Notes[0]);
    }

    [Fact]
    public void Frame_counts_follow_time_spans_at_any_sampling_rate()
    {
        var fast = ForInterval(null!, 1.0 / 15);
        Assert.Equal(5, fast.StableFrames);              // 0.3 s
        Assert.Equal(15, fast.BattleStartAbsentFrames);  // 1 s
        var slow = ForInterval(null!, 0.5);
        Assert.Equal(2, slow.StableFrames);
        Assert.Equal(2, slow.BattleStartAbsentFrames);
    }

    [Fact]
    public void Compare_order_ignores_units_outside_visible_window()
    {
        var changes = CompareOrder(["B", "C", "D", "E", "F"], ["B", "C", "D", "G"]);
        Assert.Empty(changes);
    }

    [Fact]
    public void Stable_frames_filter_transitions()
    {
        var analyzer = new SequenceAnalyzer(null!, stableFrames: 2);
        FrameResult F(string current, params string[] main)
        {
            var slots = new List<SlotResult> { new("current", SlotKind.Current, 1, new Rect(), false, current, 1, []) };
            slots.AddRange(main.Select((m, i) => new SlotResult($"main{i + 2}", SlotKind.Main, i + 2, new Rect(), false, m, 1, [])));
            return new FrameResult(100, 100, new Rect(0, 0, 100, 100), "L", null, slots, 1);
        }

        var analysis = analyzer.Analyze([
            (0.0, F("A", "B", "C")), (0.5, F("A", "B", "C")),
            (1.0, F("B", "C", "A")),                          // one transitional frame only
            (1.5, F("C", "A", "B")), (2.0, F("C", "A", "B")),
        ]);
        Assert.Equal(["A", "C"], analysis.Actions.Select(a => a.State.Current));
    }
}
