using System.Text;
using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>Reads frames from a local video file (decoded by OpenCV's bundled FFmpeg backend). No downloading.</summary>
public static class VideoFrames
{
    /// <summary>Yields (seconds, frame) every <paramref name="interval"/> seconds. The caller owns (and must dispose) each frame.</summary>
    public static IEnumerable<(double Time, Mat Frame)> Sample(string videoPath, double interval, double start = 0, double? end = null)
    {
        using var access = AsciiAccessiblePath(videoPath);
        using var capture = new VideoCapture(access.Path);
        if (!capture.IsOpened())
        {
            throw new InvalidDataException($"无法打开视频：{Path.GetFileName(videoPath)}（格式或编码不受支持，或文件损坏）。");
        }

        var fps = capture.Get(VideoCaptureProperties.Fps);
        if (fps is <= 0 or > 1000)
        {
            fps = 30;
        }

        var index = 0L;
        var next = start;
        while (capture.Grab())
        {
            var time = index / fps;
            index++;
            if (end is not null && time > end)
            {
                yield break;
            }

            if (time + 0.5 / fps < next)
            {
                continue;
            }

            var frame = new Mat();
            if (!capture.Retrieve(frame) || frame.Empty())
            {
                frame.Dispose();
                continue;
            }

            next += interval;
            yield return (time, frame);
        }
    }

    public static (double Fps, double Seconds, int Width, int Height) Probe(string videoPath)
    {
        using var access = AsciiAccessiblePath(videoPath);
        using var capture = new VideoCapture(access.Path);
        if (!capture.IsOpened())
        {
            throw new InvalidDataException($"无法打开视频：{Path.GetFileName(videoPath)}。");
        }

        var fps = capture.Get(VideoCaptureProperties.Fps);
        var frames = capture.Get(VideoCaptureProperties.FrameCount);
        return (fps, fps > 0 ? frames / fps : 0, (int)capture.Get(VideoCaptureProperties.FrameWidth), (int)capture.Get(VideoCaptureProperties.FrameHeight));
    }

    /// <summary>
    /// OpenCV's video backend cannot open non-ASCII paths on Windows. For such paths, a temporary
    /// copy with an ASCII name is made under %TEMP% and deleted afterwards.
    /// </summary>
    private static TempPath AsciiAccessiblePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"找不到视频文件：{Path.GetFileName(path)}");
        }

        if (Encoding.UTF8.GetByteCount(full) == full.Length)
        {
            return new TempPath(full, false);
        }

        var temp = Path.Combine(Path.GetTempPath(), $"axis_video_{Guid.NewGuid():N}{Path.GetExtension(full)}");
        if (Encoding.UTF8.GetByteCount(temp) != temp.Length)
        {
            throw new InvalidOperationException("视频路径和临时文件夹都含非 ASCII 字符，OpenCV 无法打开；请把视频放到纯英文路径下。");
        }

        File.Copy(full, temp);
        return new TempPath(temp, true);
    }

    private sealed class TempPath(string path, bool delete) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose()
        {
            if (delete)
            {
                try
                {
                    File.Delete(Path);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
