using System.Buffers.Binary;
using VideoRecorder;

namespace VideoRecorder.Tests;

public class DashStreamTests
{
    private static DashVideo Video(int id, string codecs, int height, double fps, long bandwidth = 1000) =>
        new(id, codecs, height * 16 / 9, height, fps, bandwidth, ["https://example.invalid/v.m4s"], "0-935", "936-1875");

    [Fact]
    public void Choose_takes_1080p_30_over_60_fps_and_prefers_avc()
    {
        var chosen = DashStream.Choose([
            Video(116, "avc1.640032", 1080, 60.0),
            Video(80, "hev1.1.6.L120.90", 1080, 30.001),
            Video(80, "avc1.640032", 1080, 30.001),
            Video(64, "avc1.640028", 720, 30.001),
        ]);

        Assert.NotNull(chosen);
        Assert.Equal(80, chosen.Id);
        Assert.StartsWith("avc1", chosen.Codecs);
    }

    [Fact]
    public void Choose_falls_back_to_lower_quality_and_skips_streams_without_index()
    {
        var noIndex = Video(80, "avc1.640032", 1080, 30) with { Index = "" };
        var chosen = DashStream.Choose([noIndex, Video(32, "avc1.64001F", 480, 30.001)]);

        Assert.Equal(32, chosen?.Id);
        Assert.Null(DashStream.Choose([Video(120, "avc1", 2160, 30), noIndex]));
    }

    [Theory]
    [InlineData("936-1875", true, 936, 1875)]
    [InlineData("0-0", true, 0, 0)]
    [InlineData("5-4", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData("-5", false, 0, 0)]
    public void Ranges_parse(string text, bool ok, long from, long to)
    {
        Assert.Equal(ok, DashStream.TryParseRange(text, out var f, out var t));
        if (ok)
        {
            Assert.Equal((from, to), (f, t));
        }
    }

    /// <summary>ftyp (24 bytes) + a version-0 sidx with the given (size, duration) references at timescale 1000.</summary>
    private static byte[] Head(uint firstOffset, params (uint Size, uint Duration)[] refs)
    {
        var sidxSize = 32 + 12 * refs.Length;
        var bytes = new byte[24 + sidxSize];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, 24);
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        var s = bytes.AsSpan(24);
        BinaryPrimitives.WriteUInt32BigEndian(s, (uint)sidxSize);
        "sidx"u8.CopyTo(s[4..]);
        BinaryPrimitives.WriteUInt32BigEndian(s[12..], 1);       // reference_ID
        BinaryPrimitives.WriteUInt32BigEndian(s[16..], 1000);    // timescale
        BinaryPrimitives.WriteUInt32BigEndian(s[20..], 0);       // earliest_presentation_time
        BinaryPrimitives.WriteUInt32BigEndian(s[24..], firstOffset);
        BinaryPrimitives.WriteUInt16BigEndian(s[30..], (ushort)refs.Length);
        for (var i = 0; i < refs.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s[(32 + 12 * i)..], refs[i].Size);
            BinaryPrimitives.WriteUInt32BigEndian(s[(36 + 12 * i)..], refs[i].Duration);
            BinaryPrimitives.WriteUInt32BigEndian(s[(40 + 12 * i)..], 0x9000_0000);
        }

        return bytes;
    }

    [Fact]
    public void Sidx_gives_fragment_times_and_byte_offsets()
    {
        var head = Head(0, (100, 5000), (200, 4000), (300, 6000));
        var fragments = DashStream.ParseSidx(head);

        Assert.NotNull(fragments);
        Assert.Equal(3, fragments.Count);
        var end = head.Length;
        Assert.Equal(new DashFragment(0, 5, end, 100), fragments[0]);
        Assert.Equal(new DashFragment(5, 4, end + 100, 200), fragments[1]);
        Assert.Equal(new DashFragment(9, 6, end + 300, 300), fragments[2]);
    }

    [Fact]
    public void Sidx_with_nested_index_or_cut_short_is_rejected()
    {
        var nested = Head(0, (100, 5000));
        BinaryPrimitives.WriteUInt32BigEndian(nested.AsSpan(24 + 32), 0x8000_0000 | 100);
        Assert.Null(DashStream.ParseSidx(nested));

        var head = Head(0, (100, 5000), (200, 4000));
        Assert.Null(DashStream.ParseSidx(head.AsSpan(0, head.Length - 4)));
        Assert.Null(DashStream.ParseSidx(head.AsSpan(0, 24)));
    }

    [Fact]
    public void Select_covers_the_span_from_the_fragment_holding_the_start()
    {
        var fragments = DashStream.ParseSidx(Head(0, (1, 5000), (1, 5000), (1, 5000), (1, 5000)))!;

        Assert.Equal((1, 2), DashStream.Select(fragments, 7, 12));
        Assert.Equal((1, 1), DashStream.Select(fragments, 5, 10));
        Assert.Equal((2, 3), DashStream.Select(fragments, 10, null));
        Assert.Null(DashStream.Select(fragments, 25, 30));
    }
}
