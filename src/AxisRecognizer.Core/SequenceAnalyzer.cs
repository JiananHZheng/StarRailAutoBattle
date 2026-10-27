using System.Text;

namespace AxisRecognizer;

/// <summary>
/// Turns per-frame results of a video (or ordered screenshots) into the action sequence.
/// Rules implemented:
/// <list type="bullet">
/// <item>An action is recorded when a stable state shows a different unit fully in the current (top-left) cell.</item>
/// <item>If that unit was waiting in the insert area to the right in the previous stable state, it is an inserted
/// action numbered "main-insert" (e.g. 3-1); the pending insert itself is never listed separately.</item>
/// <item>Order changes compare the visible queue with the order expected from the previous state. Pull/push and
/// speed changes cannot be told apart from the queue alone, so every change is "原因待确认" with the number of
/// places moved relative to the expected order.</item>
/// <item>Units appearing or disappearing are not recorded.</item>
/// </list>
/// </summary>
public sealed class SequenceAnalyzer
{
    public const string UnknownToken = "?";

    private readonly KnowledgeBase _kb;

    public SequenceAnalyzer(KnowledgeBase kb, int stableFrames = 2)
    {
        _kb = kb;
        StableFrames = stableFrames;
    }

    public int StableFrames { get; }

    public sealed record AxisState(double Time, string Current, SlotResult CurrentSlot, IReadOnlyList<string> Main, IReadOnlyList<string> Inserts)
    {
        public bool SameAs(AxisState other) =>
            Current == other.Current && Main.SequenceEqual(other.Main) && Inserts.SequenceEqual(other.Inserts);
    }

    public sealed record OrderChange(string Unit, int Shift);

    public sealed class ActionEntry
    {
        public required int Main { get; init; }
        public required int Insert { get; init; }
        public required AxisState State { get; init; }
        public List<string> Notes { get; } = [];
        public List<OrderChange> OrderChanges { get; } = [];
        public string Label => Insert == 0 ? Main.ToString() : $"{Main}-{Insert}";
    }

    /// <param name="BattleStart">Time the action axis appeared after at least <see cref="BattleStartAbsentFrames"/> frames without it.</param>
    public sealed record Analysis(IReadOnlyList<ActionEntry> Actions, int FramesTotal, int FramesUsable, IReadOnlyList<string> Problems, double? BattleStart = null);

    /// <summary>How many sampled frames without an action axis must precede it for its appearance to count as the battle start.</summary>
    public int BattleStartAbsentFrames { get; init; } = 2;

    /// <summary>
    /// An analyzer whose frame counts follow fixed time spans whatever the sampling rate: a state is stable after
    /// <paramref name="stableSeconds"/>, a battle starts after <paramref name="absentSeconds"/> without the axis.
    /// </summary>
    public static SequenceAnalyzer ForInterval(KnowledgeBase kb, double every, double stableSeconds = 0.3, double absentSeconds = 1.0) =>
        new(kb, Math.Max(2, (int)Math.Ceiling(stableSeconds / every - 1e-9)))
        {
            BattleStartAbsentFrames = Math.Max(2, (int)Math.Ceiling(absentSeconds / every - 1e-9)),
        };

    /// <param name="isMarker">Recognizes cells that are not an action of their own (markers, attachments); they are dropped from the queue.</param>
    public static AxisState? ToState(double time, FrameResult frame, Func<string?, bool>? isMarker = null)
    {
        if (frame.Problem is not null || !frame.AxisFound)
        {
            return null;
        }

        bool Skip(SlotResult s) => s.IsEmpty || (isMarker?.Invoke(s.UnitId) ?? false);
        var current = frame.MainAxis.FirstOrDefault(s => s.Kind == SlotKind.Current);
        if (current is null || Skip(current))
        {
            return null;
        }

        static string Token(SlotResult s) => s.UnitId ?? UnknownToken;
        var main = frame.MainAxis.Where(s => s.Kind == SlotKind.Main && !Skip(s)).Select(Token).ToList();
        var inserts = frame.InsertSlots.Where(s => !Skip(s)).Select(Token).ToList();
        return new AxisState(time, Token(current), current, main, inserts);
    }

