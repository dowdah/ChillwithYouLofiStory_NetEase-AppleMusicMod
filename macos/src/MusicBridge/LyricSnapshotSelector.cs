using System;
using System.Collections.Generic;

namespace MusicBridge;

// Pure, non-consuming time lookup shared by any number of views.
internal static class LyricSnapshotSelector
{
    internal static LyricSnapshot Select(string context, int revision, long trackId,
        LyricsState state, string status, IReadOnlyList<LyricLine> lines, double position)
    {
        int index = -1;
        if (state == LyricsState.Ready && lines != null && lines.Count > 0 &&
            !double.IsNaN(position) && !double.IsInfinity(position))
        {
            int lo = 0, hi = lines.Count - 1;
            double target = position + 0.02;
            while (lo <= hi)
            {
                int mid = lo + (hi - lo) / 2;
                if (lines[mid].TimeSeconds <= target) { index = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
        }
        LyricLine line = index >= 0 ? lines[index] : default;
        return new LyricSnapshot(context, revision, trackId, state, status, index,
            index >= 0 ? line.TimeSeconds : 0, line.Text, line.Translation);
    }
}
