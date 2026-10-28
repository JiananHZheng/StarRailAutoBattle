using System.Globalization;
using System.Text;
using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>One saved example of a new avatar: the frame it came from and the cell.</summary>
public sealed record NewUnitExample(double Time, string Slot, Rect Rect, string FrameFile);

/// <summary>
/// A group of avatars the KB does not know, which look like one unit. Inside one run it carries a temporary id
/// (新01, 新02…); only after the user checks the run does it become a global unit (axis kb promote).
/// </summary>
public sealed class NewUnitGroup
{
    internal NewUnitGroup(int key) => Key = key;

    internal int Key { get; }

    /// <summary>新01… after <see cref="NewUnitClusterer.Finish"/>, numbered by first appearance.</summary>
    public string TempId { get; internal set; } = string.Empty;

    internal List<AvatarFeature> Exemplars { get; } = [];

    public List<NewUnitExample> Examples { get; } = [];

    /// <summary>Cells (frame × slot) assigned to this group.</summary>
    public int Cells { get; internal set; }

    public double FirstTime { get; internal set; } = double.MaxValue;
    public double LastTime { get; internal set; }
    public string FirstSlot { get; internal set; } = string.Empty;

    /// <summary>The known unit this group most resembles, when close enough to be worth a look (疑似).</summary>
    public Candidate? SuspectedKnown { get; internal set; }

    /// <summary>Another new group this one most resembles, when close enough to be worth a look (疑似同一单位).</summary>
    public (string TempId, double Score)? SuspectedSame { get; internal set; }

    /// <summary>
    /// For a group seen in the current cell: the unit that was in main-axis cell 2 right before this group entered the
    /// current cell, with how often that happened. The current cell frames portraits differently from the main axis,
    /// so the same unit forms one group per cell kind; this is the evidence that links them (疑似, not merged).
    /// </summary>
    public (string Id, int Votes, int Entries)? EnteredFrom { get; internal set; }

    /// <summary>
    /// A known unit that a cell kept flickering between and this group (疑似: the avatar may be that unit in another
    /// state, or the known unit's samples may be wrong). Only noted, never merged.
    /// </summary>
    public (string UnitId, int Times)? FlickersWithKnown { get; internal set; }

    internal Dictionary<string, int> KnownFlickers { get; } = [];

    /// <summary>Set when this group was merged into another during <see cref="NewUnitClusterer.Finish"/>.</summary>
    internal NewUnitGroup? MergedInto { get; set; }

    /// <summary>The group this one ended up in (itself unless merged).</summary>
    public NewUnitGroup Root => MergedInto?.Root ?? this;
}

/// <summary>
/// Groups avatars that matched no known unit, within one run. An avatar joins the most similar group when the
/// similarity reaches <see cref="JoinScore"/>; otherwise it starts a new group. Afterwards groups that are similar on
/// average (not just through one close pair) are merged, so the order cells arrive in does not split a unit. Groups
/// seen in fewer than <see cref="MinCells"/> cells are dropped as noise (mostly cells caught mid-animation). Below
/// the join score nothing is merged: near misses are only reported as 疑似 for the user to decide.
/// </summary>
public sealed class NewUnitClusterer(RecognitionSettings settings)
{
    private readonly List<NewUnitGroup> _groups = [];

    /// <summary>About one second at 15 frames per second.</summary>
    public int MinCells { get; init; } = 15;

    /// <summary>From the KB settings (<see cref="RecognitionSettings.GroupJoinScore"/>), where the measurements behind it are logged.</summary>
    public double JoinScore { get; init; } = settings.GroupJoinScore;
    public int MaxExemplars { get; init; } = 8;
    public int MaxExamples { get; init; } = 3;

    /// <summary>A member this similar to an existing exemplar adds nothing new and is not kept as one.</summary>
    public double ExemplarDiversity { get; init; } = 0.93;

    /// <summary>Lowest similarity reported as 疑似 (to a known unit or between new groups).</summary>
    public double SuspectScore { get; init; } = settings.GroupJoinScore - 0.07;

