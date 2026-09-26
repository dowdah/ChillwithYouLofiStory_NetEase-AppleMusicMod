namespace MusicBridge;

internal sealed class MediaCommandGate
{
    private ulong _lastSequence;
    public void Reset() { _lastSequence = 0; }

    public bool Accept(NativeMediaCommand command, ulong activeEpoch, ulong currentTrackToken, bool hasTrack)
    {
        if (!hasTrack || command.Abi != 1 || command.Size != 64 ||
            command.Sequence <= _lastSequence || command.OwnerEpoch != activeEpoch ||
            double.IsNaN(command.AgeSeconds) || double.IsInfinity(command.AgeSeconds) ||
            command.AgeSeconds < 0 || command.AgeSeconds > 2 ||
            command.Type < 1 || command.Type > 6) return false;
        _lastSequence = command.Sequence;
        var kind = (MediaCommandKind)command.Type;
        // Two separately received Next events remain valid after the first changes tracks.
        return kind == MediaCommandKind.Next || kind == MediaCommandKind.Previous ||
            command.TrackToken == currentTrackToken;
    }
}
