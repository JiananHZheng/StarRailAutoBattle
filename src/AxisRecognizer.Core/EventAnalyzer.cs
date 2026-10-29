namespace AxisRecognizer;

public enum BattleEventKind
{
    BattleStart,
    WaveChange,
    PhaseChange,
    Action,
    BattleEnd,
}

/// <summary>What one frame showed: the current cell's unit (null when unknown) and the other screen cues.</summary>
public sealed record FrameObservation(double Time, bool AxisFound, string? CurrentUnit, ScreenCueFrame Cues)
{
    /// <summary>The team bar's ultimate buttons seen in this frame (missing slots were not found; R29).</summary>
    public IReadOnlyList<UltButton> UltButtons { get; init; } = [];
}

/// <summary>
/// One event of the battle. For actions, <see cref="Skill"/>, <see cref="Round"/> and <see cref="ActionValue"/> are
/// filled in while the action runs (they may stay null when unclear); everything else is fixed when it is emitted.
/// </summary>
public sealed class BattleEvent
{
    public required BattleEventKind Kind { get; init; }
    public required double Time { get; init; }

    /// <summary>
    /// Segment and numbering can still change after the event was emitted: a phase change is only confirmed once the
    /// lower dot count has held, and actions that started after the drop move into the new phase.
    /// </summary>
    public required int Segment { get; set; }

    public StepKind? StepKind { get; init; }
    public int Main { get; set; }
    public int Extra { get; set; }

    /// <summary>Settable: an ultimate seen first as an insert card gets its unit when it enters the current cell.</summary>
    public string? UnitId { get; set; }

    /// <summary>Team slot (1–4) whose ultimate button went out with this action (R29).</summary>
    public int? TeamSlot { get; set; }
    public SkillKind? Skill { get; set; }
    public int? Round { get; set; }
    public int? ActionValue { get; set; }
    public List<string> Notes { get; } = [];

    /// <summary>"3", "3-1" for actions; the kind otherwise.</summary>
    public string Index => Kind switch
    {
        BattleEventKind.Action => Extra == 0 ? $"{Main}" : $"{Main}-{Extra}",
        BattleEventKind.WaveChange => "换波",
        BattleEventKind.PhaseChange => "转阶段",
        BattleEventKind.BattleStart => "战斗开始",
        _ => "战斗结束",
    };
}

/// <summary>
/// Turns per-frame observations into battle events, one frame at a time (so the same code serves a recorded video
/// and, later, live play). Rules (docs/score-retry-check.md):
/// <list type="bullet">
/// <item>A turn starts at the current cell's white flash, or when its marker or unit changes and stays changed.</item>
/// <item>A turn's kind comes from the marker it showed most: teal dot = main action n, teal star = extra action n-i
/// (ultimates, under the latest main action; 0-i before the first), red dot / red diamond = enemy, not recorded.</item>
/// <item>The first "战斗开始" banner after the battle start is wave 1; each later one is a wave change. One fewer lit
/// boss phase dot is a phase change. Both restart the numbering.</item>
/// <item>A reading needs several agreeing frames; single-frame misreads are ignored.</item>
/// </list>
/// </summary>
public sealed class EventAnalyzer
{
    /// <summary>Frames that must agree before a marker, unit, banner or reading counts.</summary>
    public int StableFrames { get; init; } = 3;

    /// <summary>
    /// The flash: at least this share of the current cell is near white (measured on 铁墓: flashes 0.96–1.00, an
    /// ultimate's light beam 0.47, a white portrait about 0.35). Mean saturation alone could not tell flash and beam apart.
    /// </summary>
    public double FlashWhiteShare { get; init; } = 0.8;

    /// <summary>A change right after a turn started belongs to that turn (the flash and the new marker arrive together).</summary>
    public double MinTurnSeconds { get; init; } = 0.4;

    /// <summary>Banners are shown for over a second and can be cut by animations: later matches are the same banner.</summary>
    public double BannerSeconds { get; init; } = 4;

    public double BannerScore { get; init; } = 0.75;

    /// <summary>
    /// How long a phase-dot count must hold before it counts. Effects can hide one dot for about half a second
    /// (铁墓 0:47.6–0:48.5 read 2 of 3); a real phase change stays lower. Frames without dots (0) don't break the hold.
    /// </summary>
    public double PhaseDotSeconds { get; init; } = 2.0;

