using System;
using System.Threading;

namespace MusicBridge;

// One speculative compressed file, no AudioSource, no queue mutations, no PCM cache.
// Foreground playback always obtains fresh authorization before Take().
internal sealed class NeteaseAudioPrefetch : IDisposable
{
#if MUSICBRIDGE_TESTS
    internal static string TestDownloadUrl;
#endif
    private readonly object _gate = new object();
    private readonly NeteaseRequestCancellation _cancellation = new NeteaseRequestCancellation();
    private AudioFilePreparation _preparation;
    private AudioDiskCache.Lease _file;
    private NeteasePlaybackSource _source;
    private int _closed, _done;
    public readonly long SongId;
    public readonly NeteaseQuality Quality;
    public readonly NeteaseAccountContext Context;
    public readonly int Generation;
    public bool Done => Volatile.Read(ref _done) != 0;
    public NeteaseAudioPrefetch(long songId, NeteaseQuality quality, NeteaseAccountContext context, int generation)
    {
        SongId = songId; Quality = quality; Context = context; Generation = generation;
        new Thread(Work) { IsBackground = true, Name = "MusicBridge-Prefetch" }.Start();
    }
    private bool Cancelled => Volatile.Read(ref _closed) != 0 || !Context.Active;
    private void Work()
    {
        AudioFilePreparation preparation = null;
        try
        {
            var quality = Quality;
            NeteaseResult<NeteasePlaybackSource> result;
            while (true)
            {
                if (Cancelled) return;
                result = NeteaseApi.GetPlaybackSource(SongId, quality, Context, _cancellation);
                if (result.Ok) break;
                if (!NeteaseQualityPolicy.CanFallback(result.Failure) || !(NeteaseQualityPolicy.Lower(quality) is NeteaseQuality lower)) return;
                quality = lower;
            }
            var source = result.Value;
#if MUSICBRIDGE_TESTS
            if (TestDownloadUrl != null) source.Url = TestDownloadUrl;
#endif
            source.RequestedQuality = Quality; source.AttemptedQuality = quality;
            if (!source.IsTrial.HasValue || !source.SizeBytes.HasValue || !source.Bitrate.HasValue || Cancelled) return;
            // Existing valid cache already meets the objective; pin it until use/cancel.
            var cached = AudioDiskCache.TryGet(Context.UserId, source);
            if (cached != null)
            {
                lock (_gate) { if (Cancelled) cached.Dispose(); else { _file = cached; _source = source; } }
                return;
            }
            preparation = new AudioFilePreparation(Context, source, null, publishCache: false);
            lock (_gate) { if (Cancelled) preparation.Dispose(); else _preparation = preparation; }
            while (!preparation.Done && !Cancelled) Thread.Sleep(10);
            if (Cancelled || preparation.Error != null) return;
            var file = preparation.TakeFile();
            lock (_gate)
            {
                if (Cancelled) file?.Dispose();
                else { _file = file; _source = source; }
            }
            if (!Cancelled && file != null) BridgeLog.Info("下一首预下载就绪 songId=" + SongId +
                " format=" + source.Format + " download_s=" + preparation.DownloadSeconds.ToString("F3"));
        }
        catch (Exception ex) { if (!Cancelled) BridgeLog.Info("下一首预下载未采用（" + ex.GetType().Name + "），正常播放不受影响。"); }
        finally
        {
            preparation?.Dispose();
            lock (_gate) { _preparation = null; Volatile.Write(ref _done, 1); }
        }
    }
    public AudioDiskCache.Lease Take(NeteaseAccountContext context, NeteasePlaybackSource fresh)
    {
        lock (_gate)
        {
            if (Cancelled || !Done || !ReferenceEquals(context, Context) || _file == null || !AudioDiskCache.SameSource(_source, fresh)) return null;
            var file = _file; _file = null;
            fresh.WasPrefetched = true;
            Volatile.Write(ref _closed, 1);
            return file;
        }
    }
    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1); _cancellation.Cancel();
        lock (_gate) { _preparation?.Dispose(); _file?.Dispose(); _file = null; }
    }
}
