using System.Text;

namespace AxisRecognizer;

/// <summary>
/// Saved expected sequences: one UTF-8 text file per sequence in &lt;axis-data&gt;/sequences, named after the sequence.
/// The launcher edits them; the auto-play code reads them.
/// </summary>
public static class SequenceFiles
{
    public const string FolderName = "sequences";
    public const string Extension = ".txt";
    public const int MaxNameLength = 40;

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    public static string Folder(string dataRoot) => Path.Combine(dataRoot, FolderName);

    /// <summary>Why <paramref name="name"/> can't be a sequence name, or null when it can.</summary>
    public static string? NameError(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "请填写序列名称。";
        }

        if (name != name.Trim())
        {
            return "名称前后不能有空格。";
        }

        if (name.Length > MaxNameLength)
        {
            return $"名称最多 {MaxNameLength} 个字。";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "名称不能包含 \\ / : * ? \" < > | 等字符。";
        }

        if (name.StartsWith('.') || name.EndsWith('.'))
        {
            return "名称不能以点开头或结尾。";
        }

        if (ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return "这个名称是 Windows 的保留名，请换一个。";
        }

        return null;
    }

    public static string PathFor(string dataRoot, string name)
    {
        if (NameError(name) is { } error)
        {
            throw new ArgumentException(error, nameof(name));
        }

        return Path.Combine(Folder(dataRoot), name + Extension);
    }

    public static bool Exists(string dataRoot, string name) => NameError(name) is null && File.Exists(PathFor(dataRoot, name));

    /// <summary>Saved sequence names, most recently saved first.</summary>
    public static IReadOnlyList<string> List(string dataRoot)
    {
        var folder = Folder(dataRoot);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return new DirectoryInfo(folder).GetFiles("*" + Extension)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .Where(n => NameError(n) is null)
            .ToList();
    }

    public static string Read(string dataRoot, string name) => File.ReadAllText(PathFor(dataRoot, name), Encoding.UTF8);

    /// <summary>Writes through a temporary file, so a failed save never leaves a half-written sequence.</summary>
    public static string Write(string dataRoot, string name, string text)
    {
        var path = PathFor(dataRoot, name);
        Directory.CreateDirectory(Folder(dataRoot));
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return path;
    }
}