    /// <summary>Assigns one unknown cell. <paramref name="wantExample"/> asks the caller to save this frame as an example.</summary>
    public NewUnitGroup Assign(AvatarFeature feature, double time, string slot, Candidate? bestKnown, out bool wantExample)
    {
        NewUnitGroup? best = null;
        var bestScore = double.MinValue;
        foreach (var group in _groups)
        {
            var score = group.Exemplars.Max(e => e.Similarity(feature, settings));
            if (score > bestScore)
            {
                (best, bestScore) = (group, score);
            }
        }

        var joined = best is not null && bestScore >= JoinScore;
        var target = joined ? best! : new NewUnitGroup(_groups.Count);
        if (!joined)
        {
            _groups.Add(target);
        }

        target.Cells++;
        if (time < target.FirstTime)
        {
            (target.FirstTime, target.FirstSlot) = (time, slot);
        }

        target.LastTime = Math.Max(target.LastTime, time);
        if (bestKnown is not null && bestKnown.Score > (target.SuspectedKnown?.Score ?? 0))
        {
            target.SuspectedKnown = bestKnown;
        }

        var novel = target.Exemplars.Count == 0 || target.Exemplars.Max(e => e.Similarity(feature, settings)) < ExemplarDiversity;
        if (novel && target.Exemplars.Count < MaxExemplars)
        {
            target.Exemplars.Add(feature);
        }

        wantExample = novel && target.Examples.Count < MaxExamples;
        return target;
    }

    public void AddExample(NewUnitGroup group, NewUnitExample example) => group.Examples.Add(example);

    /// <summary>Merges look-alike groups, drops noise, numbers the rest 新01… by first appearance and fills in the 疑似 notes.</summary>
    public IReadOnlyList<NewUnitGroup> Finish()
    {
        MergeSimilarGroups();
        var kept = _groups.Where(g => g.MergedInto is null && g.Cells >= MinCells).OrderBy(g => g.FirstTime).ThenBy(g => g.Key).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            kept[i].TempId = TempIdFor(i + 1);
        }

        foreach (var group in kept)
        {
            var flicker = group.KnownFlickers.OrderByDescending(kv => kv.Value).FirstOrDefault();
            group.FlickersWithKnown = flicker.Key is null ? null : (flicker.Key, flicker.Value);
            if (group.SuspectedKnown is { } known && known.Score < SuspectScore)
            {
                group.SuspectedKnown = null;
            }

            var closest = kept.Where(o => o != group)
                .Select(o => (o.TempId, Score: group.Exemplars.Max(a => o.Exemplars.Max(b => a.Similarity(b, settings)))))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            group.SuspectedSame = closest.TempId is not null && closest.Score >= SuspectScore ? closest : null;
        }

