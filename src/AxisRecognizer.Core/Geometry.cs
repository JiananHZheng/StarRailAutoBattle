using System.Globalization;
using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>
/// A rectangle in game-frame relative units. Every value is divided by the height of the game content
/// area and measured from its top-left corner, so a layout keeps working when the window is resized.
/// </summary>
public sealed record RelRect(double X, double Y, double W, double H)
{
    public Rect ToPixels(Rect content) => new(
        content.X + (int)Math.Round(X * content.Height),
        content.Y + (int)Math.Round(Y * content.Height),
        Math.Max(1, (int)Math.Round(W * content.Height)),
        Math.Max(1, (int)Math.Round(H * content.Height)));

    public static RelRect FromPixels(Rect pixels, Rect content) => new(
        (pixels.X - content.X) / (double)content.Height,
        (pixels.Y - content.Y) / (double)content.Height,
        pixels.Width / (double)content.Height,
        pixels.Height / (double)content.Height);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X:0.0000},{Y:0.0000},{W:0.0000},{H:0.0000}");
}

public static class Geometry
{
    /// <summary>
    /// Finds the game picture inside an image by trimming uniform black bars (letterboxing in videos,
    /// black borders in captures). Returns the full image when nothing sensible can be trimmed.
    /// </summary>
    public static Rect DetectContent(Mat bgr)
    {
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        int rows = gray.Rows, cols = gray.Cols;
        var full = new Rect(0, 0, cols, rows);

        bool RowBlank(int y)
        {
            using var row = gray.Row(y);
            Cv2.MinMaxLoc(row, out _, out double max);
            return max < 18;
        }

        bool ColBlank(int x, int top, int bottom)
        {
            using var col = new Mat(gray, new Rect(x, top, 1, bottom - top));
            Cv2.MinMaxLoc(col, out _, out double max);
            return max < 18;
        }

        int t = 0, b = rows - 1;
        while (t < rows / 3 && RowBlank(t)) t++;
        while (b > rows * 2 / 3 && RowBlank(b)) b--;
        int l = 0, r = cols - 1;
        while (l < cols / 3 && ColBlank(l, t, b + 1)) l++;
        while (r > cols * 2 / 3 && ColBlank(r, t, b + 1)) r--;

        var content = new Rect(l, t, r - l + 1, b - t + 1);
        var aspect = content.Width / (double)content.Height;
        return aspect is >= 1.2 and <= 2.6 && content.Width >= 320 ? content : full;
    }

    public static Rect Clamp(Rect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var w = Math.Clamp(rect.Width, 1, width - x);
        var h = Math.Clamp(rect.Height, 1, height - y);
        return new Rect(x, y, w, h);
    }

    public static Rect Parse(string text)
    {
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
        {
            throw new FormatException($"矩形格式应为 x,y,w,h（像素整数）：{text}");
        }

        var v = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return new Rect(v[0], v[1], v[2], v[3]);
    }
}