    private readonly List<BattleEvent> _events = [];
    private readonly List<string> _problems = [];

    // Battle and numbering.
    private bool _inBattle;
    private bool _sawBattleStart;
    private bool _expectFirstBanner;
    private int _segment = 1;
    private int _main;
    private int _extra;

    // Current turn.
    private Turn? _turn;
    private bool _flashing;

    // Recent frames for stability checks.
    private readonly List<FrameObservation> _recent = [];
    private int _battleStartRun;
    private string? _bannerRunLabel;
    private int _bannerRun;
    private readonly Dictionary<string, double> _lastBannerTime = [];
    private int _dotCandidate;
    private double _dotSince;
    private bool _dotHeld;
    private int _dotStable;

    // Team-bar ultimate buttons (R29): per slot, the held state and when it first left a held ready stretch; press
    // ultimates not yet seen in the current cell (R52).
    private readonly Dictionary<int, (string State, double Since)> _slotRun = [];
    private readonly Dictionary<int, bool> _slotReady = [];
    private readonly Dictionary<int, double> _slotLastReady = [];
    private readonly List<BattleEvent> _pressUltimates = [];

    /// <summary>All events in time order (ultimates confirmed after their press are numbered back in time, R51).</summary>
    public IReadOnlyList<BattleEvent> Events => _events.OrderBy(e => e.Time).ToList();

    /// <summary>Why each turn started and how it was decided (developer view, for checking the rules on real videos).</summary>
    public List<string> Trace { get; } = [];

    /// <summary>Things the analyzer could not settle (shown with the result, never guessed away).</summary>
    public IReadOnlyList<string> Problems => _problems;

    /// <summary>The action now running, if one has been decided (its readings may still be filling in).</summary>
    public BattleEvent? CurrentAction => _turn?.Event;

    private sealed class Turn(double start)
    {
        public double Start { get; } = start;
        public Dictionary<string, int> Markers { get; } = [];
        public Dictionary<string, int> Units { get; } = [];
        public Dictionary<string, int> Readings { get; } = [];
        public string? LastSelection { get; set; }
        public SkillKind? Skill { get; set; }
        public bool Executing { get; set; }
        public BattleEvent? Event { get; set; }
        public bool Dropped { get; set; }
        public int Frames { get; set; }

        public static string? Top(Dictionary<string, int> votes, int min) =>
            votes.Count == 0 ? null : votes.MaxBy(v => v.Value) is { Value: var n, Key: var k } && n >= min ? k : null;
    }

    /// <summary>Feeds one frame; returns the events it completed (actions are returned when their kind and unit are decided).</summary>
    public IReadOnlyList<BattleEvent> Feed(FrameObservation frame)
    {
        var emitted = new List<BattleEvent>();
        _recent.Add(frame);
        if (_recent.Count > 12)
        {
            _recent.RemoveAt(0);
        }

        var cues = frame.Cues;

        // Battle start: black screen with a white line, held for a few frames.
        _battleStartRun = cues.BattleStart >= 0.9 ? _battleStartRun + 1 : 0;
        if (_battleStartRun == 2)
        {
            EndTurn();
            _inBattle = true;
            _sawBattleStart = true;
            _expectFirstBanner = true;
            (_segment, _main, _extra) = (1, 0, 0);
            (_dotStable, _dotCandidate, _dotHeld) = (0, 0, false);
            emitted.Add(Emit(BattleEventKind.BattleStart, frame.Time));
        }

        // Banners: wave start, battle end, result.
        var label = cues.Banner >= BannerScore ? cues.BannerLabel : null;
        _bannerRun = label is not null && label == _bannerRunLabel ? _bannerRun + 1 : label is null ? 0 : 1;
        _bannerRunLabel = label;
        if (_bannerRun == 2 && label is not null && frame.Time - _lastBannerTime.GetValueOrDefault(label, double.NegativeInfinity) > BannerSeconds)
        {
            _lastBannerTime[label] = frame.Time;
            if (label == "battle-start")
            {
                if (!_inBattle)
                {
                    StartImplicitly(frame.Time, "未检测到黑屏白线，从第一条“战斗开始”横幅算起");
                    _expectFirstBanner = true;
                }

                if (_expectFirstBanner)
                {
                    _expectFirstBanner = false;
                }
                else
                {
                    emitted.Add(NewSegment(BattleEventKind.WaveChange, frame.Time));
                }
            }
            else if (_inBattle)
            {
                EndTurn();
                _inBattle = false;
                emitted.Add(Emit(BattleEventKind.BattleEnd, frame.Time));
            }
        }

        // Phase dots: a held count one lower than the last held count is a phase change.
        if (cues.PhaseDots > 0)
        {
            if (cues.PhaseDots != _dotCandidate)
            {
                (_dotCandidate, _dotSince, _dotHeld) = (cues.PhaseDots, frame.Time, false);
            }
            else if (!_dotHeld && frame.Time - _dotSince >= PhaseDotSeconds)
            {
                _dotHeld = true;
                if (_inBattle && _dotStable > 0 && _dotCandidate == _dotStable - 1)
                {
                    emitted.Add(PhaseChangeAt(_dotSince));
                }

                _dotStable = _dotCandidate;
            }
        }

        if (frame.AxisFound)
        {
            // R45: insert cards are not used; ultimates come from the team bar only (R29).
            TrackUltButtons(frame);
        }

        if (frame.AxisFound || cues.CellWhite > 0)
        {
            emitted.AddRange(TrackTurn(frame));
        }

        return emitted;
    }

