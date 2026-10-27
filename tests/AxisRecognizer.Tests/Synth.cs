using AxisRecognizer;
using OpenCvSharp;

namespace AxisRecognizer.Tests;

/// <summary>Synthetic battle frames: deterministic fake avatars placed on a known action-axis layout.</summary>
internal static class Synth
{
    /// <summary>Layout used to draw frames, in game-height-relative units (left-anchored).</summary>
    public static readonly (string Name, RelRect Rect)[] TrueLayout =
    [
        ("current", new RelRect(0.020, 0.100, 0.090, 0.090)),
        ("main2", new RelRect(0.030, 0.205, 0.065, 0.065)),
        ("main3", new RelRect(0.030, 0.280, 0.065, 0.065)),
        ("main4", new RelRect(0.030, 0.355, 0.065, 0.065)),
        ("main5", new RelRect(0.030, 0.430, 0.065, 0.065)),
        ("insert1", new RelRect(0.125, 0.112, 0.070, 0.070)),
        ("insert2", new RelRect(0.205, 0.112, 0.070, 0.070)),
    ];

    public static Mat Avatar(int seed, int size = 96)
    {
        var rng = new Random(seed * 7919 + 17);
        var mat = new Mat(size, size, MatType.CV_8UC3, new Scalar(rng.Next(40, 200), rng.Next(40, 200), rng.Next(40, 200)));
        for (var i = 0; i < 7; i++)
        {
            var color = new Scalar(rng.Next(256), rng.Next(256), rng.Next(256));
            if (rng.Next(2) == 0)
            {
                Cv2.Circle(mat, new Point(rng.Next(size), rng.Next(size)), rng.Next(8, size / 2), color, -1);
            }
            else
            {
                var x = rng.Next(size - 10);
                var y = rng.Next(size - 10);
                Cv2.Rectangle(mat, new Rect(x, y, rng.Next(10, size - x), rng.Next(10, size - y)), color, -1);
            }
        }

        return mat;
    }

    /// <summary>
    /// Draws a frame: noisy background, letterbox bars (top/bottom), and each slot's avatar alpha-blended
    /// with a light border. A null seed leaves the slot empty (only the faint slot background).
    /// </summary>
    public static Mat Frame(int width, int gameHeight, IReadOnlyDictionary<string, int?> slots, int backgroundSeed, double alpha = 0.9, int letterbox = 0, double jitter = 0)
    {
        var frame = new Mat(gameHeight + 2 * letterbox, width, MatType.CV_8UC3, Scalar.Black);
        var content = new Rect(0, letterbox, width, gameHeight);
        using (var game = new Mat(frame, content))
        {
            var rng = new Random(backgroundSeed);
            game.SetTo(new Scalar(rng.Next(30, 90), rng.Next(30, 90), rng.Next(30, 90)));
            using var noise = new Mat(game.Size(), MatType.CV_8UC3);
            Cv2.Randu(noise, Scalar.All(0), Scalar.All(40));
            Cv2.Add(game, noise, game);
            for (var i = 0; i < 25; i++)
            {
                Cv2.Line(game, new Point(rng.Next(width), rng.Next(gameHeight)), new Point(rng.Next(width), rng.Next(gameHeight)),
                    new Scalar(rng.Next(256), rng.Next(256), rng.Next(256)), rng.Next(1, 6));
            }
        }

        var jitterRng = new Random(backgroundSeed + 1);
        foreach (var (name, rel) in TrueLayout)
        {
            var rect = rel.ToPixels(content);
            if (jitter > 0)
            {
                rect.X += (int)Math.Round((jitterRng.NextDouble() * 2 - 1) * jitter * rect.Width);
                rect.Y += (int)Math.Round((jitterRng.NextDouble() * 2 - 1) * jitter * rect.Height);
            }

            using var roi = new Mat(frame, rect);
            using var slotBackground = new Mat(rect.Size, MatType.CV_8UC3, new Scalar(20, 20, 20));
            Cv2.AddWeighted(roi, 0.5, slotBackground, 0.5, 0, roi);
            if (slots.TryGetValue(name, out var seed) && seed is not null)
            {
                using var avatar = Avatar(seed.Value);
                using var scaled = new Mat();
                Cv2.Resize(avatar, scaled, rect.Size, 0, 0, InterpolationFlags.Area);
                Cv2.AddWeighted(scaled, alpha, roi, 1 - alpha, 0, roi);
            }

            Cv2.Rectangle(frame, rect, new Scalar(210, 210, 210), Math.Max(1, rect.Width / 30));
        }

        return frame;
    }

    public static Dictionary<string, Rect> PixelLayout(int width, int gameHeight, int letterbox = 0)
    {
        var content = new Rect(0, letterbox, width, gameHeight);
        return TrueLayout.ToDictionary(s => s.Name, s => s.Rect.ToPixels(content));
    }

    public static LayoutProfile Layout16x9(int width = 1920, int height = 1080)
    {
        var content = new Rect(0, 0, width, height);
        return new LayoutProfile
        {
            Id = "SYN16x9",
            AspectMin = 1.70,
            AspectMax = 1.80,
            Slots = TrueLayout.Select(s =>
            {
                var (kind, order) = SlotNames.Parse(s.Name);
                return new SlotDef { Name = s.Name, Kind = kind, Order = order, Rect = RelRect.FromPixels(s.Rect.ToPixels(content), content) };
            }).ToList(),
        };
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxisTests", (name ?? "") + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
    }
}
