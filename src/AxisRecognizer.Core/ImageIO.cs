using OpenCvSharp;

namespace AxisRecognizer;

/// <summary>Unicode-safe image file access (cv::imread/imwrite cannot open non-ASCII paths on Windows).</summary>
public static class ImageIO
{
    public static Mat Read(string path)
    {
        var mat = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Color);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidDataException($"无法解码图片：{Path.GetFileName(path)}");
        }

        return mat;
    }

    public static void WritePng(string path, Mat mat)
    {
        Cv2.ImEncode(".png", mat, out var bytes);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path, bytes);
    }

    public static byte[] EncodePng(Mat mat)
    {
        Cv2.ImEncode(".png", mat, out var bytes);
        return bytes;
    }
}
