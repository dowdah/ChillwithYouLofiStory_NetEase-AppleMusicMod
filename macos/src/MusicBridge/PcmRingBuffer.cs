using System;
using System.Threading;

namespace MusicBridge;

// SPSC; Reset is legal only after producer invalidates ReadySequence and readers drain.
internal sealed class PcmRingBuffer
{
    private readonly float[] _samples;
    private long _read, _written;
    public PcmRingBuffer(int capacity) { _samples = new float[capacity]; }
    public int Capacity => _samples.Length;
    public int Count => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));
    public int Free => Capacity - Count;
    public void Reset() { Volatile.Write(ref _read, 0); Volatile.Write(ref _written, 0); }
    public void Write(float[] source, int count)
    {
        long start = _written;
        int offset = (int)(start % Capacity), first = Math.Min(count, Capacity - offset);
        Array.Copy(source, 0, _samples, offset, first);
        if (first < count) Array.Copy(source, first, _samples, 0, count - first);
        Volatile.Write(ref _written, start + count);
    }
    public int Read(float[] target, int count)
    {
        long start = _read;
        count = Math.Min(count, (int)(Volatile.Read(ref _written) - start));
        int offset = (int)(start % Capacity), first = Math.Min(count, Capacity - offset);
        Array.Copy(_samples, offset, target, 0, first);
        if (first < count) Array.Copy(_samples, 0, target, first, count - first);
        Volatile.Write(ref _read, start + count);
        return count;
    }
}