    /// <summary>A team slot's ultimate button counts as ready at this lit share (measured on 铁墓: ready 0.55–0.84, others ≤ 0.4).</summary>
    public double UltReadyLit { get; init; } = 0.5;

    /// <summary>…and as used at this share or less (after the cut-in: 0.00–0.10).</summary>
    public double UltUsedLit { get; init; } = 0.2;

    /// <summary>An extra action starting this long after a button was last seen ready can be that ultimate.</summary>
    public double PressMatchSeconds { get; init; } = 6;

    /// <summary>
    /// How long a button must stay ready, and then out, to count as one press. Buttons flicker for a moment when the
    /// bar rearranges or an effect passes over it (铁墓 2:52–2:58 had five such flickers in slot 3 within 6 s).
    /// </summary>
    public double UltHoldSeconds { get; init; } = 1.0;

    /// <summary>
    /// The unit in each team slot (index 0 = slot 1), when known (e.g. from the strategy video's team list). Without
    /// it, presses only mark ultimates and slots; they never name a unit (a guessed mapping named one wrongly).
    /// </summary>
    public IReadOnlyList<string?> Team { get; init; } = [];

    /// <summary>
    /// The team bar's ultimate buttons (user, 2026-10-29: lit = ready, the key press releases it): a slot whose button
    /// was held ready and is then held out has used its ultimate. The press time is the last frame it was ready.
    /// </summary>
    private void TrackUltButtons(FrameObservation frame)
    {
        foreach (var button in frame.UltButtons)
        {
            var state = button.Lit >= UltReadyLit ? "ready" : button.Lit <= UltUsedLit ? "used" : "mid";
            var (last, since) = _slotRun.GetValueOrDefault(button.Slot, ("", frame.Time));
            if (state != last)
            {
                since = frame.Time;
            }

            _slotRun[button.Slot] = (state, since);
            var held = frame.Time - since >= UltHoldSeconds;
            if (state == "ready")
            {
                if (held)
                {
                    // Ready again for a whole hold: an earlier leave was only a flicker.
                    _slotReady[button.Slot] = true;
                    _slotLastReady.Remove(button.Slot);
                }
            }
            else if (_slotReady.GetValueOrDefault(button.Slot))
            {
                // The press is the first moment the button leaves a held ready stretch; brief "ready" readings after
                // the ultimate (铁墓 0:24.6, during the cut-in's fade) must not move it.
                _slotLastReady.TryAdd(button.Slot, frame.Time);
            }

            if (state == "used" && held && _slotReady.GetValueOrDefault(button.Slot))
            {
                _slotReady[button.Slot] = false;
                var pressed = _slotLastReady.GetValueOrDefault(button.Slot, frame.Time);
                _slotLastReady.Remove(button.Slot);
                Trace.Add($"{pressed:0.00} 队伍第 {button.Slot} 位的终结技按钮熄灭（{frame.Time:0.00} 确认），终结技释放");
                OnPress(button.Slot, pressed);
            }
        }
    }

