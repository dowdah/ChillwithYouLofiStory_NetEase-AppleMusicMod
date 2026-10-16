using System;
using System.Globalization;
using System.Text;

namespace MusicBridge;

internal readonly struct PresenceTextState
{
    internal readonly PlaybackState MusicState;
    internal readonly string Title, Artist, TimerText;
    internal readonly bool ShowMusic, ShowTitle;
    internal PresenceTextState(PlaybackState musicState, string title, string timerText, bool showMusic, bool showTitle, string artist = null)
    { MusicState = musicState; Title = title; Artist = artist; TimerText = timerText; ShowMusic = showMusic; ShowTitle = showTitle; }
}

// Music first. Public metadata is opt-in; playing music can displace the timer when space is insufficient.
internal static class SteamPresenceFormatter
{
    internal const int MaxUtf8Bytes = 255;
    internal static string Format(PresenceTextState state)
    {
        string timer = Sanitize(state.TimerText);
        string music = !state.ShowMusic ? "" : state.MusicState switch
        {
            PlaybackState.Loading => "音乐缓冲中",
            PlaybackState.Playing => "音乐播放中",
            PlaybackState.Paused => "音乐已暂停",
            PlaybackState.Failed => "音乐播放失败",
            _ => ""
        };
        if (music.Length == 0) return timer.Length == 0 ? null : Truncate(timer, MaxUtf8Bytes);
        const string sectionSeparator = " · ";
        timer = Truncate(timer, MaxUtf8Bytes - Encoding.UTF8.GetByteCount(music + sectionSeparator));
        string timerSuffix = timer.Length == 0 ? "" : sectionSeparator + timer;
        // Never show stale metadata for idle/failed/loading state or when music visibility is disabled.
        bool share = state.ShowMusic && state.ShowTitle &&
            (state.MusicState == PlaybackState.Playing || state.MusicState == PlaybackState.Paused);
        string title = share ? Sanitize(state.Title) : "";
        string artist = share ? Sanitize(state.Artist) : "";
        string metadata = JoinMetadata(title, artist);
        if (metadata.Length == 0) return music + timerSuffix;
        const string separator = "：";
        if (state.MusicState == PlaybackState.Playing &&
            Encoding.UTF8.GetByteCount(music + separator + metadata + timerSuffix) > MaxUtf8Bytes)
            timerSuffix = "";
        int remaining = MaxUtf8Bytes - Encoding.UTF8.GetByteCount(music + separator + timerSuffix);
        string publicMetadata = FitMetadata(title, artist, remaining);
        return music + (publicMetadata.Length == 0 ? "" : separator + publicMetadata) + timerSuffix;
    }

    private const string ArtistSeparator = " — ";
    private static string JoinMetadata(string title, string artist) =>
        title.Length > 0 && artist.Length > 0 ? title + ArtistSeparator + artist : title + artist;

    private static string FitMetadata(string title, string artist, int bytes)
    {
        string full = JoinMetadata(title, artist);
        if (title.Length == 0 || artist.Length == 0 || Encoding.UTF8.GetByteCount(full) <= bytes)
            return Truncate(full, bytes);
        int available = bytes - Encoding.UTF8.GetByteCount(ArtistSeparator);
        if (available < 6) return Truncate(full, bytes);
        // Retain a visible fragment of both names rather than allowing a long title to erase the artist.
        int artistBytes = Math.Min(Encoding.UTF8.GetByteCount(artist), available / 2);
        int titleBytes = Math.Min(Encoding.UTF8.GetByteCount(title), available - artistBytes);
        artistBytes = available - titleBytes;
        return Truncate(title, titleBytes) + ArtistSeparator + Truncate(artist, artistBytes);
    }

    internal static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var result = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                { result.Append(c); result.Append(value[++i]); }
                continue;
            }
            if (char.IsLowSurrogate(c)) continue;
            UnicodeCategory category = char.GetUnicodeCategory(c);
            // Remove control, line/paragraph separators and formatting controls (including bidi overrides).
            // Emoji variation selectors remain; removing ZWJ is safe and avoids invisible arbitrary formatting.
            if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator) continue;
            result.Append(c);
        }
        return result.ToString().Trim();
    }

    internal static string Truncate(string value, int bytes)
    {
        if (bytes <= 0 || string.IsNullOrEmpty(value)) return "";
        if (Encoding.UTF8.GetByteCount(value) <= bytes) return value;
        const string ellipsis = "…";
        int budget = bytes >= 3 ? bytes - 3 : bytes;
        int used = 0, end = 0;
        while (end < value.Length)
        {
            int length = char.IsHighSurrogate(value[end]) && end + 1 < value.Length &&
                char.IsLowSurrogate(value[end + 1]) ? 2 : 1;
            int cost = Encoding.UTF8.GetByteCount(value.Substring(end, length));
            if (used + cost > budget) break;
            used += cost; end += length;
        }
        return value.Substring(0, end) + (bytes >= 3 ? ellipsis : "");
    }
}
