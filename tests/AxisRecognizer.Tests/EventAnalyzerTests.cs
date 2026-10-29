using AxisRecognizer;
using OpenCvSharp;

namespace AxisRecognizer.Tests;

public class EventAnalyzerTests
{
    private const double Step = 1.0 / 15;

    private static readonly CueReading NoText = new(CueNames.PhaseText, null, null, 0, null, 0);

    /// <summary>A frame of the battle: unit in the current cell, its marker and the other cues.</summary>
    private sealed record F(
        string? Unit = null,
        string? Marker = null,
        bool Axis = true,
        double White = 0.3,
        double Start = 0,
        string? Banner = null,
        int Dots = 0,
        string? Bar = null,
        string? Selection = null,
        string? Phase = null,
        int Cards = 0,
        double? Lit2 = null,
        double? Lit3 = null);

    private static IReadOnlyList<BattleEvent> Run(EventAnalyzer analyzer, IEnumerable<(F Frame, int Count)> script)
    {
        var t = 0.0;
        foreach (var (f, count) in script)
        {
            for (var i = 0; i < count; i++, t += Step)
            {
                var cues = new ScreenCueFrame(
                    f.Axis && f.Marker is not null ? new CueReading(CueNames.Marker, f.Marker, f.Marker, 0.9, null, 0) : null,
                    f.Phase is null ? NoText : new CueReading(CueNames.PhaseText, f.Phase, f.Phase, 0.9, null, 0),
                    150, 80, f.Start, f.Selection, f.Banner, f.Banner is null ? 0 : 0.9, 0.84, f.Dots,
                    f.Bar is null ? null : new AvBarReading(int.Parse(f.Bar.Split('|')[0]), int.Parse(f.Bar.Split('|')[1]), f.Bar, 0.95, new Rect()),
                    Enumerable.Repeat(new Rect(), f.Cards).ToList(),
                    f.White);
                var buttons = new List<UltButton>();
                if (f.Lit2 is { } lit2)
                {
                    buttons.Add(new UltButton(2, 0.48, 0.83, 0.04, lit2, lit2));
                }

                if (f.Lit3 is { } lit3)
                {
                    buttons.Add(new UltButton(3, 0.69, 0.83, 0.04, lit3, lit3));
                }

                analyzer.Feed(new FrameObservation(t, f.Axis, f.Axis ? f.Unit : null, cues) { UltButtons = buttons });
            }
        }

        analyzer.Finish();
        return analyzer.Events;
    }

    private static List<string> Lines(IEnumerable<BattleEvent> events) =>
        events.Select(e => e.Kind == BattleEventKind.Action ? $"{e.Index} {e.UnitId}" : e.Index).ToList();

    private static readonly (F, int) BattleStart = (new F(Axis: false, Start: 1), 3);
    private static readonly (F, int) Wave1Banner = (new F(Axis: false, Banner: "battle-start"), 3);

    [Fact]
    public void Main_actions_are_numbered_and_enemy_turns_left_out()
    {
        var events = Run(new EventAnalyzer(), [
            BattleStart, Wave1Banner,
            (new F("A", "ally-main"), 10),
            (new F("B", "ally-main"), 10),
            (new F("M", "enemy-main"), 10),
            (new F("C", "ally-main"), 10),
        ]);

        Assert.Equal(["战斗开始", "1 A", "2 B", "3 C"], Lines(events));
    }

    [Fact]
    public void A_flash_splits_two_turns_of_the_same_unit_and_a_light_beam_does_not()
    {
        var events = Run(new EventAnalyzer(), [
            BattleStart,
            (new F("A", "ally-main"), 10),
            (new F("A", "ally-main", White: 0.97), 2),      // turn-start flash
            (new F("A", "ally-main"), 10),
            (new F("A", "ally-main", White: 0.47), 2),      // an ultimate's beam over the cell
            (new F("A", "ally-main"), 10),
        ]);

        Assert.Equal(["战斗开始", "1 A", "2 A"], Lines(events));
    }

    [Fact]
    public void A_press_then_a_star_turn_of_that_unit_is_one_ultimate_and_a_star_alone_gets_no_skill()
    {
        // R29, R52, R14. Slot 2 (U) is ready, goes out (pressed); its ultimate then shows as a star turn of U.
        var events = Run(new EventAnalyzer { Team = [null, "U", null, null] }, [
            BattleStart,
            (new F("A", "ally-main", Lit2: 0.8), 30),
            (new F("A", "ally-main", Lit2: 0.0), 20),        // pressed: out, held
            (new F(Axis: false), 20),                        // cut-in
            (new F("U", "ally-extra", Lit2: 0.0), 10),       // the same ultimate in the current cell
            (new F("V", "ally-extra", Lit2: 0.0), 10),       // another extra action, no press seen
            (new F("B", "ally-main", Lit2: 0.0), 10),
        ]);

        Assert.Equal(["战斗开始", "1 A", "1-1 U", "1-2 V", "2 B"], Lines(events));
        Assert.Equal((SkillKind.Ultimate, 2), (events.Single(e => e.UnitId == "U").Skill, events.Single(e => e.UnitId == "U").TeamSlot));
        Assert.Null(events.Single(e => e.UnitId == "V").Skill);
    }