    /// <summary>
    /// A confirmed press of a slot's ultimate (R29). A star turn of that unit that already began after the press is this
    /// ultimate entering the current cell (R52; the press is confirmed only after its hold, so the star can come first).
    /// Otherwise it is a new extra action under the main action running at the press (R51, R11), possibly numbered back
    /// in time when the press is confirmed after the next main action started.
    /// </summary>
    private void OnPress(int slot, double pressed)
    {
        if (!_inBattle)
        {
            return;
        }

        var slotUnit = slot <= Team.Count ? Team[slot - 1] : null;
        var stars = _events.Where(e => e.StepKind == AxisRecognizer.StepKind.Extra && e.Skill is null && e.TeamSlot is null
            && e.Time >= pressed - 0.5 && e.Time <= pressed + PressMatchSeconds && (slotUnit is null || e.UnitId == slotUnit)).ToList();
        if (stars.Count == 1)
        {
            var star = stars[0];
            (star.Skill, star.TeamSlot) = (SkillKind.Ultimate, slot);
            star.Notes.Add("队伍栏终结技按钮熄灭");
            Trace.Add($"{pressed:0.00} 第 {slot} 位的终结技 = 已认定的四角星回合 {star.Index} {star.UnitId}");
            return;
        }

        var (segment, main) = MainAt(pressed);
        var e = new BattleEvent
        {
            Kind = BattleEventKind.Action, Time = pressed, Segment = segment, StepKind = AxisRecognizer.StepKind.Extra,
            Main = main, UnitId = slotUnit, Skill = SkillKind.Ultimate, TeamSlot = slot,
        };
        e.Notes.Add("队伍栏终结技按钮熄灭");
        _events.Add(e);
        _pressUltimates.Add(e);
        RenumberExtras(segment, main);
        Trace.Add($"{pressed:0.00} 第 {slot} 位的终结技，记为 {e.Index} {e.UnitId ?? "单位看不清"}（挂在熄灭时进行的主轴行动下）");
    }

    /// <summary>The segment and main action running at a time (0 before the segment's first main action).</summary>
    private (int Segment, int Main) MainAt(double time)
    {
        var (segment, main) = (1, 0);
        foreach (var e in _events.Where(e => e.Time <= time).OrderBy(e => e.Time))
        {
            if (e.Kind == BattleEventKind.Action && e.StepKind == AxisRecognizer.StepKind.Main)
            {
                (segment, main) = (e.Segment, e.Main);
            }
            else if (e.Kind is BattleEventKind.WaveChange or BattleEventKind.PhaseChange)
            {
                (segment, main) = (e.Segment + 1, 0);
            }
            else if (e.Kind == BattleEventKind.BattleStart)
            {
                (segment, main) = (1, 0);
            }
        }

        return (segment, main);
    }

    /// <summary>Numbers the extra actions under one main action by time: n-1, n-2… (R11).</summary>
    private void RenumberExtras(int segment, int main)
    {
        var extras = _events.Where(e => e.Kind == BattleEventKind.Action && e.StepKind == AxisRecognizer.StepKind.Extra
            && e.Segment == segment && e.Main == main).OrderBy(e => e.Time).ToList();
        for (var i = 0; i < extras.Count; i++)
        {
            extras[i].Extra = i + 1;
        }

        if (segment == _segment && main == _main)
        {
            _extra = extras.Count;
        }
    }

    /// <summary>Ends the last turn (end of video or capture).</summary>
    public void Finish()
    {
        EndTurn();
        if (!_sawBattleStart && _events.Count > 0 && !_problems.Any(p => p.Contains("未检测到", StringComparison.Ordinal)))
        {
            _problems.Add("未检测到战斗开始（录像可能从战斗中途开始）");
        }

        if (_inBattle)
        {
            _problems.Add("未检测到战斗结束（录像可能在战斗中途结束）");
        }
    }

