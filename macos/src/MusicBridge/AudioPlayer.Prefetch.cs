using UnityEngine;

namespace MusicBridge;

internal sealed partial class AudioPlayer
{
    private NeteaseAudioPrefetch _prefetch;
    private void CancelPrefetch() { var prefetch = _prefetch; _prefetch = null; prefetch?.Dispose(); }
    private void TickPrefetch()
    {
        var context = NeteaseRuntime.Context;
        var options = MusicBridgeOptions.Current.Netease;
        if (!options.NextAudioPreload || context == null || !context.Active ||
            PlaybackCoordinator.Active != MusicProvider.Netease || AudioOutputRecovery.OutputUnavailable || IsBuffering ||
            State == PlaybackState.Paused || State == PlaybackState.Idle || State == PlaybackState.Failed)
        { CancelPrefetch(); return; }
        if (State == PlaybackState.Loading)
        {
            // A completed slot can be claimed after the foreground's fresh URL lookup.
            // An unfinished speculative transfer immediately yields bandwidth to foreground.
            if (_prefetch != null && (!_prefetch.Done || _prefetch.SongId != CurrentTrack?.Id || _prefetch.Quality != options.PreferredQuality)) CancelPrefetch();
            return;
        }
        if (Time.realtimeSinceStartup - _trackStartedAt < 2) return;
        TrackInfo next = null;
        if (IsFm) { if (!NeteaseRuntime.Fm.Suspended) next = NeteaseRuntime.Fm.PeekNext; }
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
        if (next == null || !next.Playable || next.Id == CurrentTrack?.Id) { CancelPrefetch(); return; }
        if (_prefetch != null && _prefetch.SongId == next.Id && _prefetch.Quality == options.PreferredQuality &&
            ReferenceEquals(_prefetch.Context, context) && _prefetch.Generation == _generation) return;
        CancelPrefetch();
        _prefetch = new NeteaseAudioPrefetch(next.Id, options.PreferredQuality, context, _generation);
    }
}
