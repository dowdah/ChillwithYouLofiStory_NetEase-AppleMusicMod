namespace MusicBridge;

internal readonly struct LyricSnapshot
{
    public readonly string ContextKey;
    public readonly int Revision;
    public readonly long TrackId;
    public readonly LyricsState State;
    public readonly string StatusText;
    public readonly int LineIndex;
    public readonly double LineStartSeconds;
    public readonly string OriginalText;
    public readonly string TranslationText;
    public LyricSnapshot(string contextKey, int revision, long trackId, LyricsState state,
        string statusText, int lineIndex, double lineStartSeconds, string original, string translation)
    {
        ContextKey = contextKey; Revision = revision; TrackId = trackId; State = state;
        StatusText = statusText; LineIndex = lineIndex; LineStartSeconds = lineStartSeconds;
        OriginalText = original; TranslationText = translation;
    }
}