        return kept;
    }

    /// <summary>Groups that were dropped as noise, and how many cells they held.</summary>
    public (int Groups, int Cells) Dropped
    {
        get
        {
            var noise = _groups.Where(g => g.MergedInto is null && g.Cells < MinCells).ToList();
            return (noise.Count, noise.Sum(g => g.Cells));
        }
    }

    /// <summary>
    /// Merges two groups on evidence from the video rather than looks (e.g. one cell flickering A → B → A while
    /// nothing moved). Safe to call with groups already merged elsewhere.
    /// </summary>
    public void Link(NewUnitGroup a, NewUnitGroup b)
    {
        var (ra, rb) = (a.Root, b.Root);
        if (ra != rb)
        {
            var (into, from) = ra.Cells >= rb.Cells ? (ra, rb) : (rb, ra);
            Absorb(into, from);
        }
    }

    public void NoteKnownFlicker(NewUnitGroup group, string unitId, int times)
    {
        var root = group.Root;
        root.KnownFlickers[unitId] = root.KnownFlickers.GetValueOrDefault(unitId) + times;
    }

    /// <summary>
    /// Average-linkage similarity: how well each exemplar of one group is matched by the other on average, taking the
    /// weaker direction. A single close pair is not enough, which keeps two different units from chaining together.
    /// </summary>
    internal double GroupSimilarity(NewUnitGroup a, NewUnitGroup b)
    {
        double Directed(NewUnitGroup x, NewUnitGroup y) => x.Exemplars.Average(e => y.Exemplars.Max(f => e.Similarity(f, settings)));
        return Math.Min(Directed(a, b), Directed(b, a));
    }

    private void MergeSimilarGroups()
    {
        // Tiny groups are noise either way; leaving them out keeps this quick.
        var live = _groups.Where(g => g.MergedInto is null && g.Cells >= Math.Max(2, MinCells / 3)).ToList();
        var scores = new Dictionary<(int, int), double>();
        double Score(NewUnitGroup a, NewUnitGroup b)
        {
            var key = a.Key < b.Key ? (a.Key, b.Key) : (b.Key, a.Key);
            if (!scores.TryGetValue(key, out var s))
            {
                scores[key] = s = GroupSimilarity(a, b);
            }

            return s;
        }

        while (true)
        {
            (NewUnitGroup A, NewUnitGroup B, double S)? best = null;
            for (var i = 0; i < live.Count; i++)
            {
                for (var j = i + 1; j < live.Count; j++)
                {
                    var s = Score(live[i], live[j]);
                    if (s >= JoinScore && s > (best?.S ?? double.MinValue))
                    {
                        best = (live[i], live[j], s);
                    }
                }
            }

            if (best is not { } pair)
            {
                return;
            }

            var (into, from) = pair.A.Cells >= pair.B.Cells ? (pair.A, pair.B) : (pair.B, pair.A);
            Absorb(into, from);
            live.Remove(from);
            foreach (var key in scores.Keys.Where(k => k.Item1 == into.Key || k.Item2 == into.Key).ToList())
            {
                scores.Remove(key);
            }
        }
    }

    private void Absorb(NewUnitGroup into, NewUnitGroup from)
    {
        from.MergedInto = into;
        into.Cells += from.Cells;
        if (from.FirstTime < into.FirstTime)
        {
            (into.FirstTime, into.FirstSlot) = (from.FirstTime, from.FirstSlot);
        }

        into.LastTime = Math.Max(into.LastTime, from.LastTime);
        if (from.SuspectedKnown is { } known && known.Score > (into.SuspectedKnown?.Score ?? 0))
        {
            into.SuspectedKnown = known;
        }

        foreach (var exemplar in from.Exemplars.Where(_ => into.Exemplars.Count < MaxExemplars))
        {
            if (into.Exemplars.Max(e => e.Similarity(exemplar, settings)) < ExemplarDiversity)
            {
                into.Exemplars.Add(exemplar);
            }
        }

        into.Examples.AddRange(from.Examples.Take(Math.Max(0, MaxExamples - into.Examples.Count)));
        foreach (var (unit, times) in from.KnownFlickers)
        {
            into.KnownFlickers[unit] = into.KnownFlickers.GetValueOrDefault(unit) + times;
        }
    }

    public static string TempIdFor(int number) => "新" + number.ToString("00", CultureInfo.InvariantCulture);

    public static bool IsTempId(string? id) => id is { Length: >= 3 } && id[0] == '新' && id[1..].All(char.IsAsciiDigit);
}

/// <summary>
/// The user's decisions on a run's temporary ids, one per line:
/// <code>
/// 新01 = new          a new global unit
/// 新02 = new 昔涟      a new global unit, with its name as a note
/// 新03 = U0012        the same unit as a known one (an old id such as CH-爻光 also works)
/// 新05 = 新02          the same unit as another temporary id of this run
/// 新04 = skip         not a unit and not worth learning (e.g. caption text over the axis); nothing is stored
/// 新06 = marker 剩余行动条   an axis entry that is not a unit; learned so later runs recognize it and leave it out
/// </code>
/// </summary>
public static class NewUnitPromotion
{
    public enum Action
    {
        New,
        Existing,
        SameAs,
        Skip,
    }

    public sealed record Decision(Action Action, string? Target, string? Name, UnitKind Kind = UnitKind.Unclassified);

    /// <summary>One unit to create (of <paramref name="Kind"/>) or extend, with the temporary ids that become it.</summary>
    public sealed record Target(IReadOnlyList<string> TempIds, string? ExistingUnit, string? NewName, UnitKind Kind = UnitKind.Unclassified);

