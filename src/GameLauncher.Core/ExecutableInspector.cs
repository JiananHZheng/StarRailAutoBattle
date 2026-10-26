using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using GameLauncher.Core.Native;

namespace GameLauncher.Core;

public enum PeArchitecture
{
    Unknown,
    X86,
    X64,
    Arm64,
}

/// <summary>Value of requestedExecutionLevel in the program's embedded manifest.</summary>
public enum ExecutionLevel
{
    /// <summary>No manifest or no requestedExecutionLevel; Windows treats it like asInvoker (may still apply installer detection).</summary>
    NotSpecified,
    AsInvoker,
    HighestAvailable,
    RequireAdministrator,
    /// <summary>The manifest could not be read.</summary>
    Unknown,
}

public sealed record ExecutableInfo(
    string Path,
    PeArchitecture Architecture,
    ExecutionLevel ExecutionLevel,
    string? ProductName,
    string? FileDescription)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public static partial class ExecutableInspector
{
    private static readonly byte[] LnkHeader =
    [
        0x4C, 0x00, 0x00, 0x00, 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46,
    ];

    public enum FileSignature
    {
        Unknown,
        PortableExecutable,
        Shortcut,
    }

    /// <summary>Detects the real file format from its first bytes, independent of the file name.</summary>
    public static FileSignature DetectSignature(string path)
    {
        Span<byte> header = stackalloc byte[LnkHeader.Length];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        if (read >= LnkHeader.Length && header.SequenceEqual(LnkHeader))
        {
            return FileSignature.Shortcut;
        }

        if (read >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z')
        {
            return FileSignature.PortableExecutable;
        }

        return FileSignature.Unknown;
    }

    /// <summary>Reads PE machine type. Returns null when the file is not a valid PE image.</summary>
    public static PeArchitecture? ReadArchitecture(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D)
        {
            return null;
        }

        stream.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        if (peOffset <= 0 || peOffset > stream.Length - 6)
        {
            return null;
        }

        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550)
        {
            return null;
        }

        return reader.ReadUInt16() switch
        {
            0x014C => PeArchitecture.X86,
            0x8664 => PeArchitecture.X64,
            0xAA64 => PeArchitecture.Arm64,
            _ => PeArchitecture.Unknown,
        };
    }

    public static ExecutableInfo Inspect(string path, PeArchitecture architecture)
    {
        string? product = null;
        string? description = null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            product = NullIfBlank(version.ProductName);
            description = NullIfBlank(version.FileDescription);
        }
        catch (Exception)
        {
            // Version resources are informational only.
        }

        return new ExecutableInfo(path, architecture, ReadExecutionLevel(path), product, description);
    }

    public static ExecutionLevel ReadExecutionLevel(string path)
    {
        var manifest = ReadManifest(path, out var readFailed);
        if (readFailed)
        {
            return ExecutionLevel.Unknown;
        }

        return manifest is null ? ExecutionLevel.NotSpecified : ParseExecutionLevel(manifest);
    }

    public static ExecutionLevel ParseExecutionLevel(string manifestXml)
    {
        var match = ExecutionLevelRegex().Match(manifestXml);
        if (!match.Success)
        {
            return ExecutionLevel.NotSpecified;
        }

        return match.Groups[1].Value.ToLowerInvariant() switch
        {
            "asinvoker" => ExecutionLevel.AsInvoker,
            "highestavailable" => ExecutionLevel.HighestAvailable,
            "requireadministrator" => ExecutionLevel.RequireAdministrator,
            _ => ExecutionLevel.Unknown,
        };
    }

    private static string? ReadManifest(string path, out bool readFailed)
    {
        readFailed = false;
        var module = NativeMethods.LoadLibraryExW(
            path, IntPtr.Zero, NativeMethods.LOAD_LIBRARY_AS_DATAFILE | NativeMethods.LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (module == IntPtr.Zero)
        {
            readFailed = true;
            return null;
        }

        try
        {
            // 1 = CREATEPROCESS_MANIFEST_RESOURCE_ID, used by Windows when creating the process.
            var resource = NativeMethods.FindResourceW(module, 1, NativeMethods.RT_MANIFEST);
            if (resource == IntPtr.Zero)
            {
                return null;
            }

            var size = NativeMethods.SizeofResource(module, resource);
            var data = NativeMethods.LockResource(NativeMethods.LoadResource(module, resource));
            if (data == IntPtr.Zero || size == 0)
            {
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            NativeMethods.FreeLibrary(module);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("""requestedExecutionLevel[^>]*?\blevel\s*=\s*["']([A-Za-z]+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutionLevelRegex();
}