    public Analysis Analyze(IEnumerable<(double Time, FrameResult Frame)> frames)
    {
        var problems = new List<string>();
        var total = 0;
        var usable = 0;
        var stable = new List<AxisState>();
        AxisState? candidate = null;
        var run = 0;
        var absentBeforeAxis = 0;
        double? firstAxisTime = null;

        foreach (var (time, frame) in frames)
        {
            total++;
            if (frame.Problem is not null && !problems.Contains(frame.Problem))
            {
                problems.Add(frame.Problem);
            }

            // Battle start: the axis appears after a stretch of frames without it (loading / opening animation).
            if (firstAxisTime is null && frame.Problem is null)
            {
                if (frame.AxisFound)
                {
                    firstAxisTime = time;
                }
                else
                {
                    absentBeforeAxis++;
                }
            }

            var state = ToState(time, frame, _kb is null ? null : _kb.IsNonAction);
            if (state is null)
            {
                candidate = null;
                run = 0;
                continue;
            }

            usable++;
            if (candidate is not null && candidate.SameAs(state))
            {
                run++;
            }
            else
            {
                candidate = state;
                run = 1;
            }

            if (run == StableFrames && (stable.Count == 0 || !stable[^1].SameAs(candidate)))
            {
                stable.Add(candidate);
            }
        }

        var battleStart = absentBeforeAxis >= BattleStartAbsentFrames ? firstAxisTime : null;
        var actsAsInsert = _kb is null ? null : (Func<string, bool>)(id => _kb.ActsAsInsert(id));
        return new Analysis(BuildActions(stable, actsAsInsert, battleStart, TimeIsIndex), total, usable, problems, battleStart);
    }

    /// <param name="actsAsInsert">Units whose actions are always inserted turns (e.g. 阿哈时刻).</param>
    /// <param name="battleStart">When known, numbering is from the start of the battle instead of from the first stable frame.</param>
    internal static List<ActionEntry> BuildActions(
        IReadOnlyList<AxisState> stable,
        Func<string, bool>? actsAsInsert = null,
        double? battleStart = null,
        bool timeIsIndex = false)
    {
        var actions = new List<ActionEntry>();
        var mainCount = 0;
        var insertCount = 0;
        AxisState? prev = null;
        bool IsInsertUnit(string token) => token != UnknownToken && (actsAsInsert?.Invoke(token) ?? false);

        foreach (var state in stable)
        {
            if (prev is null)
            {
                var firstIsInsert = battleStart is not null && IsInsertUnit(state.Current);
                mainCount = firstIsInsert ? 0 : 1;
                insertCount = firstIsInsert ? 1 : 0;
                var first = new ActionEntry { Main = mainCount, Insert = insertCount, State = state };
                first.Notes.Add(battleStart is not null
                    ? $"战斗开始：{(timeIsIndex ? $"第 {battleStart:0} 张" : TimeSpan.FromSeconds(battleStart.Value).ToString(@"mm\:ss\.f"))} 检测到行动轴出现，序号从战斗开始起算"
                    : "未检测到战斗开始（画面一开始就有行动轴），序号从第一次稳定画面起算，此前的行动未知；该行动是否为插入行动待确认");
                actions.Add(first);
                prev = state;
                continue;
            }

            var unknownInvolved = state.Current == UnknownToken || prev.Current == UnknownToken;
            if (state.Current != prev.Current || unknownInvolved && !state.SameAs(prev))
            {
                ActionEntry entry;
                List<string> expected;
                if (state.Current != UnknownToken && (prev.Inserts.Contains(state.Current) || IsInsertUnit(state.Current)))
                {
                    insertCount++;
                    entry = new ActionEntry { Main = mainCount, Insert = insertCount, State = state };
                    expected = [.. prev.Main];

                    // An insert-type unit waiting in the main queue leaves it when it acts; that is not an order change.
                    if (!prev.Inserts.Contains(state.Current))
                    {
                        var index = expected.IndexOf(state.Current);
                        if (index >= 0)
                        {
                            expected.RemoveAt(index);
                        }
                    }
                }
                else
                {
                    mainCount++;
                    insertCount = 0;
                    entry = new ActionEntry { Main = mainCount, Insert = 0, State = state };
                    expected = [.. prev.Main];
                    var position = expected.IndexOf(state.Current);
                    if (state.Current != UnknownToken && position > 0)
                    {
                        entry.OrderChanges.Add(new OrderChange(state.Current, position));
                    }

                    if (position >= 0)
                    {
                        expected.RemoveAt(position);
                    }
                }

                if (unknownInvolved)
                {
                    entry.Notes.Add("当前格或上一当前格的单位未识别，行动归属待确认");
                }

                // The unit that just finished goes back into the queue at a position we cannot predict; ignore it.
                var actual = RemoveBestOccurrence(state.Main, prev.Current, expected);
                entry.OrderChanges.AddRange(CompareOrder(expected, actual));
                actions.Add(entry);
            }
            else if (!state.Main.SequenceEqual(prev.Main) && actions.Count > 0)
            {
                actions[^1].OrderChanges.AddRange(CompareOrder(prev.Main, state.Main));
            }

            prev = state;
        }

        return actions;
    }

    private static List<string> RemoveBestOccurrence(IReadOnlyList<string> queue, string token, IReadOnlyList<string> expected)
    {
        var list = queue.ToList();
        var indices = Enumerable.Range(0, list.Count).Where(i => list[i] == token).ToList();
        if (token == UnknownToken || indices.Count == 0)
        {
            return list;
        }

        var bestIndex = indices
            .OrderByDescending(i => Lcs(expected, list.Where((_, j) => j != i).ToList()).Count)
            .First();
        list.RemoveAt(bestIndex);
        return list;
    }