    public static Dictionary<string, Decision> Parse(string text)
    {
        var decisions = new Dictionary<string, Decision>();
        var lineNo = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Split('#')[0].Trim().Replace('＝', '=');
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('=', 2);
            var id = parts[0].Trim();
            if (parts.Length != 2 || !NewUnitClusterer.IsTempId(id))
            {
                throw new FormatException($"第 {lineNo} 行应为“新01 = new [名称]”、“新01 = U0012”、“新01 = 新02”或“新01 = skip”：{raw.Trim()}");
            }

            var value = parts[1].Trim();
            var words = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var decision = words.Length == 0 ? throw new FormatException($"第 {lineNo} 行缺少内容：{raw.Trim()}")
                : words[0].Equals("new", StringComparison.OrdinalIgnoreCase) ? new Decision(Action.New, null, words.ElementAtOrDefault(1))
                : words[0].Equals("marker", StringComparison.OrdinalIgnoreCase) ? new Decision(Action.New, null, words.ElementAtOrDefault(1), UnitKind.Marker)
                : words[0].Equals("skip", StringComparison.OrdinalIgnoreCase) ? new Decision(Action.Skip, null, null)
                : NewUnitClusterer.IsTempId(words[0]) ? new Decision(Action.SameAs, words[0], null)
                : new Decision(Action.Existing, words[0], null);
            if (!decisions.TryAdd(id, decision))
            {
                throw new FormatException($"{id} 写了两次（第 {lineNo} 行）。");
            }
        }

        return decisions;
    }

    /// <summary>Joins 新X = 新Y chains and checks that each joined group has one consistent outcome.</summary>
    public static List<Target> Plan(IReadOnlyDictionary<string, Decision> decisions)
    {
        var parent = decisions.Keys.ToDictionary(k => k, k => k);
        string Root(string id)
        {
            while (parent[id] != id)
            {
                id = parent[id];
            }

            return id;
        }

        foreach (var (id, decision) in decisions.Where(d => d.Value.Action == Action.SameAs))
        {
            if (!parent.ContainsKey(decision.Target!))
            {
                throw new FormatException($"{id} = {decision.Target}：{decision.Target} 本身也要写一行（new、已知编号或 skip）。");
            }

            parent[Root(id)] = Root(decision.Target!);
        }

        var targets = new List<Target>();
        foreach (var group in decisions.Keys.GroupBy(Root))
        {
            var members = group.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var own = members.Select(m => decisions[m]).Where(d => d.Action != Action.SameAs).ToList();
            if (own.Any(d => d.Action == Action.Skip))
            {
                if (members.Count > 1)
                {
                    throw new FormatException($"{string.Join("、", members)} 被写成同一单位，其中又有 skip，请检查。");
                }

                continue;
            }

            var existing = own.Where(d => d.Action == Action.Existing).Select(d => d.Target!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var names = own.Where(d => d.Action == Action.New && !string.IsNullOrWhiteSpace(d.Name)).Select(d => d.Name!).Distinct().ToList();
            if (existing.Count > 1 || (existing.Count == 1 && own.Any(d => d.Action == Action.New)) || names.Count > 1)
            {
                throw new FormatException($"{string.Join("、", members)} 被写成同一单位，但给出的结果不一致（{string.Join("、", existing.Concat(names))}），请检查。");
            }

            if (own.Count == 0)
            {
                throw new FormatException($"{string.Join("、", members)} 互相指向，没有一行写明是 new 还是已知编号。");
            }

            var kind = own.FirstOrDefault(d => d.Action == Action.New)?.Kind ?? UnitKind.Unclassified;
            targets.Add(new Target(members, existing.FirstOrDefault(), names.FirstOrDefault(), kind));
        }

        return targets;
    }
}

/// <summary>The new-units listing a run writes next to its raw result, and its text summary.</summary>
public static class NewUnitFiles
{
    public const string Folder = "new_units";
    public const string TableFile = "new_units.tsv";

    private const string Header = "temp_id\tcells\tfirst\tlast\tfirst_slot\tsuspected_known\tknown_score\tsuspected_same\tsame_score\texamples";

