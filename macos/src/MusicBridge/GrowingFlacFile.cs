using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MusicBridge;

// The HTTP writer publishes only bytes visible to a separate file reader. dr_flac's
// read callback must never return a short read merely because the file is growing:
// that would make a temporary network gap look like a permanent FLAC EOF.
internal sealed class GrowingFlacFile
{
    private readonly object _gate = new object();
    private readonly long _limit;
    private long _published;
    private int _epoch;
    private bool _complete, _closed;
    private string _error;
    public GrowingFlacFile(long limit) { _limit = limit; }
    public long Published { get { lock (_gate) return _published; } }
    public bool Complete { get { lock (_gate) return _complete; } }
    public string Error { get { lock (_gate) return _error; } }
    public int Epoch { get { lock (_gate) return _epoch; } }

    public void Publish(long bytes)
    {
        lock (_gate)
        {
            if (_closed || _complete || _error != null) return;
            if (bytes < _published || bytes > _limit) throw new InvalidDataException("FLAC增长文件长度异常");
            _published = bytes;
            Monitor.PulseAll(_gate);
        }
    }
    public void Finish()
    {
        lock (_gate) { _complete = true; Monitor.PulseAll(_gate); }
    }
    public void Fail(string error)
    {
        lock (_gate) { _error = error ?? "FLAC下载失败"; Monitor.PulseAll(_gate); }
    }
    public void Interrupt()
    {
        lock (_gate) { _epoch++; Monitor.PulseAll(_gate); }
    }
    public void Close()
    {
        lock (_gate) { _closed = true; _epoch++; Monitor.PulseAll(_gate); }
    }

    // Runs on the decoder worker through the native ABI, never on Unity's audio callback.
    public int Read(FileStream file, ref long cursor, byte[] scratch, IntPtr destination, int requested, int epoch)
    {
        int copied = 0;
        while (copied < requested)
        {
            int available;
            lock (_gate)
            {
                while (cursor >= _published && !_complete && _error == null && !_closed && epoch == _epoch)
                    Monitor.Wait(_gate);
                if (epoch != _epoch || _closed || _error != null || cursor >= _published) break;
                available = (int)Math.Min(Math.Min((long)(requested - copied), _published - cursor), scratch.Length);
            }
            file.Position = cursor;
            int count = file.Read(scratch, 0, available);
            if (count <= 0) throw new IOException("已发布的FLAC字节不可读");
            Marshal.Copy(scratch, 0, IntPtr.Add(destination, copied), count);
            cursor += count; copied += count;
        }
        return copied;
    }
    public bool Seek(ref long cursor, int offset, int origin, int epoch)
    {
        lock (_gate)
        {
            if (_closed || epoch != _epoch || _error != null) return false;
            if (origin == 2 && !_complete) return false;
            long basis = origin == 0 ? 0 : origin == 1 ? cursor : origin == 2 ? _published : -1;
            if (basis < 0 || basis > _limit - offset) return false;
            long target = basis + offset;
            if (_complete && target > _published) return false;
            cursor = target;
            return true;
        }
    }
}
