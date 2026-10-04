using UnityEngine;

namespace MusicBridge;

internal sealed partial class AudioPlayer
{
    private NeteaseAudioPrefetch _prefetch;
    private int _prefetchHits, _prefetchCancelled;
    private string _lastPrefetchCancelReason = "无";
    internal int PrefetchHits => System.Threading.Volatile.Read(ref _prefetchHits);
    internal int PrefetchCancelled => System.Threading.Volatile.Read(ref _prefetchCancelled);
    internal string LastPrefetchCancelReason => _lastPrefetchCancelReason;
    internal string PrefetchStatus => _prefetch == null ? "空" :
        (_prefetch.Ready ? "已准备" : _prefetch.Done ? "未就绪" : "准备中") +
        "（计划 " + _prefetch.PlanId + "，音质 " + NeteaseQualityPolicy.Label(_prefetch.Quality) + "）";
    private void CancelPrefetch(string reason = "状态变化")
    {
        var prefetch = _prefetch; _prefetch = null;
        if (prefetch == null) return;
        _lastPrefetchCancelReason = reason;
        System.Threading.Interlocked.Increment(ref _prefetchCancelled);
        prefetch.Dispose();
    }
    private void TickPrefetch()
    {
        var context = NeteaseRuntime.Context;
        var options = MusicBridgeOptions.Current.Netease;
        if (!options.NextAudioPreload || context == null || !context.Active ||
            PlaybackCoordinator.Active != MusicProvider.Netease || AudioOutputRecovery.OutputUnavailable || IsBuffering ||
            (_flacStream != null && _flacStream.Underflow) ||
            (_progressiveSession != null && !_progressiveSession.Download.Validated) ||
            State == PlaybackState.Paused || State == PlaybackState.Idle || State == PlaybackState.Failed)
        { CancelPrefetch("不具备预下载条件"); return; }
        if (State == PlaybackState.Loading)
        {
            // A completed slot can be claimed after the foreground's fresh URL lookup.
            // An unfinished speculative transfer immediately yields bandwidth to foreground.
            if (_prefetch != null && (!_prefetch.Done || _prefetch.SongId != CurrentTrack?.Id || _prefetch.Quality != options.PreferredQuality)) CancelPrefetch("前台加载优先");
            return;
        }
        if (Time.realtimeSinceStartup - _trackStartedAt < 2) return;
        TrackInfo next = null;
        long planId = 0;
        if (IsFm) { if (!NeteaseRuntime.Fm.Suspended) next = NeteaseRuntime.Fm.PeekNext; }
        else if (Shuffle && options.NoRepeatShuffle && !RepeatOne)
        {
            var plan = _shuffleNavigator.PeekNext(RepeatQueue);
            if (plan.HasValue) { next = _queue[plan.Value.QueueIndex]; planId = plan.Value.Id; }
        }
        else if (!Shuffle && !RepeatOne && _queue.Count > 1)
        {
            int start = _index + 1;
            if (start >= _queue.Count && RepeatQueue) start = 0;
            if (start < _queue.Count)
            {
                int index = FirstPlayableFrom(start, RepeatQueue, out _);
                if (index >= 0) next = _queue[index];
            }
        }
        if (next == null || !next.Playable || next.Id == CurrentTrack?.Id) { CancelPrefetch("没有下一首候选"); return; }
        if (_prefetch != null && _prefetch.SongId == next.Id && _prefetch.Quality == options.PreferredQuality &&
            ReferenceEquals(_prefetch.Context, context) && _prefetch.Generation == _generation &&
            _prefetch.PlanId == planId) return;
        CancelPrefetch("候选计划变化");
        _prefetch = new NeteaseAudioPrefetch(next.Id, options.PreferredQuality, context, _generation, planId);
    }
}
