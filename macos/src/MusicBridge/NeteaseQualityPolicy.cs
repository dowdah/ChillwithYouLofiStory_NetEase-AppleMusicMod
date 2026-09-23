using System;

namespace MusicBridge;

internal static class NeteaseQualityPolicy
{
    public static bool IsLossless(NeteaseQuality q) => q == NeteaseQuality.Lossless || q == NeteaseQuality.HiRes;
    public static string Level(NeteaseQuality q) => q switch {
        NeteaseQuality.Standard => "standard", NeteaseQuality.Exhigh => "exhigh",
        NeteaseQuality.Lossless => "lossless", NeteaseQuality.HiRes => "hires",
        _ => throw new ArgumentOutOfRangeException(nameof(q)) };
    public static string Label(NeteaseQuality q) => q switch {
        NeteaseQuality.Standard => "128 kbps", NeteaseQuality.Exhigh => "320 kbps",
        NeteaseQuality.Lossless => "无损", NeteaseQuality.HiRes => "Hi-Res", _ => "未知音质" };
    public static int Rank(NeteaseQuality q) => q switch {
        NeteaseQuality.Standard => 0, NeteaseQuality.Exhigh => 1,
        NeteaseQuality.Lossless => 2, NeteaseQuality.HiRes => 3, _ => -1 };
    public static NeteaseQuality? Lower(NeteaseQuality q) => q switch {
        NeteaseQuality.HiRes => NeteaseQuality.Lossless, NeteaseQuality.Lossless => NeteaseQuality.Exhigh,
        NeteaseQuality.Exhigh => NeteaseQuality.Standard, _ => (NeteaseQuality?)null };
    public static bool CanFallback(NeteaseFailure f) => f == NeteaseFailure.Subscription ||
        f == NeteaseFailure.Rejected || f == NeteaseFailure.UnsupportedFormat;
    public static bool IsDowngraded(NeteasePlaybackSource s)
    {
        int actual = s.ReturnedLevel switch { "standard" => 0, "higher" => 0, "exhigh" => 1,
            "lossless" => 2, "hires" => 3, _ => -1 };
        if (!s.IsFlac && s.Bitrate.HasValue)
            actual = Math.Min(actual < 0 ? 1 : actual, s.Bitrate < 320000 ? 0 : 1);
        if (actual >= 0 && actual < Rank(s.RequestedQuality)) return true;
        return Rank(s.AttemptedQuality) >= 0 && Rank(s.AttemptedQuality) < Rank(s.RequestedQuality);
    }
}
