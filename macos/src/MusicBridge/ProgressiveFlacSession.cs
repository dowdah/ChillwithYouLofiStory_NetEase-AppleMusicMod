using System;
using System.IO;
using System.Threading;

namespace MusicBridge;

// Owns the download, decoder and temporary file as one lifetime. Closing is
// nonblocking for Unity's main thread; only the reaper releases the file lease.
internal sealed class ProgressiveFlacSession : IDisposable
{
    private readonly AudioDiskCache.Lease _file;
    private readonly string _path;
    private int _closed, _released;
    private static int _sessions;
    public static int ActiveSessions => Volatile.Read(ref _sessions);
    public bool Released => Volatile.Read(ref _released) != 0;
    internal string TemporaryPath => _path;
    public GrowingFlacFile Growth { get; }
    public ProgressiveFlacDownload Download { get; }
    public FlacPcmStream Pcm { get; }
    public ProgressiveFlacSession(NeteaseAccountContext context, NeteasePlaybackSource source, long startFrame)
    {
        _file = AudioDiskCache.CreateTemporary();
        _path = _file.Path;
        Growth = new GrowingFlacFile(MusicBridgeOptions.Current.Netease.FlacMaximumDownloadBytes);
        try
        {
            Download = new ProgressiveFlacDownload(context, source, _file, Growth);
            Pcm = new FlacPcmStream(_file.Path, null, startFrame,
                p => new NativeFlacDecoder(p, Growth), Growth.Interrupt);
            Interlocked.Increment(ref _sessions);
        }
        catch
        {
            Download?.Dispose();
            if (Download == null || Download.Done) _file.Dispose();
            else new Thread(() => { while (!Download.Done) Thread.Sleep(2); _file.Dispose(); })
                { IsBackground = true, Name = "MusicBridge-FLAC-open-cleanup" }.Start();
            throw;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Download.Dispose(); Pcm.Dispose();
        new Thread(() => {
            while (!Download.Done || !Pcm.Released) Thread.Sleep(2);
            _file.Dispose();
            for (int i = 0; i < 500 && File.Exists(_path); i++) Thread.Sleep(10);
            if (File.Exists(_path)) BridgeLog.Warn("FLAC流式临时文件清理未完成。");
            Interlocked.Decrement(ref _sessions);
            Volatile.Write(ref _released, 1);
            BridgeLog.Info("FLAC流式会话释放 handles=" + NativeFlacDecoder.ActiveHandles +
                " workers=" + FlacPcmStream.ActiveWorkers + " leases=" + AudioDiskCache.ActiveLeases +
                " sessions=" + ActiveSessions);
        }) { IsBackground = true, Name = "MusicBridge-FLAC-reaper" }.Start();
    }
}
