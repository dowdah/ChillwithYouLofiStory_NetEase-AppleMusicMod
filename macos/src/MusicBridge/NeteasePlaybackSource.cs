using System;
using System.Collections.Generic;
using System.Net;

namespace MusicBridge;

internal enum NeteaseQuality { Standard = 128, Exhigh = 320, Lossless = 1000, HiRes = 2000 }
internal enum NeteaseFailure { None, Network, Cancelled, Unauthorized, Rejected, Copyright, Subscription, Protocol, UnknownWrite, UnsupportedFormat }

internal sealed class NeteaseResult<T>
{
    public T Value;
    public NeteaseFailure Failure;
    public string Message;
    public bool Ok => Failure == NeteaseFailure.None;
    public static NeteaseResult<T> Success(T value) => new NeteaseResult<T> { Value = value };
    public static NeteaseResult<T> Fail(NeteaseFailure failure, string message) => new NeteaseResult<T> { Failure = failure, Message = message };
}

// A context never changes identity or cookie containers. Invalidating it aborts its requests.
internal sealed class NeteaseAccountContext
{
    public readonly long UserId;
    public readonly CookieContainer Cookies;
    public readonly string Csrf;
    private readonly object _gate = new object();
    private readonly HashSet<NeteaseRequestCancellation> _requests = new HashSet<NeteaseRequestCancellation>();
    public volatile bool Active = true;
    public NeteaseAccountContext(long userId, CookieContainer cookies, string csrf)
    { UserId = userId; Cookies = cookies; Csrf = csrf ?? ""; }
    public bool Register(NeteaseRequestCancellation cancellation)
    {
        lock (_gate) { if (!Active) { cancellation.Cancel(); return false; } _requests.Add(cancellation); return true; }
    }
    public void Release(NeteaseRequestCancellation cancellation) { lock (_gate) _requests.Remove(cancellation); }
    public void Invalidate()
    {
        lock (_gate) { Active = false; foreach (var request in _requests) request.Cancel(); _requests.Clear(); }
    }
}

internal sealed class NeteasePlaybackSource
{
    public long SongId;
    public NeteaseQuality RequestedQuality;
    public string Url, ReturnedLevel, Format, ServerMd5;
    public int? Bitrate;
    public long? SizeBytes;
    public DateTime? ExpiresAtUtc;
    public bool? IsTrial;
    public double? TrialStartSeconds, TrialEndSeconds;
    public NeteaseQuality AttemptedQuality;
    public int? SampleRate, BitsPerSample, Channels;
    public long? PcmFrames;
    public string FallbackReason;
    public bool WasPrefetched;
    public double UrlLookupSeconds;
    public bool IsFlac => string.Equals(Format, "flac", StringComparison.OrdinalIgnoreCase);
    public string QualityLabel => (Format ?? "格式未知").ToUpperInvariant() + " · " +
        (Bitrate.HasValue ? (Bitrate.Value / 1000) + " kbps" : "码率未知") +
        (SampleRate.HasValue ? " · " + (SampleRate.Value / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " kHz" : "") +
        (BitsPerSample.HasValue ? " / " + BitsPerSample + "-bit" : "") +
        (Channels.HasValue ? " · " + Channels + " 声道" : "") +
        (NeteaseQualityPolicy.IsDowngraded(this) ? "｜首选 " + NeteaseQualityPolicy.Label(RequestedQuality) + "，已降级" : "") +
        (!string.IsNullOrEmpty(FallbackReason) ? "（" + FallbackReason + "）" : "") +
        (IsTrial == true ? " · 试听" : IsTrial == null ? " · 试听状态未知" : "");

}

internal interface INeteaseClient
{
    NeteaseResult<List<long>> Favorites(NeteaseAccountContext context, NeteaseRequestCancellation cancellation);
    NeteaseResult<bool> SetLiked(long id, bool liked, NeteaseAccountContext context, NeteaseRequestCancellation cancellation);
    NeteaseResult<List<TrackInfo>> Fm(NeteaseAccountContext context, NeteaseRequestCancellation cancellation);
    NeteaseResult<bool> Trash(long id, NeteaseAccountContext context, NeteaseRequestCancellation cancellation);
}

internal sealed class NeteaseClient : INeteaseClient
{
    public NeteaseResult<List<long>> Favorites(NeteaseAccountContext c, NeteaseRequestCancellation x) => NeteaseApi.RefreshFavorites(c, x);
    public NeteaseResult<bool> SetLiked(long id, bool liked, NeteaseAccountContext c, NeteaseRequestCancellation x) => NeteaseApi.SetSongLiked(id, liked, c, x);
    public NeteaseResult<List<TrackInfo>> Fm(NeteaseAccountContext c, NeteaseRequestCancellation x) => NeteaseApi.GetPersonalFmBatch(c, x);
    public NeteaseResult<bool> Trash(long id, NeteaseAccountContext c, NeteaseRequestCancellation x) => NeteaseApi.TrashFmSong(id, c, x);
}
