using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>One team slot's ultimate button: where it is and how lit it looks.</summary>
public sealed record UltButton(int Slot, double X, double Y, double R, double Lit, double Fill);

/// <summary>
/// The ultimate buttons right of the portraits in the team bar (user, 2026-10-29: the button lights when the
/// ultimate is ready; pressing the key releases it and the light goes). Four slots, left to right.
/// </summary>
public sealed partial class CueReader
{
    /// <summary>The team bar, relative to game height (16:9).</summary>
    public static readonly RelRect TeamBar = new(0.08, 0.72, 0.98, 0.20);

    /// <summary>Slot centers measured on 铁墓 (1080p): about 0.211 of game height apart, the first at 0.267.</summary>
    private const double FirstButtonX = 0.267;

    private const double ButtonSpacing = 0.211;

    /// <summary>
    /// The ultimate button of each slot found in the team bar. Lit is the share of the button's ring that is bright
    /// and colored (a ready button glows); Fill is the share of its inside that is colored (the energy gauge).
    /// </summary>
    public static List<UltButton> ReadUltButtons(Mat frame, Rect content)
    {
        var h = content.Height;
        var area = Geometry.Clamp(TeamBar.ToPixels(content), frame.Width, frame.Height);
        using var crop = new Mat(frame, area);
        using var gray = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 1.5);
        var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient, 1, h * 0.08, 100, 30, (int)(h * 0.030), (int)(h * 0.052));
        using var hsv = new Mat();
        Cv2.CvtColor(crop, hsv, ColorConversionCodes.BGR2HSV);

        var buttons = new List<UltButton>();
        foreach (var c in circles)
        {
            var x = (area.X + c.Center.X - content.X) / (double)h;
            var slot = (int)Math.Round((x - FirstButtonX) / ButtonSpacing) + 1;
            if (slot is < 1 or > 4 || Math.Abs(x - (FirstButtonX + (slot - 1) * ButtonSpacing)) > 0.06)
            {
                continue;
            }

            var (lit, fill) = ButtonLight(hsv, c.Center.X, c.Center.Y, c.Radius);
            var y = (area.Y + c.Center.Y - content.Y) / (double)h;
            if (buttons.FirstOrDefault(b => b.Slot == slot) is { } other)
            {
                // Two circles for one slot: keep the one closer to the slot's center.
                if (Math.Abs(other.X - (FirstButtonX + (slot - 1) * ButtonSpacing)) <= Math.Abs(x - (FirstButtonX + (slot - 1) * ButtonSpacing)))
                {
                    continue;
                }

                buttons.Remove(other);
            }

            buttons.Add(new UltButton(slot, x, y, c.Radius / h, lit, fill));
        }

        return buttons.OrderBy(b => b.Slot).ToList();
    }

    /// <summary>Share of bright colored pixels on the ring (just inside the edge) and of colored pixels inside.</summary>
    private static (double Lit, double Fill) ButtonLight(Mat hsv, float cx, float cy, float r)
    {
        int ring = 0, ringLit = 0, inside = 0, insideColored = 0;
        var r2 = r * r;
        for (var y = (int)(cy - r); y <= (int)(cy + r); y++)
        {
            for (var x = (int)(cx - r); x <= (int)(cx + r); x++)
            {
                if (x < 0 || y < 0 || x >= hsv.Width || y >= hsv.Height)
                {
                    continue;
                }

                var d2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                if (d2 > r2)
                {
                    continue;
                }

                var p = hsv.At<Vec3b>(y, x);
                var colored = p.Item1 >= 90 && p.Item2 >= 120;
                if (d2 >= r2 * 0.72)
                {
                    ring++;
                    if (colored && p.Item2 >= 200)
                    {
                        ringLit++;
                    }
                }
                else
                {
                    inside++;
                    if (colored)
                    {
                        insideColored++;
                    }
                }
            }
        }

        return (ring == 0 ? 0 : ringLit / (double)ring, inside == 0 ? 0 : insideColored / (double)inside);
    }
}
