using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace MusicBridge;

internal static partial class NeteaseApi
{
#if MUSICBRIDGE_TESTS
    internal static Func<string, JObject, NeteaseAccountContext, JObject> TestTransport;
#endif
    internal static NeteaseAccountContext CaptureContext(long userId)
    {
        lock (CookieLock) return new NeteaseAccountContext(userId, _cookies, GetCookieValue("__csrf"));
    }
    internal static string BuildRequestUrl(string path, string csrf) => Origin + path +
        (string.IsNullOrEmpty(csrf) ? "" : (path.Contains("?") ? "&" : "?") + "csrf_token=" + Uri.EscapeDataString(csrf));

    private static NeteaseResult<JObject> Send(string path, JObject body, NeteaseAccountContext context, NeteaseRequestCancellation cancellation, bool write = false)
    {
        if (context == null || !context.Active) return NeteaseResult<JObject>.Fail(NeteaseFailure.Unauthorized, "请重新登录网易云");
        cancellation = cancellation ?? new NeteaseRequestCancellation();
        if (!context.Register(cancellation)) return NeteaseResult<JObject>.Fail(NeteaseFailure.Cancelled, "操作已取消");
        try
        {
            if (cancellation.IsCancelled) return NeteaseResult<JObject>.Fail(NeteaseFailure.Cancelled, "操作已取消");
            body["csrf_token"] = context.Csrf;
            var failure = NeteaseFailure.Protocol;
            JObject json;
#if MUSICBRIDGE_TESTS
            if (TestTransport != null) json = TestTransport(path, body, context);
            else
#endif
            json = Post(path, body.ToString(Newtonsoft.Json.Formatting.None), out _, context.Csrf, cancellation, context, f => failure = f);
            if (!context.Active || cancellation.IsCancelled) return NeteaseResult<JObject>.Fail(NeteaseFailure.Cancelled, "操作已取消");
            if (json == null)
            {
                if (write && (failure == NeteaseFailure.Network || failure == NeteaseFailure.Protocol)) failure = NeteaseFailure.UnknownWrite;
                return NeteaseResult<JObject>.Fail(failure, failure == NeteaseFailure.UnknownWrite ? "写入结果待确认" : "请求失败，请重试或重新登录");
            }
            int? code = json.Value<int?>("code");
            if (code != 200) return NeteaseResult<JObject>.Fail(code == 301 || code == 401 ? NeteaseFailure.Unauthorized : code == null ? (write ? NeteaseFailure.UnknownWrite : NeteaseFailure.Protocol) : NeteaseFailure.Rejected,
                code == 301 || code == 401 ? "登录已失效，请重新登录" : "服务端未确认成功（code=" + code + "）");
            return NeteaseResult<JObject>.Success(json);
        }
        catch { return NeteaseResult<JObject>.Fail(write ? NeteaseFailure.UnknownWrite : NeteaseFailure.Protocol, write ? "写入结果待确认" : "接口数据异常"); }
        finally { context.Release(cancellation); }
    }
    internal static NeteaseResult<List<long>> ParseFavorites(JObject json)
    {
        var ids = new List<long>();
        var seen = new HashSet<long>();
        if (json.Value<int?>("code") != 200 || !(json["ids"] is JArray values)) return NeteaseResult<List<long>>.Fail(NeteaseFailure.Protocol, "喜欢列表数据不完整");
        foreach (var value in values)
        {
            if (value.Type != JTokenType.Integer || !long.TryParse(value.ToString(), out long id) || id <= 0)
                return NeteaseResult<List<long>>.Fail(NeteaseFailure.Protocol, "喜欢列表数据异常");
            if (seen.Add(id)) ids.Add(id);
        }
        return NeteaseResult<List<long>>.Success(ids);
    }
    public static NeteaseResult<List<long>> RefreshFavorites(NeteaseAccountContext c, NeteaseRequestCancellation x)
    {
        var result = Send("/weapi/song/like/get", new JObject { ["uid"] = c?.UserId }, c, x);
        return result.Ok ? ParseFavorites(result.Value) : NeteaseResult<List<long>>.Fail(result.Failure, result.Message);
    }
    public static NeteaseResult<bool> SetSongLiked(long id, bool liked, NeteaseAccountContext c, NeteaseRequestCancellation x)
    {
        var result = Send("/weapi/radio/like", new JObject { ["trackId"] = id, ["like"] = liked, ["alg"] = "itembased", ["time"] = 3 }, c, x, true);
        return result.Ok ? NeteaseResult<bool>.Success(true) : NeteaseResult<bool>.Fail(result.Failure, result.Message);
    }
    public static NeteaseResult<bool> TrashFmSong(long id, NeteaseAccountContext c, NeteaseRequestCancellation x)
    {
        var result = Send("/weapi/radio/trash/add?alg=RT&songId=" + id + "&time=25", new JObject { ["songId"] = id }, c, x, true);
        return result.Ok ? NeteaseResult<bool>.Success(true) : NeteaseResult<bool>.Fail(result.Failure, result.Message);
    }
    public static NeteaseResult<List<TrackInfo>> GetPersonalFmBatch(NeteaseAccountContext c, NeteaseRequestCancellation x)
    {
        var result = Send("/weapi/v1/radio/get", new JObject(), c, x);
        if (!result.Ok) return NeteaseResult<List<TrackInfo>>.Fail(result.Failure, result.Message);
        try
        {
            if (!(result.Value["data"] is JArray data)) throw new FormatException();
            var tracks = new List<TrackInfo>();
            foreach (var item in data)
            {
                // FM uses the older song schema (artists/album/duration).
                var song = (JObject)item.DeepClone();
                song["ar"] = song["ar"] ?? song["artists"];
                song["al"] = song["al"] ?? song["album"];
                song["dt"] = song["dt"] ?? song["duration"];
                var track = ParseTrack(song);
                if (track == null || track.Id <= 0) throw new FormatException();
                tracks.Add(track);
            }
            return NeteaseResult<List<TrackInfo>>.Success(tracks);
        }
        catch { return NeteaseResult<List<TrackInfo>>.Fail(NeteaseFailure.Protocol, "私人FM数据异常"); }
    }
    public static NeteaseResult<NeteasePlaybackSource> GetPlaybackSource(long id, NeteaseQuality quality, NeteaseAccountContext c, NeteaseRequestCancellation x)
    {
        var result = Send("/weapi/song/enhance/player/url/v1", new JObject { ["ids"] = "[" + id + "]", ["level"] = quality == NeteaseQuality.Exhigh ? "exhigh" : "standard", ["encodeType"] = "mp3" }, c, x);
        return result.Ok ? ParsePlaybackSource(result.Value, id, quality, DateTime.UtcNow) : NeteaseResult<NeteasePlaybackSource>.Fail(result.Failure, result.Message);
    }
    internal static NeteaseResult<NeteasePlaybackSource> ParsePlaybackSource(JObject json, long id, NeteaseQuality quality, DateTime now)
    {
        try
        {
            if (json.Value<int?>("code") != 200 || !(json["data"] is JArray data)) throw new FormatException();
            JToken song = null;
            foreach (var item in data) if (item.Value<long?>("id") == id) { song = item; break; }
            if (song == null) throw new FormatException();
            string url = ToHttps(song.Value<string>("url"));
            if (string.IsNullOrEmpty(url))
            {
                var failure = song.Value<int?>("code") == 404 ? NeteaseFailure.Copyright : song.Value<int?>("fee") == 1 || song.Value<int?>("fee") == 4 ? NeteaseFailure.Subscription : NeteaseFailure.Rejected;
                return NeteaseResult<NeteasePlaybackSource>.Fail(failure, failure == NeteaseFailure.Copyright ? "该歌曲无版权" : failure == NeteaseFailure.Subscription ? "当前账号需要会员或购买权限" : "服务端未返回可播放地址");
            }
            if (song.Value<int?>("code") != 200 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new FormatException();
            string format = song.Value<string>("type");
            if (!string.Equals(format, "mp3", StringComparison.OrdinalIgnoreCase)) return NeteaseResult<NeteasePlaybackSource>.Fail(NeteaseFailure.UnsupportedFormat, "本版本仅支持MP3，服务端返回 " + (format ?? "未知格式"));
            var trial = song["freeTrialInfo"];
            bool? isTrial = song.PropertyExists("freeTrialInfo") ? trial != null && trial.Type != JTokenType.Null : (bool?)null;
            if (trial != null && trial.Type != JTokenType.Null && !(trial is JObject)) throw new FormatException();
            int? bitrate = song.Value<int?>("br"); if (bitrate <= 0) bitrate = null;
            long? size = song.Value<long?>("size"); if (size <= 0) size = null;
            double? expires = song.Value<double?>("expi");
            return NeteaseResult<NeteasePlaybackSource>.Success(new NeteasePlaybackSource {
                SongId = id, RequestedQuality = quality, Url = url, Format = "mp3", ReturnedLevel = song.Value<string>("level"),
                Bitrate = bitrate, SizeBytes = size, ServerMd5 = song.Value<string>("md5"),
                ExpiresAtUtc = expires.HasValue && expires > 0 ? now.AddSeconds(expires.Value) : (DateTime?)null,
                IsTrial = isTrial, TrialStartSeconds = trial is JObject ? trial.Value<double?>("start") : null, TrialEndSeconds = trial is JObject ? trial.Value<double?>("end") : null
            });
        }
        catch { return NeteaseResult<NeteasePlaybackSource>.Fail(NeteaseFailure.Protocol, "播放数据异常"); }
    }
    private static bool PropertyExists(this JToken token, string key) => token is JObject obj && obj.Property(key) != null;
}
