using System;
using System.Globalization;
using System.Text;

namespace MusicBridge;

internal readonly struct PresenceTextState
{
    internal readonly PlaybackState MusicState;
    internal readonly string Title, TimerText;
    internal readonly bool ShowMusic, ShowTitle;
    internal PresenceTextState(PlaybackState musicState, string title, string timerText, bool showMusic, bool showTitle)
    { MusicState = musicState; Title = title; TimerText = timerText; ShowMusic = showMusic; ShowTitle = showTitle; }
}

// Music is presented first. Preserve basic music/timer states before spending bytes on a public title.
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
        // Never show a stale title for idle/failed/loading state or when music visibility is disabled.
        string title = state.ShowMusic && state.ShowTitle &&
            (state.MusicState == PlaybackState.Playing || state.MusicState == PlaybackState.Paused)
            ? Sanitize(state.Title) : "";
        if (title.Length == 0) return music + timerSuffix;
        const string separator = "：";
        int remaining = MaxUtf8Bytes - Encoding.UTF8.GetByteCount(music + separator + timerSuffix);
        string publicTitle = remaining <= 0 ? "" : Truncate(title, remaining);
        return music + (publicTitle.Length == 0 ? "" : separator + publicTitle) + timerSuffix;
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
