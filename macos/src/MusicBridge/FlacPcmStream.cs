using System;
using System.IO;
using System.Threading;

namespace MusicBridge;

// Background-owned decoder + bounded PCM transport. No Unity dependency: exercised with
// real FLAC fixtures by both .NET tests and the game-Mono probe.
internal sealed class FlacPcmStream : IDisposable
{
    private readonly string _path;
    private readonly IDisposable _fileLease;
    private readonly Func<string, INeteaseAudioDecoder> _factory;
    private PcmRingBuffer _ring;
    private int _requested, _ready = -1, _readers, _closed, _released, _underflow, _enabled;
    private long _target, _consumed, _underruns;
    private volatile bool _eof;
    private int _prebuffer;
    private static int _workers;
    public static int ActiveWorkers => Volatile.Read(ref _workers);
    public PcmFormat Format { get; private set; }
    public string Error { get; private set; }
    public bool Released => Volatile.Read(ref _released) != 0;
    public bool Ready => Volatile.Read(ref _ready) == Volatile.Read(ref _requested) && Volatile.Read(ref _closed) == 0;
    public bool Drained => Ready && _eof && _ring.Count == 0;
    public bool Underflow => Volatile.Read(ref _underflow) != 0;
    public bool CanResume => Ready && (_ring.Count >= _prebuffer || _eof);
    public long PositionFrames => Interlocked.Read(ref _consumed);
    public long Underruns => Interlocked.Read(ref _underruns);
    public long RequestedFrame => Interlocked.Read(ref _target);
    public int BufferedBytes => _ring == null ? 0 : _ring.Capacity * sizeof(float);
    internal sealed class ClipReader
    {
        private readonly FlacPcmStream _stream;
        private readonly int _sequence;
        internal ClipReader(FlacPcmStream stream) { _stream = stream; _sequence = Volatile.Read(ref stream._requested); }
        public void Read(float[] samples) => _stream.Read(samples, _sequence);
    }
    public ClipReader CreateClipReader() => new ClipReader(this);
    public FlacPcmStream(string path, IDisposable lease, long startFrame = 0, Func<string, INeteaseAudioDecoder> factory = null)
    {
        _path = path; _fileLease = lease; _factory = factory ?? (p => new NativeFlacDecoder(p));
        _target = startFrame;
        new Thread(Work) { IsBackground = true, Name = "MusicBridge-FLAC" }.Start();
    }
    public void Enable(bool enabled) => Volatile.Write(ref _enabled, enabled ? 1 : 0);
    public void Seek(long frame)
    {
        Enable(false);
        Interlocked.Exchange(ref _target, Math.Max(0, frame));
        Interlocked.Increment(ref _requested);
    }
    public void ClearUnderflow() => Volatile.Write(ref _underflow, 0);
    // Called only by the audio thread. Always overwrite every scalar sample, even when
    // cancellation/seek races a callback. Neither native code nor a lock is entered.
    public void Read(float[] samples)
    { Read(samples, Volatile.Read(ref _requested)); }
    private void Read(float[] samples, int sequence)
    {
        Interlocked.Increment(ref _readers);
        try
        {
            Array.Clear(samples, 0, samples.Length);
            if (Volatile.Read(ref _closed) != 0 || Volatile.Read(ref _enabled) == 0 ||
                Volatile.Read(ref _requested) != sequence || Volatile.Read(ref _ready) != sequence) return;
            int read = _ring.Read(samples, samples.Length);
            if (sequence != Volatile.Read(ref _requested) || Volatile.Read(ref _closed) != 0)
            { Array.Clear(samples, 0, samples.Length); return; }
            Interlocked.Add(ref _consumed, read / Format.Channels);
            if (read < samples.Length && !_eof && Interlocked.Exchange(ref _underflow, 1) == 0) Interlocked.Increment(ref _underruns);
        }
        finally { Interlocked.Decrement(ref _readers); }
    }
    private void Work()
    {
        Interlocked.Increment(ref _workers);
        try
        {
            using var decoder = _factory(_path);
            Format = decoder.Format;
            int capacity = checked(Format.SampleRate * Format.Channels * 8);
            if ((long)capacity * 4 + 8192 * Format.Channels * 4 > 32L * 1024 * 1024) throw new InvalidDataException("PCM缓冲超过预算");
            _ring = new PcmRingBuffer(capacity);
            _prebuffer = Format.SampleRate * Format.Channels * 2;
            var scratch = new float[8192 * Format.Channels];
            int applied = -1; long decoded = 0;
            while (Volatile.Read(ref _closed) == 0)
            {
                int request = Volatile.Read(ref _requested);
                if (applied != request)
                {
                    Volatile.Write(ref _ready, -1);
                    while (Volatile.Read(ref _readers) != 0) Thread.Sleep(1);
                    long target = Math.Min(Interlocked.Read(ref _target), Format.Frames - 1);
                    decoder.Seek(target); _ring.Reset(); _eof = false;
                    Interlocked.Exchange(ref _consumed, target); decoded = target;
                    Volatile.Write(ref _underflow, 0); applied = request;
                }
                int frames = Math.Min(8192, _ring.Free / Format.Channels);
                if (!_eof && frames > 0)
                {
                    int count = decoder.Read(scratch, frames);
                    decoded += count;
                    if (count == 0 || decoded == Format.Frames)
                    {
                        if (decoded != Format.Frames) throw new InvalidDataException("FLAC解码提前结束");
                        // Publish PCM before EOF; the consumer must not see an empty terminal ring.
                        if (count > 0) _ring.Write(scratch, count * Format.Channels);
                        _eof = true;
                    }
                    else _ring.Write(scratch, count * Format.Channels);
                    if (_ring.Count >= _prebuffer || _eof) Volatile.Write(ref _ready, applied);
                }
                else Thread.Sleep(2);
            }
        }
        catch (Exception ex) { Error = "FLAC播放失败（" + ex.GetType().Name + "）"; }
        finally
        {
            Volatile.Write(ref _ready, -1);
            while (Volatile.Read(ref _readers) != 0) Thread.Sleep(1);
            try { _fileLease?.Dispose(); }
            finally {
                Interlocked.Decrement(ref _workers); Volatile.Write(ref _released, 1);
                BridgeLog.Info("FLAC会话释放 handles=" + NativeFlacDecoder.ActiveHandles + " workers=" + ActiveWorkers + " leases=" + AudioDiskCache.ActiveLeases);
            }
        }
    }
    public void Dispose() { Enable(false); Interlocked.Exchange(ref _closed, 1); }
}
