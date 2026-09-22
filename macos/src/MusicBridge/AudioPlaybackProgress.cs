using System;

namespace MusicBridge;

// A device reconnect may move AudioSource.time straight to the end of its clip.
// Only elapsed-time-consistent samples may become an end-of-track observation.
internal sealed class AudioPlaybackProgress
{
    public double Position { get; private set; }
    private double _observedAt;
    private bool _initialized;

    // Unity Play() may reset a newly assigned clip to zero. Seek only after
    // starting/resuming; caller retains the saved position while it is paused.
    public static void ResumeAt(float position, Action startOrResume, Action<float> seek)
    {
        startOrResume();
        seek(position);
    }

    public void Reset(double position, double now)
    {
        Position = Math.Max(0, position);
        _observedAt = now;
        _initialized = true;
    }

    public bool TryObserve(double position, double now)
    {
        if (double.IsNaN(position) || double.IsInfinity(position) || position < 0) return false;
        if (!_initialized) { Reset(position, now); return true; }
        double elapsed = Math.Max(0, now - _observedAt);
        // Unity updates compressed-audio positions in packets. Allow one second
        // of packet jitter, but never learn an unexplained jump or clock rewind.
        if (position - Position > elapsed + 1 || Position - position > 1) return false;
        Reset(position, now);
        return true;
    }
}
