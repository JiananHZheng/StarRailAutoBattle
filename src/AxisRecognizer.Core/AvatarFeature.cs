using System.Runtime.InteropServices;
using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>
/// A compact, hand-built descriptor of one avatar crop. The border (inset) is ignored so the slot frame and the
/// semi-transparent background weigh less than the portrait itself. Comparison is a weighted sum of:
/// masked grayscale correlation, masked edge-magnitude correlation (robust to transparency and tint),
/// and hue/saturation histogram intersection (robust to small shifts).
/// </summary>
public sealed class AvatarFeature
{
    private const int HueBins = 18;
    private const int SatBins = 3;
    private const int HistLength = HueBins * SatBins + 2;

    private AvatarFeature(float[] gray, float[] edge, float[] hist, double grayStd)
    {
        Gray = gray;
        Edge = edge;
        Hist = hist;
        GrayStd = grayStd;
    }

    public float[] Gray { get; }
    public float[] Edge { get; }
    public float[] Hist { get; }

    /// <summary>Standard deviation of gray levels inside the mask (0–255 scale); low means a flat, likely empty cell.</summary>
    public double GrayStd { get; }

    /// <param name="ignore">Areas of the crop to leave out, as fractions of the crop (e.g. the action-value box).</param>
    public static AvatarFeature Compute(Mat crop, RecognitionSettings settings, IReadOnlyList<RelRect>? ignore = null)
    {
        var n = settings.FeatureSize;
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(n, n), 0, 0, InterpolationFlags.Area);
        using var bgr = resized.Channels() == 3 ? resized.Clone() : ToBgr(resized);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var bgrBytes = Bytes(bgr);
        var hsvBytes = Bytes(hsv);
        var inset = (int)Math.Round(n * settings.Inset);
        var ignored = new bool[n * n];
        foreach (var r in ignore ?? [])
        {
            for (var y = 0; y < n; y++)
            {
                for (var x = 0; x < n; x++)
                {
                    double fx = (x + 0.5) / n, fy = (y + 0.5) / n;
                    if (fx >= r.X && fx < r.X + r.W && fy >= r.Y && fy < r.Y + r.H)
                    {
                        ignored[y * n + x] = true;
                    }
                }
            }
        }

        bool InMask(int x, int y) => x >= inset && y >= inset && x < n - inset && y < n - inset && !ignored[y * n + x];

        var gray = new float[n * n];
        for (var i = 0; i < n * n; i++)
        {
            gray[i] = (float)(0.114 * bgrBytes[i * 3] + 0.587 * bgrBytes[i * 3 + 1] + 0.299 * bgrBytes[i * 3 + 2]);
        }

        var edge = new float[n * n];
        for (var y = 1; y < n - 1; y++)
        {
            for (var x = 1; x < n - 1; x++)
            {
                float G(int dx, int dy) => gray[(y + dy) * n + x + dx];
                var gx = G(1, -1) + 2 * G(1, 0) + G(1, 1) - G(-1, -1) - 2 * G(-1, 0) - G(-1, 1);
                var gy = G(-1, 1) + 2 * G(0, 1) + G(1, 1) - G(-1, -1) - 2 * G(0, -1) - G(1, -1);
                edge[y * n + x] = MathF.Sqrt(gx * gx + gy * gy);
            }
        }

        var hist = new float[HistLength];
        double sum = 0, sumSq = 0;
        var count = 0;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (!InMask(x, y))
                {
                    continue;
                }

                var i = y * n + x;
                sum += gray[i];
                sumSq += gray[i] * gray[i];
                count++;
                int h = hsvBytes[i * 3], s = hsvBytes[i * 3 + 1], v = hsvBytes[i * 3 + 2];
                if (v < 30)
                {
                    hist[HistLength - 2]++;
                }
                else if (s < 30)
                {
                    hist[HistLength - 1]++;
                }
                else
                {
                    var sBin = s < 90 ? 0 : s < 170 ? 1 : 2;
                    hist[Math.Min(HueBins - 1, h * HueBins / 180) * SatBins + sBin]++;
                }
            }
        }

        var total = hist.Sum();
        if (total > 0)
        {
            for (var i = 0; i < hist.Length; i++)
            {
                hist[i] /= total;
            }
        }

        var mean = sum / Math.Max(1, count);
        var std = Math.Sqrt(Math.Max(0, sumSq / Math.Max(1, count) - mean * mean));
        return new AvatarFeature(Normalize(gray, n, InMask), Normalize(edge, n, InMask), hist, std);
    }

    public double Similarity(AvatarFeature other, RecognitionSettings settings)
    {
        var g = Math.Max(0, Dot(Gray, other.Gray));
        var e = Math.Max(0, Dot(Edge, other.Edge));
        double h = 0;
        for (var i = 0; i < Hist.Length; i++)
        {
            h += Math.Min(Hist[i], other.Hist[i]);
        }

        return settings.WeightGray * g + settings.WeightEdge * e + settings.WeightHist * h;
    }

    private static float[] Normalize(float[] values, int n, Func<int, int, bool> inMask)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (inMask(x, y))
                {
                    sum += values[y * n + x];
                    count++;
                }
            }
        }

        var mean = sum / Math.Max(1, count);
        var result = new float[values.Length];
        double norm = 0;
        for (var y = 0; y < n; y++)
        {
            for (var x = 0; x < n; x++)
            {
                if (inMask(x, y))
                {
                    var v = values[y * n + x] - mean;
                    result[y * n + x] = (float)v;
                    norm += v * v;
                }
            }
        }

        norm = Math.Sqrt(norm);
        if (norm > 1e-6)
        {
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (float)(result[i] / norm);
            }
        }

        return result;
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++)
        {
            s += a[i] * b[i];
        }

        return s;
    }

    private static Mat ToBgr(Mat mat)
    {
        var result = new Mat();
        Cv2.CvtColor(mat, result, mat.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
        return result;
    }

    private static byte[] Bytes(Mat mat)
    {
        // Clone() always yields a continuous buffer, which Marshal.Copy needs.
        using var continuous = mat.Clone();
        var bytes = new byte[continuous.Rows * continuous.Cols * continuous.Channels()];
        Marshal.Copy(continuous.Data, bytes, 0, bytes.Length);
        return bytes;
    }
}