    private IEnumerable<BattleEvent> TrackTurn(FrameObservation frame)
    {
        var cues = frame.Cues;

        // The flash starts a turn. Only its first frame counts.
        var flash = cues.CellWhite >= FlashWhiteShare;
        if (flash && !_flashing && (_turn is null || frame.Time - _turn.Start >= MinTurnSeconds))
        {
            EndTurn();
            _turn = new Turn(frame.Time);
            Trace.Add($"{frame.Time:0.00} 白闪（近白占 {cues.CellWhite:0.00}），新回合");
        }

        _flashing = flash;
        if (!frame.AxisFound)
        {
            yield break;
        }

        // A marker or unit that changed and stayed changed for StableFrames also starts a turn (a flash can be hidden).
        if (_turn is not null && frame.Time - _turn.Start >= MinTurnSeconds && (ChangedAndHeld(f => f.Cues.Marker?.Label, _turn.Markers)
            || ChangedAndHeld(f => f.AxisFound ? f.CurrentUnit : null, _turn.Units)))
        {
            Trace.Add($"{frame.Time:0.00} 标记或单位改变并保持（{Turn.Top(_turn.Markers, 1)}/{Turn.Top(_turn.Units, 1)} → {frame.Cues.Marker?.Label}/{frame.CurrentUnit}），新回合");
            EndTurn();
            _turn = new Turn(_recent[^StableFrames].Time);
            // The frames that showed the change belong to the new turn.
            foreach (var f in _recent.Skip(_recent.Count - StableFrames).SkipLast(1))
            {
                Vote(f);
            }
        }

        _turn ??= new Turn(frame.Time);
        Vote(frame);
        var decided = Decide();
        if (decided is not null)
        {
            yield return decided;
        }
    }

    /// <summary>True when the last StableFrames frames agree on a value that is not this turn's leading value.</summary>
    private bool ChangedAndHeld(Func<FrameObservation, string?> value, Dictionary<string, int> votes)
    {
        if (_recent.Count < StableFrames || Turn.Top(votes, StableFrames) is not { } leading)
        {
            return false;
        }

        var last = _recent.Skip(_recent.Count - StableFrames).Select(value).ToList();
        return last[0] is { } v && v != leading && last.All(x => x == v);
    }

    private void Vote(FrameObservation frame)
    {
        var turn = _turn!;
        var cues = frame.Cues;
        turn.Frames++;
        if (cues.Marker?.Label is { } m && m != "none")
        {
            turn.Markers[m] = turn.Markers.GetValueOrDefault(m) + 1;
        }

        if (frame.CurrentUnit is { } u)
        {
            turn.Units[u] = turn.Units.GetValueOrDefault(u) + 1;
        }

        if (cues.AvBar is { } av)
        {
            turn.Readings[av.Text] = turn.Readings.GetValueOrDefault(av.Text) + 1;
        }

        // The skill: the last selection seen before the action starts running ("我方行动中"). Players often switch at
        // the last moment (铁墓 0:17.7: basic for 1.5 s, then skill for a few frames while the button was still
        // growing), so a single clear reading counts; the button reader gives none while unsure.
        if (!turn.Executing)
        {
            if (cues.Selection is { } seen)
            {
                turn.LastSelection = seen;
            }

            if (cues.PhaseText.Label == "ally" && turn.LastSelection is { } selected)
            {
                turn.Executing = true;
                turn.Skill = selected switch { "basic" => SkillKind.Basic, "skill" => SkillKind.Skill, _ => null };
            }
        }

        if (turn.Event is { } e)
        {
            Fill(turn, e);
        }
    }

