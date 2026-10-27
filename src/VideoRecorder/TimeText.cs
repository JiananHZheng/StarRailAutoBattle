using System.Globalization;

namespace VideoRecorder;

/// <summary>Time inputs such as "74", "74.5", "1:14" or "1:02:03" (full-width colons accepted).</summary>
public static class TimeText
{
    public const double MaxSeconds = 24 * 3600;

    /// <summary>Empty input is valid and means "not given" (<paramref name="seconds"/> = null).</summary>
    public static bool TryParse(string? input, out double? seconds, out string? error)
    {
        seconds = null;
        error = null;
        var text = (input ?? string.Empty).Trim().Replace('：', ':');
        if (text.Length == 0)
        {
            return true;
        }

        var parts = text.Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Length == 0))
        {
            error = "时间格式应为 1:14、1:02:03 或秒数（如 74）。";
            return false;
        }

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            // Only the last part (seconds) may have a fraction; hours and minutes are whole numbers.
            var style = i == parts.Length - 1 ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            var ok = double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out var value);
            if (!ok || (i > 0 && value >= 60))
            {
                error = parts.Length > 1
                    ? "时间格式应为 分:秒 或 时:分:秒，分和秒小于 60，例如 1:14。"
                    : "时间格式应为 1:14、1:02:03 或秒数（如 74）。";
                return false;
            }

            total = total * 60 + value;
        }

        if (total > MaxSeconds)
        {
            error = "时间超过 24 小时，请检查。";
            return false;
        }

        seconds = total;
        return true;
    }

    /// <summary>"01:14" / "01:14.5" / "1:02:03".</summary>
    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(Math.Max(0, seconds), 1));
        var frac = t.Milliseconds >= 100 ? $".{t.Milliseconds / 100}" : string.Empty;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}{frac}"
            : $"{t.Minutes:00}:{t.Seconds:00}{frac}";
    }
}
