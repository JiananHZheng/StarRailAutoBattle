using System.Text.RegularExpressions;

namespace AxisRecognizer;

/// <summary>
/// Typing help for the hand-written expected sequence (R48): a space after the line's index, no doubled spaces,
/// and symbols the format never uses are dropped. Comments after # are left alone; pasted text is not filtered.
/// </summary>
public static partial class SequenceTyping
{
    /// <summary>Symbols the format uses (R16, R20, R22), R15's notes, and the dot in names like 丹恒·饮月.</summary>
    private const string Symbols = "-－=＝~～@＠()（）+＋#＃：:、，,·•・";

    /// <summary>
    /// The text to put in place of <paramref name="typed"/> when it is typed over
    /// <paramref name="text"/>[<paramref name="start"/>, <paramref name="start"/> + <paramref name="length"/>).
    /// Empty = nothing is added.
    /// </summary>
    public static string Filter(string text, int start, int length, string typed)
    {
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var before = text[lineStart..start];
        var end = start + length;
        var next = end < text.Length ? text[end] : '\n';
        var result = new System.Text.StringBuilder();
        foreach (var c in typed)
        {
            var line = before + result;
            if (line.Contains('#') || line.Contains('＃') || char.IsControl(c))
            {
                result.Append(c);
                continue;
            }

            if (c is ' ' or '　')
            {
                var prev = line.Length > 0 ? line[^1] : '\n';
                if (!IsSpace(prev) && !IsSpace(next))
                {
                    result.Append(c);
                }

                continue;
            }

            if (!char.IsLetterOrDigit(c) && !Symbols.Contains(c))
            {
                continue;
            }

            // R48: "3" or "3-1" then anything but more of the index gets a space in between.
            if (!char.IsDigit(c) && c is not ('-' or '－') && IndexRegex().IsMatch(line))
            {
                result.Append(' ');
            }

            result.Append(c);
        }

        return result.ToString();
    }

    private static bool IsSpace(char c) => c is ' ' or '　' or '\t';

    [GeneratedRegex(@"^[0-9０-９]+(?:[-－][0-9０-９]+)?$")]
    private static partial Regex IndexRegex();
}
