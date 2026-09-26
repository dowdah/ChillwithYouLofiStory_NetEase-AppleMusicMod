using System;
using System.Collections.Generic;
using MusicBridge;

internal static class LyricSnapshotSelectorTests
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

    public static void Run()
    {
        var lines = new List<LyricLine>
        {
            new LyricLine { TimeSeconds = 0, Text = "第一行", Translation = "first" },
            new LyricLine { TimeSeconds = 5, Text = "第二行", Translation = "second" },
            new LyricLine { TimeSeconds = 10, Text = "第三行", Translation = "third" },
        };
        var main = LyricSnapshotSelector.Select("netease:7", 3, 7, LyricsState.Ready, "", lines, 5.2);
        var overlay = LyricSnapshotSelector.Select("netease:7", 3, 7, LyricsState.Ready, "", lines, 0.5);
        var mainAgain = LyricSnapshotSelector.Select("netease:7", 3, 7, LyricsState.Ready, "", lines, 5.2);
        Check(main.LineIndex == 1 && main.OriginalText == "第二行" && main.TranslationText == "second" &&
            overlay.LineIndex == 0 && mainAgain.LineIndex == main.LineIndex,
            "independent views read lyrics without consuming a shared changed flag");
        Check(LyricSnapshotSelector.Select("netease:7", 3, 7, LyricsState.Ready, "", lines, 10.5).LineIndex == 2 &&
            LyricSnapshotSelector.Select("netease:7", 3, 7, LyricsState.Ready, "", lines, 5.2).LineIndex == 1,
            "seek relocates by real position and pause does not advance on wall clock");
        var loading = LyricSnapshotSelector.Select("netease:8", 4, 8, LyricsState.Loading,
            "歌词加载中", Array.Empty<LyricLine>(), 100);
        Check(loading.ContextKey == "netease:8" && loading.LineIndex == -1 && loading.OriginalText == null,
            "new track loading cannot retain previous track lyrics");
        Check(LyricSnapshotSelector.Select("netease:7", 3, 7, LyricsState.Ready, "", lines, double.NaN).LineIndex == -1,
            "invalid position does not select a lyric line");
    }
}
