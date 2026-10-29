using System.Buffers.Binary;
using System.Globalization;

namespace VideoRecorder;

/// <summary>One DASH video stream the player was offered (only the picture; audio is never fetched).</summary>
internal sealed record DashVideo(int Id, string Codecs, int Width, int Height, double Fps, long Bandwidth, string[] Urls, string Init, string Index);

/// <summary>A media fragment (moof + mdat) listed in the stream's sidx box; times in seconds of video time.</summary>
internal sealed record DashFragment(double Start, double Duration, long Offset, long Size)
{
    public double End => Start + Duration;
}

/// <summary>
/// Picks a stream and finds the byte ranges of a time span in it, so only that part is fetched. Bilibili's streams are
/// fragmented MP4 with one sidx box: init segment (ftyp + moov), then sidx, then fragments that each start on a key frame.
/// </summary>
internal static class DashStream
{
    /// <summary>Highest picture up to 1080 lines and 30 fps (more frames per second do not help recognition); AVC first.</summary>
    public static DashVideo? Choose(IEnumerable<DashVideo> videos) => videos
        .Where(v => v.Urls.Length > 0 && Math.Min(v.Width, v.Height) is > 0 and <= 1080 && v.Fps <= 31
            && TryParseRange(v.Init, out _, out _) && TryParseRange(v.Index, out _, out _))
        .OrderByDescending(v => Math.Min(v.Width, v.Height))
        .ThenByDescending(v => v.Id)
        .ThenBy(v => CodecRank(v.Codecs))
        .ThenByDescending(v => v.Bandwidth)
        .FirstOrDefault();

    /// <summary>AVC decodes everywhere; HEVC and AV1 depend on the bundled FFmpeg build.</summary>
    internal static int CodecRank(string codecs) =>
        codecs.StartsWith("avc", StringComparison.OrdinalIgnoreCase) ? 0
        : codecs.StartsWith("hev", StringComparison.OrdinalIgnoreCase) || codecs.StartsWith("hvc", StringComparison.OrdinalIgnoreCase) ? 1
        : codecs.StartsWith("av01", StringComparison.OrdinalIgnoreCase) ? 2
        : 3;

    /// <summary>"936-1875" (inclusive byte range, as the play info gives it).</summary>
    public static bool TryParseRange(string? text, out long from, out long to)
    {
        from = to = 0;
        var parts = (text ?? string.Empty).Split('-');
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out from)
            && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out to)
            && to >= from;
    }

    /// <summary>
    /// Fragments listed by the first sidx box in <paramref name="head"/> (the file's bytes from offset 0). Returns null
    /// when there is none, or when it points to further index boxes (not used by Bilibili, not supported here).
    /// </summary>
    public static IReadOnlyList<DashFragment>? ParseSidx(ReadOnlySpan<byte> head)
    {
        var pos = 0;
        while (pos + 8 <= head.Length)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(head[pos..]);
            var header = 8;
            if (size == 1)
            {
                if (pos + 16 > head.Length)
                {
                    return null;
                }

                size = (long)BinaryPrimitives.ReadUInt64BigEndian(head[(pos + 8)..]);
                header = 16;
            }

            if (size < header || size > int.MaxValue)
            {
                return null;
            }

            if (head.Slice(pos + 4, 4).SequenceEqual("sidx"u8))
            {
                return pos + size > head.Length || size - header < 32 ? null : ParseSidxBody(head.Slice(pos + header, (int)size - header), pos + size);
            }

            pos += (int)size;
        }

        return null;
    }

    private static IReadOnlyList<DashFragment>? ParseSidxBody(ReadOnlySpan<byte> b, long anchor)
    {
        var version = b[0];
        var timescale = BinaryPrimitives.ReadUInt32BigEndian(b[8..]);
        ulong earliest, firstOffset;
        int p;
        if (version == 0)
        {
            earliest = BinaryPrimitives.ReadUInt32BigEndian(b[12..]);
            firstOffset = BinaryPrimitives.ReadUInt32BigEndian(b[16..]);
            p = 20;
        }
        else
        {
            earliest = BinaryPrimitives.ReadUInt64BigEndian(b[12..]);
            firstOffset = BinaryPrimitives.ReadUInt64BigEndian(b[20..]);
            p = 28;
        }

        if (timescale == 0 || p + 4 > b.Length)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(b[(p + 2)..]);
        p += 4;
        if (p + count * 12 > b.Length)
        {
            return null;
        }

        var fragments = new List<DashFragment>(count);
        var offset = anchor + (long)firstOffset;
        var time = earliest;
        for (var i = 0; i < count; i++, p += 12)
        {
            var reference = BinaryPrimitives.ReadUInt32BigEndian(b[p..]);
            if ((reference & 0x8000_0000) != 0)
            {
                return null;
            }

            var size = reference & 0x7FFF_FFFF;
            var duration = BinaryPrimitives.ReadUInt32BigEndian(b[(p + 4)..]);
            fragments.Add(new DashFragment(time / (double)timescale, duration / (double)timescale, offset, size));
            offset += size;
            time += duration;
        }

        return fragments;
    }

    /// <summary>Index range of the fragments overlapping [start, end) (end null = to the end), or null when none do.</summary>
    public static (int First, int Last)? Select(IReadOnlyList<DashFragment> fragments, double start, double? end)
    {
        var first = -1;
        var last = -1;
        for (var i = 0; i < fragments.Count; i++)
        {
            var f = fragments[i];
            if (f.End > start + 1e-6 && (end is not { } e || f.Start < e - 1e-6))
            {
                first = first < 0 ? i : first;
                last = i;
            }
        }

        return first < 0 ? null : (first, last);
    }
}
