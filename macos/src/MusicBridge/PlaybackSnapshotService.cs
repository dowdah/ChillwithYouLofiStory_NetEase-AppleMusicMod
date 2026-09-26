using System;

namespace MusicBridge;

internal readonly struct PlaybackSnapshot
{
    public readonly MusicProvider Owner;
    public readonly long OwnerEpoch;
    public readonly int PlaybackGeneration;
    public readonly string TrackKey, Title, Artist;
    public readonly PlaybackState State;
    public readonly bool DesiredPlaying, CanPlay, CanPause, CanNext, CanPrevious, CanSeek, SupportsLyrics;
    public readonly double Position, Duration;
    public readonly float Volume;
    public PlaybackSnapshot(MusicProvider owner, long ownerEpoch, int generation, string trackKey,
        string title, string artist, PlaybackState state, bool desiredPlaying, bool canPlay,
        bool canPause, bool canNext, bool canPrevious, bool canSeek, bool supportsLyrics,
        double position, double duration, float volume)
    {
        Owner = owner; OwnerEpoch = ownerEpoch; PlaybackGeneration = generation;
        TrackKey = trackKey; Title = title; Artist = artist; State = state;
        DesiredPlaying = desiredPlaying; CanPlay = canPlay; CanPause = canPause;
        CanNext = canNext; CanPrevious = canPrevious; CanSeek = canSeek;
        SupportsLyrics = supportsLyrics; Position = position; Duration = duration; Volume = volume;
    }
}

// Read-only aggregation of the actual owner; no separate transport state machine.
internal static class PlaybackSnapshotService
{
    private static MusicProvider _owner = MusicProvider.Netease;
    private static bool _hadTrack;
    private static long _ownerEpoch;
    private static long _sessionEpoch;

    internal static PlaybackSnapshot Capture()
    {
        IMusicModule module = MusicModules.Current;
        bool hasTrack = module.HasTrack;
        var player = module.Id == MusicProvider.Netease ? AudioPlayer.Instance : null;
        long session = player?.SessionEpoch ?? 0;
        if (module.Id != _owner || (!hasTrack && _hadTrack) ||
            (module.Id == MusicProvider.Netease && session != _sessionEpoch))
        { _owner = module.Id; _ownerEpoch++; }
        _sessionEpoch = session;
        _hadTrack = hasTrack;
        string trackKey = !hasTrack ? "" : module.Id == MusicProvider.Netease && player?.CurrentTrack != null ?
            "netease:" + player.CurrentTrack.Id : module.Id + ":" + module.Title + ":" + module.Artist;
        PlaybackState state = !hasTrack ? PlaybackState.Idle :
            player != null ? player.State : module.IsPlaying ? PlaybackState.Playing : PlaybackState.Paused;
        if (player != null && (player.IsBuffering || AudioOutputRecovery.OutputUnavailable) &&
            state == PlaybackState.Playing) state = PlaybackState.Loading;
        bool desiredPlaying = player != null ? player.DesiredPlaying : module.IsPlaying;
        double duration = hasTrack ? module.Duration : 0;
        if (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0) duration = 0;
        double position = hasTrack ? module.Position : 0;
        if (double.IsNaN(position) || double.IsInfinity(position) || position < 0) position = 0;
        if (duration > 0 && position > duration) position = duration;
        bool canSeek = hasTrack && duration > 0 && module.CanSeek;
        return new PlaybackSnapshot(module.Id, _ownerEpoch, player?.PlaybackGeneration ?? 0,
            trackKey, hasTrack ? module.Title : null, hasTrack ? module.Artist : null,
            state, desiredPlaying, hasTrack && !desiredPlaying, hasTrack && desiredPlaying,
            MusicTransport.CanNext, MusicTransport.CanPrevious, canSeek,
            hasTrack && module.SupportsLyrics, position,
            duration, module.Volume);
    }
}