    public static string Table(IEnumerable<NewUnitGroup> groups)
    {
        var sb = new StringBuilder(Header + "\n");
        foreach (var g in groups)
        {
            var examples = string.Join(';', g.Examples.Select(e =>
                FormattableString.Invariant($"{e.Time:0.000}|{e.Slot}|{e.Rect.X},{e.Rect.Y},{e.Rect.Width},{e.Rect.Height}|{e.FrameFile}")));
            sb.AppendLine(string.Join('\t',
                g.TempId, g.Cells,
                g.FirstTime.ToString("0.000", CultureInfo.InvariantCulture), g.LastTime.ToString("0.000", CultureInfo.InvariantCulture),
                g.FirstSlot,
                g.SuspectedKnown?.UnitId ?? "", g.SuspectedKnown?.Score.ToString("0.000", CultureInfo.InvariantCulture) ?? "",
                g.SuspectedSame?.TempId ?? "", g.SuspectedSame?.Score.ToString("0.000", CultureInfo.InvariantCulture) ?? "",
                examples));
        }

        return sb.ToString();
    }

    public sealed record Row(string TempId, int Cells, IReadOnlyList<NewUnitExample> Examples);

    public static List<Row> ParseTable(string text)
    {
        var rows = new List<Row>();
        foreach (var line in text.Split('\n').Skip(1).Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0))
        {
            var cols = line.Split('\t');
            var examples = cols[9].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e =>
            {
                var parts = e.Split('|');
                var r = parts[2].Split(',').Select(int.Parse).ToArray();
                return new NewUnitExample(double.Parse(parts[0], CultureInfo.InvariantCulture), parts[1], new Rect(r[0], r[1], r[2], r[3]), parts[3]);
            }).ToList();
            rows.Add(new Row(cols[0], int.Parse(cols[1], CultureInfo.InvariantCulture), examples));
        }

        return rows;
    }

    /// <summary>The section appended to a result: what each temporary id is, and what it may be the same as.</summary>
    public static string Summary(KnowledgeBase kb, IReadOnlyList<NewUnitGroup> groups, (int Groups, int Cells) dropped, bool examplesSaved)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[新单位（临时编号）]");
        if (groups.Count == 0)
        {
            sb.AppendLine("无：所有头像都对上了知识库里的单位。");
        }

        sb.AppendLine("临时编号只在本次结果内一致：同一个临时编号 = 工具认为是同一单位。核对后用 axis kb promote 转为全局编号。");
        foreach (var g in groups)
        {
            var notes = new List<string>
            {
                FormattableString.Invariant($"{g.Cells} 格次，首次 {FormatTime(g.FirstTime)}（{g.FirstSlot}），最后 {FormatTime(g.LastTime)}"),
            };
            if (g.SuspectedKnown is { } known)
            {
                notes.Add(FormattableString.Invariant($"疑似 {ResultText.UnitLabel(kb, known.UnitId)}（{known.Score:0.00}）"));
            }

            if (g.SuspectedSame is { } same)
            {
                notes.Add(FormattableString.Invariant($"疑似与 {same.TempId} 为同一单位（{same.Score:0.00}）"));
            }

            if (g.FlickersWithKnown is { } flicker)
            {
                notes.Add($"疑似 {ResultText.UnitLabel(kb, flicker.UnitId)}（同一格在两者之间来回切换 {flicker.Times} 次；也可能是知识库里 {flicker.UnitId} 的样本有误）");
            }

            if (g.EnteredFrom is { } entered)
            {
                notes.Add($"疑似与 {ResultText.UnitLabel(kb, entered.Id)} 为同一单位（当前格与主轴格的头像取景不同；{entered.Votes}/{entered.Entries} 次是它从主轴第 2 格进入当前格后显示为 {g.TempId}）");
            }

            if (examplesSaved && g.Examples.Count > 0)
            {
                notes.Add($"头像：{Folder}/{g.TempId}_1.png 等 {g.Examples.Count} 张");
            }

            sb.AppendLine($"{g.TempId}：{string.Join("；", notes)}");
        }

        if (dropped.Cells > 0)
        {
            sb.AppendLine($"另有 {dropped.Cells} 格次（{dropped.Groups} 组）出现太少，按切换动画等噪声处理，记为“未知/待确认”。");
        }

        return sb.ToString();
    }

    private static string FormatTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return FormattableString.Invariant($"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}");
    }
}
