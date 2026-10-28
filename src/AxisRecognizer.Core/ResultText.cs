using System.Globalization;
using System.Reflection;
using System.Text;

namespace AxisRecognizer;

/// <summary>Formats recognition output as plain text (the delivery format for tests).</summary>
public static class ResultText
{
    public static string ToolVersion =>
        typeof(ResultText).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static string UnitLabel(KnowledgeBase kb, string unitId)
    {
        var unit = kb.FindUnit(unitId);
        if (unit is null)
        {
            return unitId;
        }

        // Every unit is shown by its global id; the name (given by the user) is only a note after it.
        var pending = (unit.NameStatus == "candidate" ? "（名称待确认）" : string.Empty) + (unit.Provisional ? "（临时）" : string.Empty);
        return unit.Kind switch
        {
            UnitKind.Empty => "空格",
            UnitKind.Marker or UnitKind.Attachment => unit.Name ?? unit.Id,
            _ => unit.Name is null ? unit.Id + pending : $"{unit.Id} {unit.Name}{pending}",
        };
    }

    public static string SlotLabel(KnowledgeBase kb, SlotResult slot)
    {
        if (slot.UnitId is not null)
        {
            return UnitLabel(kb, slot.UnitId);
        }

        return "未知/待确认" + CandidatesText(kb, slot.Candidates);
    }

    public static string CandidatesText(KnowledgeBase kb, IReadOnlyList<Candidate> candidates) =>
        candidates.Count == 0
            ? "（工具无候选）"
            : "（工具候选：" + string.Join("；", candidates.Select(c => $"{UnitLabel(kb, c.UnitId)} {c.Score:0.00}")) + "）";

    public static string Header(KnowledgeBase kb, string title, string kind)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine($"类型：{kind}");
        sb.AppendLine($"知识库：v{kb.Version}（内容校验 {kb.ContentHash()}，样本 {kb.Samples.Count} 个）　识别工具：AxisRecognizer {ToolVersion}");
        sb.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        return sb.ToString();
    }

    /// <summary>Per-slot TSV rows for raw_detail.tsv (columns as in <see cref="VideoRecognition.DetailHeader"/>).</summary>
    public static string DetailRows(string frameKey, FrameResult r)
    {
        var sb = new StringBuilder();
        foreach (var s in r.Slots)
        {
            sb.AppendLine(string.Join('\t', frameKey, s.Slot, s.PixelRect.X, s.PixelRect.Y, s.PixelRect.Width, s.PixelRect.Height,
                s.IsEmpty, s.UnitId ?? "", s.Score.ToString("0.000", CultureInfo.InvariantCulture),
                string.Join(';', s.Candidates.Select(c => $"{c.UnitId}:{c.Score.ToString("0.000", CultureInfo.InvariantCulture)}"))));
        }

        return sb.ToString();
    }

    /// <summary>Text for one isolated screenshot: visible main-axis order only; no order-change notes.</summary>
    public static string Frame(KnowledgeBase kb, FrameResult result, string imageName)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {imageName}");
        sb.AppendLine($"图片 {result.Width}×{result.Height}，游戏画面区域 {result.Content.Width}×{result.Content.Height}，布局 {result.LayoutId ?? "无"}，耗时 {result.ElapsedMs:0} ms");
        if (result.Problem is not null)
        {
            sb.AppendLine($"未完成：{result.Problem}");
            return sb.ToString();
        }

        sb.AppendLine();
        sb.AppendLine("[主轴]");
        var lines = new List<(string Label, string Note)>();
        foreach (var slot in result.MainAxis)
        {
            if (slot.IsEmpty)
            {
                if (slot.Kind == SlotKind.Current)
                {
                    lines.Add(("当前格未检测到头像", string.Empty));
                }

                continue;
            }

            if (kb.IsMarker(slot.UnitId))
            {
                // Non-unit entries (e.g. special-moment reminders) are recognized only to be left out.
                continue;
            }

            if (kb.IsAttachment(slot.UnitId) && lines.Count > 0)
            {
                // Part of the entry above (counted together with it), e.g. 阿哈时刻's extra-action cell.
                lines[^1] = (lines[^1].Label + $"（含{UnitLabel(kb, slot.UnitId!)}）", lines[^1].Note);
                continue;
            }

            lines.Add((SlotLabel(kb, slot), slot.Kind == SlotKind.Current ? " —— 当前行动格" : string.Empty));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {lines[i].Label}{lines[i].Note}");
        }

        if (lines.Count == 0)
        {
            sb.AppendLine("（未检测到头像）");
        }

        sb.AppendLine();
        sb.AppendLine("[插入行动]");
        sb.AppendLine("无可确认的插入行动（孤立截图：右侧待执行的插入行动按规则不列出；当前格的行动是否为插入行动、属于第几次主轴行动，单张截图无法确定）");
        return sb.ToString();
    }
}
