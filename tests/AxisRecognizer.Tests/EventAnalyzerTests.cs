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
        int Cards = 0);

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
                analyzer.Feed(new FrameObservation(t, f.Axis, f.Axis ? f.Unit : null, cues)
                {
                    InsertUnits = Enumerable.Repeat<string?>(null, f.Cards).ToList(),
                });
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
    public void Star_turns_are_extra_actions_and_only_a_started_ultimate_is_an_ultimate()
    {
        var events = Run(new EventAnalyzer(), [
            BattleStart,
            (new F("A", "ally-main"), 10),
            (new F("A", "ally-main", Cards: 1), 8),          // ultimate pressed: card shows
            (new F(Axis: false), 20),                        // cut-in
            (new F("U", "ally-extra"), 10),                  // the same ultimate in the current cell
            (new F("V", "ally-extra"), 10),                  // another ultimate, no card seen
            (new F("B", "ally-main"), 10),
        ]);

        Assert.Equal(["战斗开始", "1 A", "1-1 U", "1-2 V", "2 B"], Lines(events));
        // Only the ultimate whose start (the card) was seen is an ultimate; other extra actions keep no skill.
        Assert.Equal(SkillKind.Ultimate, events.Single(e => e.UnitId == "U").Skill);
        Assert.Null(events.Single(e => e.UnitId == "V").Skill);
    }

    [Fact]
    public void A_team_slot_whose_ultimate_button_goes_out_names_the_card_and_flickers_do_not_count()
    {
        // Slot 2's button: ready (lit) for a while, flickers out for a moment, then pressed with a card showing.
        var ready = new[] { new UltButton(2, 0.48, 0.83, 0.04, 0.8, 0.7) };
        var dark = new[] { new UltButton(2, 0.48, 0.83, 0.04, 0.0, 0.0) };
        var analyzer = new EventAnalyzer { Team = [null, "U2", null, null] };
        var t = 0.0;
        void Frames(int n, string unit, string marker, UltButton[] buttons, int cards = 0)
        {
            for (var i = 0; i < n; i++, t += Step)
            {
                var cues = new ScreenCueFrame(new CueReading(CueNames.Marker, marker, marker, 0.9, null, 0), NoText, 150, 80, i == 0 && t == 0 ? 1 : 0,
                    InsertCards: Enumerable.Repeat(new Rect(), cards).ToList(), CellWhite: 0.3);
                analyzer.Feed(new FrameObservation(t, true, unit, cues)
                {
                    InsertUnits = Enumerable.Repeat<string?>(null, cards).ToList(),
                    UltButtons = buttons,
                });
            }
        }

        analyzer.Feed(new FrameObservation(t, false, null, new ScreenCueFrame(null, NoText, 0, 0, 1)));
        analyzer.Feed(new FrameObservation(t += Step, false, null, new ScreenCueFrame(null, NoText, 0, 0, 1)));
        Frames(30, "A", "ally-main", ready);
        Frames(5, "A", "ally-main", dark);            // a flicker: not held for a second
        Frames(30, "A", "ally-main", ready);
        Frames(10, "A", "ally-main", dark, cards: 1); // pressed: the card shows, the button goes out
        Frames(30, "A", "ally-main", dark);
        Frames(120, "B", "ally-main", dark);          // long after, so the press window has closed
        analyzer.Finish();

        var card = analyzer.Events.Single(e => e.FromCard);
        Assert.Equal(("U2", 2), (card.UnitId, card.TeamSlot));
        Assert.Equal(SkillKind.Ultimate, card.Skill);
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
