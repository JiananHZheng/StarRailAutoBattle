using System.Globalization;
using System.Text.RegularExpressions;

namespace AxisRecognizer;

public static partial class SlotNames
{
    /// <summary>"current" → (Current, 1); "main2" → (Main, 2); "insert1" → (Insert, 1).</summary>
    public static (SlotKind Kind, int Order) Parse(string name)
    {
        var text = name.Trim().ToLowerInvariant();
        if (text == "current")
        {
            return (SlotKind.Current, 1);
        }

        var match = SlotRegex().Match(text);
        if (!match.Success)
        {
            throw new FormatException($"格名应为 current、mainN（N≥2，主轴第 N 格）或 insertN（N≥1，右侧第 N 个插入格）：{name}");
        }

        var order = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return match.Groups[1].Value == "main"
            ? order >= 2 ? (SlotKind.Main, order) : throw new FormatException("主轴格从 main2 开始（main1 就是 current）。")
            : order >= 1 ? (SlotKind.Insert, order) : throw new FormatException("插入格从 insert1 开始。");
    }

    [GeneratedRegex("^(main|insert)(\\d+)$")]
    private static partial Regex SlotRegex();
}

/// <summary>
/// The correction file a test is fixed with. Plain text, one section per image (or video frame "file@seconds"):
/// <code>
/// [battle01.png]
/// current = CH-流萤
/// main2   = M0003
/// main3   = EMPTY
/// </code>
/// Lines starting with # are comments.
/// </summary>
public static class AnswersFile
{
    public sealed record Section(string FrameKey, Dictionary<string, string> Slots);

    public static List<Section> Parse(string text)
    {
        var sections = new List<Section>();
        Section? current = null;
        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Section(line[1..^1].Trim(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                sections.Add(current);
                continue;
            }

            var eq = line.IndexOf('=');
            if (current is null || eq <= 0)
            {
                throw new FormatException($"第 {lineNo} 行格式不对（应为 [文件名] 或 格名 = 单位编号）：{line}");
            }

            var slot = line[..eq].Trim();
            SlotNames.Parse(slot);
            current.Slots[slot] = line[(eq + 1)..].Trim();
        }

        return sections;
    }

    /// <summary>"clip.mp4@12.5" → ("clip.mp4", 12.5); "a.png" → ("a.png", null).</summary>
    public static (string File, double? Seconds) SplitFrameKey(string key)
    {
        var at = key.LastIndexOf('@');
        if (at > 0 && double.TryParse(key[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return (key[..at], seconds);
        }

        return (key, null);
    }
}