    [Fact]
    public void Insert_cards_alone_never_count_as_ultimates()
    {
        // R45: a card shows, but no team-bar button goes out.
        var events = Run(new EventAnalyzer { Team = [null, "U", null, null] }, [
            BattleStart,
            (new F("A", "ally-main", Lit2: 0.8), 30),
            (new F("A", "ally-main", Cards: 1, Lit2: 0.8), 20),
            (new F("B", "ally-main", Lit2: 0.8), 10),
        ]);

        Assert.Equal(["战斗开始", "1 A", "2 B"], Lines(events));
    }

    [Fact]
    public void Two_presses_during_one_main_action_are_its_extras_in_order_even_when_confirmed_late()
    {
        // R51, R11: both buttons go out while A runs and the ultimates never enter the current cell. The second press is
        // confirmed only after B has started; it still hangs under A.
        var events = Run(new EventAnalyzer { Team = [null, "U2", "U3", null] }, [
            BattleStart,
            (new F("A", "ally-main", Lit2: 0.8, Lit3: 0.8), 30),
            (new F("A", "ally-main", Lit2: 0.0, Lit3: 0.8), 6),
            (new F("A", "ally-main", Lit2: 0.0, Lit3: 0.0), 10),
            (new F("B", "ally-main", Lit2: 0.0, Lit3: 0.0), 30),
        ]);

        Assert.Equal(["战斗开始", "1 A", "1-1 U2", "1-2 U3", "2 B"], Lines(events));
        Assert.All(events.Where(e => e.StepKind == StepKind.Extra), e => Assert.Equal(SkillKind.Ultimate, e.Skill));
    }

    [Fact]
    public void Without_a_team_a_press_marks_only_its_slot_and_a_flicker_is_not_a_press()
    {
        // R30: no team → no unit guessed. R29: a short flicker out is not a press.
        var analyzer = new EventAnalyzer();
        var events = Run(analyzer, [
            BattleStart,
            (new F("A", "ally-main", Lit2: 0.8), 30),
            (new F("A", "ally-main", Lit2: 0.0), 5),         // flicker: not held for a second
            (new F("A", "ally-main", Lit2: 0.8), 30),
            (new F("A", "ally-main", Lit2: 0.0), 30),        // pressed
            (new F("B", "ally-main", Lit2: 0.0), 10),
        ]);

        var ultimate = events.Single(e => e.StepKind == StepKind.Extra);
        Assert.Equal(("1-1", (string?)null, (int?)2), (ultimate.Index, ultimate.UnitId, ultimate.TeamSlot));
        Assert.Single(analyzer.Trace, line => line.Contains("按钮熄灭", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_later_banners_change_the_wave_and_numbering_restarts()
    {
        var events = Run(new EventAnalyzer(), [
            BattleStart, Wave1Banner,
            (new F("A", "ally-main"), 10),
            (new F(Axis: false), 70),                        // the banner of wave 1 is long gone
            (new F(Axis: false, Banner: "battle-start"), 3),
            (new F("B", "ally-main"), 10),
            (new F(Axis: false, Banner: "battle-end"), 3),
        ]);

        Assert.Equal(["战斗开始", "1 A", "换波", "1 B", "战斗结束"], Lines(events));
        Assert.Equal(2, events.Single(e => e.UnitId == "B").Segment);
    }

    [Fact]
    public void A_phase_change_needs_one_fewer_dot_held_for_two_seconds()
    {
        var events = Run(new EventAnalyzer(), [
            BattleStart,
            (new F("A", "ally-main", Dots: 3), 40),
            (new F("A", "ally-main", Dots: 2), 8),           // an effect hides a dot for half a second
            (new F("A", "ally-main", Dots: 3), 10),
            (new F("B", "ally-main", Dots: 3), 10),
            (new F("B", "ally-main", Dots: 2), 15),          // B breaks the phase: one dot fewer, held…
            (new F("C", "ally-main", Dots: 2), 30),          // …and confirmed only during C's turn
            (new F("D", "ally-main", Dots: 2), 10),
        ]);

        // B dealt the blow and stays in the old phase; C started after the drop and moves into the new one.
        Assert.Equal(["战斗开始", "1 A", "2 B", "转阶段", "1 C", "2 D"], Lines(events));
    }

    [Fact]
    public void Readings_need_agreeing_frames_and_the_skill_is_the_last_selection_before_running()
    {
        var events = Run(new EventAnalyzer(), [
            BattleStart,
            (new F("A", "ally-main", Selection: "basic", Bar: "2|194"), 10),
            (new F("A", "ally-main", Selection: "skill", Bar: "2|194"), 2),
            (new F("A", "ally-main", Phase: "ally", Bar: "2|184"), 1),
            (new F("A", "ally-main", Phase: "ally"), 5),
        ]);

        var action = events.Single(e => e.Kind == BattleEventKind.Action);
        Assert.Equal(SkillKind.Skill, action.Skill);
        Assert.Equal((2, 194), (action.Round, action.ActionValue));
    }
}
