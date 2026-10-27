namespace VideoRecorder;

/// <summary>
/// Maps irregular captures (at whatever media time the player was at) onto a constant-rate output file whose
/// timeline equals the video's: output frame k shows media time start + k / fps. Each output frame takes the
/// capture nearest to its time, so buffering pauses and slow captures never shift later frames.
/// </summary>
public sealed class FrameTimeline(double start, double fps)
{
    private double? _previous;

    public int Written { get; private set; }

    /// <summary>
    /// For a capture at media time <paramref name="time"/>, returns how many pending output frames should repeat the
    /// previous capture, then how many should use this one. Captures that go back in time are ignored (0, 0).
    /// </summary>
    public (int FromPrevious, int FromCurrent) Add(double time)
    {
        if (_previous is { } prev && time < prev)
        {
            return (0, 0);
        }

        var last = (int)Math.Floor((time - start) * fps + 1e-6);
        var count = Math.Max(0, last - Written + 1);
        var fromPrevious = 0;
        if (_previous is { } p)
        {
            var midpoint = (p + time) / 2;
            while (fromPrevious < count && start + (Written + fromPrevious) / fps < midpoint)
            {
                fromPrevious++;
            }
        }

        _previous = time;
        Written += count;
        return (fromPrevious, count - fromPrevious);
    }

    /// <summary>Frames still owed up to <paramref name="end"/> (exclusive), all showing the last capture.</summary>
    public int Finish(double end)
    {
        var total = Math.Max(Written, (int)Math.Ceiling((end - start) * fps - 1e-6));
        var remaining = total - Written;
        Written = total;
        return _previous is null ? 0 : remaining;
    }
}