    /// <summary>Emits the turn's action once its marker and unit are settled; enemy turns are dropped.</summary>
    private BattleEvent? Decide()
    {
        var turn = _turn!;
        if (turn.Event is not null || turn.Dropped)
        {
            return null;
        }

        var marker = Turn.Top(turn.Markers, StableFrames);
        if (marker is "enemy-main" or "boss-mechanism")
        {
            turn.Dropped = true;
            return null;
        }

        var unit = Turn.Top(turn.Units, StableFrames);
        if (marker is null || unit is null)
        {
            return null;
        }

        if (!_inBattle)
        {
            StartImplicitly(turn.Start, "未检测到战斗开始，从第一个行动算起");
        }

        BattleEvent e;
        _pressUltimates.RemoveAll(p => turn.Start - p.Time > PressMatchSeconds);
        if (marker == "ally-extra" && _pressUltimates.FirstOrDefault(p => p.UnitId is null || p.UnitId == unit) is { } pressed)
        {
            // R52: the ultimate released from the team bar is now in the current cell waiting for release: same action.
            _pressUltimates.Remove(pressed);
            pressed.UnitId ??= unit;
            pressed.Notes.Add("进入当前格等待释放");
            turn.Event = pressed;
            Fill(turn, pressed);
            Trace.Add($"{turn.Start:0.00} 四角星回合是 {pressed.Index} 的终结技（队伍栏熄灭），单位 {pressed.UnitId}");
            return null;
        }

        // R14: a star turn with no team-bar press is an extra action of another kind, so its skill stays unknown. A
        // press confirmed later can still make it an ultimate (R52, OnPress).
        if (marker == "ally-extra")
        {
            _extra++;
            e = new BattleEvent
            {
                Kind = BattleEventKind.Action, Time = turn.Start, Segment = _segment, StepKind = AxisRecognizer.StepKind.Extra,
                Main = _main, Extra = _extra, UnitId = unit,
            };
        }
        else
        {
            _main++;
            _extra = 0;
            e = new BattleEvent
            {
                Kind = BattleEventKind.Action, Time = turn.Start, Segment = _segment, StepKind = AxisRecognizer.StepKind.Main,
                Main = _main, UnitId = unit,
            };
        }

        turn.Event = e;
        Fill(turn, e);
        _events.Add(e);
        Trace.Add($"{turn.Start:0.00} 认定 {e.Index} {unit}（标记 {marker}，{turn.Frames} 帧）");
        return e;
    }

    private void Fill(Turn turn, BattleEvent e)
    {
        if (e.StepKind == AxisRecognizer.StepKind.Main && turn.Skill is { } skill)
        {
            e.Skill = skill;
        }

        if (Turn.Top(turn.Readings, 2) is { } text && text.Split('|') is [var r, var v])
        {
            e.Round = int.Parse(r);
            e.ActionValue = int.Parse(v);
        }
    }

    private void EndTurn()
    {
        if (_turn is { Event: null, Dropped: false, Frames: > 0 } turn && turn.Markers.Count + turn.Units.Count > 0)
        {
            var marker = Turn.Top(turn.Markers, 1);
            if (marker is not ("enemy-main" or "boss-mechanism"))
            {
                _problems.Add($"{turn.Start:0.0} 秒：有一个回合没能认定（标记 {marker ?? "看不清"}，单位 {Turn.Top(turn.Units, 1) ?? "看不清"}），未写入序列");
            }
        }

        _turn = null;
    }

    /// <summary>
    /// A phase change confirmed now that happened at <paramref name="time"/>: the action that dealt the blow keeps its
    /// number in the old phase; actions that started after the drop move into the new phase and are renumbered.
    /// </summary>
    private BattleEvent PhaseChangeAt(double time)
    {
        var moved = _events.Where(e => e.Kind == BattleEventKind.Action && e.Segment == _segment && e.Time > time + 0.2).ToList();
        _events.RemoveAll(moved.Contains);
        var change = Emit(BattleEventKind.PhaseChange, time);
        _segment++;
        (_main, _extra) = (0, 0);
        _pressUltimates.Clear();
        foreach (var e in moved)
        {
            if (e.StepKind == AxisRecognizer.StepKind.Main)
            {
                (_main, _extra) = (_main + 1, 0);
            }
            else
            {
                _extra++;
            }

            (e.Segment, e.Main, e.Extra) = (_segment, _main, e.StepKind == AxisRecognizer.StepKind.Main ? 0 : _extra);
            _events.Add(e);
        }

        Trace.Add($"{time:0.00} 阶段点少一个并保持，转阶段（之后的 {moved.Count} 个行动移入新阶段）");
        return change;
    }

    private BattleEvent NewSegment(BattleEventKind kind, double time)
    {
        EndTurn();
        _pressUltimates.Clear();
        var e = Emit(kind, time);
        _segment++;
        (_main, _extra) = (0, 0);
        return e;
    }

    private BattleEvent Emit(BattleEventKind kind, double time)
    {
        var e = new BattleEvent { Kind = kind, Time = time, Segment = _segment };
        _events.Add(e);
        return e;
    }

    private void StartImplicitly(double time, string note)
    {
        _inBattle = true;
        _problems.Add($"{time:0.0} 秒：{note}（录像可能从战斗中途开始）");
    }
}