    /// <summary>
    /// Relative order comparison restricted to units visible in both lists (the queue shows only a window,
    /// and unknown units are skipped). Positive shift = moved earlier (前移) than expected.
    /// </summary>
    internal static List<OrderChange> CompareOrder(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        var e = FilterCommon(expected, actual);
        var a = FilterCommon(actual, expected);
        var lcs = Lcs(e, a);
        var inLcsA = new bool[a.Count];
        foreach (var (_, ia) in lcs)
        {
            inLcsA[ia] = true;
        }

        var usedE = new bool[e.Count];
        foreach (var (ie, _) in lcs)
        {
            usedE[ie] = true;
        }

        var changes = new List<OrderChange>();
        for (var ia = 0; ia < a.Count; ia++)
        {
            if (inLcsA[ia])
            {
                continue;
            }

            var ie = Enumerable.Range(0, e.Count).FirstOrDefault(i => !usedE[i] && e[i] == a[ia], -1);
            if (ie < 0)
            {
                continue;
            }

            usedE[ie] = true;
            if (ie != ia)
            {
                changes.Add(new OrderChange(a[ia], ie - ia));
            }
        }

        return changes;
    }

    private static List<string> FilterCommon(IReadOnlyList<string> source, IReadOnlyList<string> other)
    {
        var available = other.Where(t => t != UnknownToken).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<string>();
        foreach (var token in source)
        {
            if (token != UnknownToken && available.TryGetValue(token, out var count) && count > 0)
            {
                result.Add(token);
                available[token] = count - 1;
            }
        }

        return result;
    }

    private static List<(int E, int A)> Lcs(IReadOnlyList<string> e, IReadOnlyList<string> a)
    {
        var dp = new int[e.Count + 1, a.Count + 1];
        for (var i = e.Count - 1; i >= 0; i--)
        {
            for (var j = a.Count - 1; j >= 0; j--)
            {
                dp[i, j] = e[i] == a[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        int x = 0, y = 0;
        while (x < e.Count && y < a.Count)
        {
            if (e[x] == a[y])
            {
                pairs.Add((x, y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }

    public string ToText(Analysis analysis, string sourceName, string samplingNote)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {sourceName}");
        sb.AppendLine($"{samplingNote}；可用帧 {analysis.FramesUsable}/{analysis.FramesTotal}；稳定判定：连续 {StableFrames} 帧相同");
        sb.AppendLine(analysis.BattleStart is { } start
            ? $"战斗开始：{FormatTime(start)}（行动轴在此前至少 {BattleStartAbsentFrames} 个取样帧中不存在）"
            : "战斗开始：未检测到（取样范围内画面一开始就有行动轴，或始终没有行动轴）");
        foreach (var problem in analysis.Problems)
        {
            sb.AppendLine($"问题：{problem}");
        }

        if (analysis.Actions.Count == 0)
        {
            sb.AppendLine();
            sb.AppendLine("未完成：没有得到任何稳定的行动轴画面，无法给出序列。");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("[主轴]");
        foreach (var action in analysis.Actions.Where(a => a.Insert == 0))
        {
            AppendAction(sb, action);
        }

        sb.AppendLine();
        sb.AppendLine("[插入行动]");
        var inserts = analysis.Actions.Where(a => a.Insert > 0).ToList();
        if (inserts.Count == 0)
        {
            sb.AppendLine("无");
        }

        foreach (var action in inserts)
        {
            AppendAction(sb, action);
        }

        return sb.ToString();
    }

    private void AppendAction(StringBuilder sb, ActionEntry action)
    {
        var label = action.State.Current == UnknownToken
            ? "未知/待确认" + ResultText.CandidatesText(_kb, action.State.CurrentSlot.Candidates)
            : ResultText.UnitLabel(_kb, action.State.Current);
        sb.AppendLine($"{action.Label}. {label}（{FormatTime(action.State.Time)}）");
        foreach (var note in action.Notes)
        {
            sb.AppendLine($"   注：{note}");
        }

        foreach (var change in action.OrderChanges)
        {
            var direction = change.Shift > 0 ? "前移" : "后移";
            sb.AppendLine($"   注：顺序变化，原因待确认：{ResultText.UnitLabel(_kb, change.Unit)} 相对预期{direction} {Math.Abs(change.Shift)} 格");
        }
    }

    /// <summary>True when "time" is the 1-based index of an ordered screenshot rather than seconds into a video.</summary>
    public bool TimeIsIndex { get; init; }

    private string FormatTime(double time) =>
        TimeIsIndex ? $"第 {time:0} 张" : TimeSpan.FromSeconds(time).ToString(@"mm\:ss\.f");
}
