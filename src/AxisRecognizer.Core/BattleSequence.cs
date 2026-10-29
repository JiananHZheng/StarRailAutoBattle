using System.Globalization;
using System.Text;
using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>The events found in a video, and the frames behind them.</summary>
public sealed record BattleSequenceResult(IReadOnlyList<BattleEvent> Events, IReadOnlyList<string> Problems, int Frames, int AxisFrames)
{
    /// <summary>The analyzer's trace (why each turn started), for developers.</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];
}

/// <summary>
/// Runs the frame readers and the event analyzer over a video, and writes the result as an expected-sequence text
/// for the 凹分序列 editor (readings and video times as # comments, so the user picks which ones become checks).
/// </summary>
public static class BattleSequence
{
    public static BattleSequenceResult Run(
        KnowledgeBase kb,
        string videoPath,
        double every,
        double start = 0,
        double? end = null,
        double timeOffset = 0,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        IReadOnlyList<string?>? team = null)
    {
        var recognizer = new Recognizer(kb);
        using var reader = new CueReader(kb);
        var analyzer = new EventAnalyzer { StableFrames = Math.Max(2, (int)Math.Round(0.2 / every)), Team = team ?? [] };
        var (_, seconds, _, _) = VideoFrames.Probe(videoPath);
        var last = end ?? seconds;
        Rect? lastCurrent = null;
        int frames = 0, axisFrames = 0;
        foreach (var (time, frame) in VideoFrames.Sample(videoPath, every, start, end))
        {
            using (frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                frames++;
                // Only the current cell matters for the events (a fifth of the time of recognizing every cell).
                var result = recognizer.RecognizeCurrent(frame);
                var cues = reader.ReadFrame(frame, result, lastCurrent);
                var current = result.AxisFound ? result.Slots.FirstOrDefault(s => s.Kind == SlotKind.Current) : null;
                if (current is not null)
                {
                    axisFrames++;
                    lastCurrent = current.FeatureRect;
                }

                var unit = current is { IsEmpty: false, UnitId: { } id } && !kb.IsNonAction(id) ? kb.ResolveUnit(id).Id : null;
                var cardUnits = (cues.InsertCards ?? []).Select(card =>
                {
                    using var crop = new Mat(frame, card);
                    return recognizer.NamePortrait(crop) is { } u ? kb.ResolveUnit(u).Id : null;
                }).ToList();
                analyzer.Feed(new FrameObservation(time + timeOffset, result.AxisFound, unit, cues)
                {
                    InsertUnits = cardUnits,
                    UltButtons = CueReader.ReadUltButtons(frame, result.Content),
                });
                progress?.Report(last > start ? Math.Clamp((time - start) / (last - start), 0, 1) : 0);
            }
        }

        analyzer.Finish();
        return new BattleSequenceResult(analyzer.Events, analyzer.Problems, frames, axisFrames) { Trace = analyzer.Trace };
    }

    /// <summary>"m:ss.f"</summary>
    public static string Clock(double seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)(seconds / 60)}:{seconds % 60:00.0}");

    /// <summary>The name the 凹分序列 editor accepts for a unit: its name when that name is unique, else its id.</summary>
    public static string UnitText(KnowledgeBase kb, string? unitId)
    {
        if (unitId is null)
        {
            return "未知";
        }

        var unit = kb.ResolveUnit(unitId);
        var sameName = unit.Name is null ? 0 : kb.Units.Count(u => u.MergedInto is null && u.Name == unit.Name);
        return unit.Name is { } name && sameName == 1 && !name.Contains(' ') ? name : unit.Id;
    }

    public static string ToSequenceText(KnowledgeBase kb, BattleSequenceResult result, string source)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# 由攻略视频识别生成：{source}");
        sb.AppendLine($"# 共 {result.Frames} 帧，其中 {result.AxisFrames} 帧找到行动轴；招式和读数只作参考，请核对后在需要检定的行加 @ 条件");
        foreach (var problem in result.Problems)
        {
            sb.AppendLine($"# 注意：{problem}");
        }

        foreach (var e in result.Events)
        {
            switch (e.Kind)
            {
                case BattleEventKind.BattleStart:
                    sb.AppendLine($"# 战斗开始（视频 {Clock(e.Time)}）");
                    break;
                case BattleEventKind.BattleEnd:
                    sb.AppendLine($"# 战斗结束（视频 {Clock(e.Time)}）");
                    break;
                case BattleEventKind.WaveChange:
                case BattleEventKind.PhaseChange:
                    var marker = new SequenceStep(0, e.Segment, e.Kind == BattleEventKind.WaveChange ? StepKind.WaveChange : StepKind.PhaseChange,
                        0, 0, null, null, null, null, null);
                    sb.AppendLine($"{marker.ToText()}  # 视频 {Clock(e.Time)}");
                    break;
                default:
                    var step = new SequenceStep(0, e.Segment, e.StepKind ?? StepKind.Main, e.Main, e.Extra, UnitText(kb, e.UnitId), e.UnitId,
                        e.Skill, null, null);
                    var reading = e.Round is { } r && e.ActionValue is { } av ? $"，读数 轮次={r} 行动值={av}" : "";
                    var withSlot = e.TeamSlot is { } slot ? e.Notes.Prepend($"队伍第 {slot} 位") : e.Notes;
                    var allNotes = e.UnitId is null ? withSlot.Append("单位看不清，请改成名称或 U 编号") : withSlot;
                    var notes = allNotes.Any() ? "，" + string.Join("，", allNotes) : "";
                    sb.AppendLine($"{step.ToText()}  # 视频 {Clock(e.Time)}{reading}{notes}");
                    break;
            }
        }

        return sb.ToString();
    }
}
