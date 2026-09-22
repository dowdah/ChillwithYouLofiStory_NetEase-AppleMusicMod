using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using MusicBridge;
using Newtonsoft.Json.Linq;

internal static class NeteaseEnhancementTests
{
    private static int _count;
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); _count++; Console.WriteLine("PASS: " + name); }
    private static NeteaseAccountContext Account(long id = 11) => new NeteaseAccountContext(id, new CookieContainer(), "test-csrf");
    private sealed class Scheduler
    {
        public readonly List<Action> Work = new List<Action>();
        public readonly Queue<Action> Ui = new Queue<Action>();
        public DateTime Now = new DateTime(2026, 9, 22, 0, 0, 0, DateTimeKind.Utc);
        public void Worker(int index = 0) { var a = Work[index]; Work.RemoveAt(index); a(); }
        public void Commit() { int n = 0; while (Ui.Count > 0) { if (++n > 100) throw new Exception("unbounded UI work"); Ui.Dequeue()(); } }
        public void Flush() { int n = 0; while (Work.Count > 0 || Ui.Count > 0) { if (++n > 100) throw new Exception("unbounded work"); if (Work.Count > 0) Worker(); Commit(); } }
    }
    private sealed class Fake : INeteaseClient
    {
        public HashSet<long> Cloud = new HashSet<long>();
        public readonly List<Tuple<long, bool, long>> Writes = new List<Tuple<long, bool, long>>();
        public int Reads, Batches, Trashes;
        public Func<NeteaseResult<List<long>>> Read;
        public Func<long, bool, NeteaseResult<bool>> Write;
        public Func<NeteaseResult<List<TrackInfo>>> Batch;
        public Func<NeteaseResult<bool>> TrashResult;
        public NeteaseResult<List<long>> Favorites(NeteaseAccountContext c, NeteaseRequestCancellation x) { Reads++; return Read != null ? Read() : NeteaseResult<List<long>>.Success(Cloud.ToList()); }
        public NeteaseResult<bool> SetLiked(long id, bool liked, NeteaseAccountContext c, NeteaseRequestCancellation x)
        {
            if (!c.Active) return NeteaseResult<bool>.Fail(NeteaseFailure.Cancelled, "cancelled");
            Writes.Add(Tuple.Create(id, liked, c.UserId));
            if (Write != null) return Write(id, liked);
            if (liked) Cloud.Add(id); else Cloud.Remove(id);
            return NeteaseResult<bool>.Success(true);
        }
        public NeteaseResult<List<TrackInfo>> Fm(NeteaseAccountContext c, NeteaseRequestCancellation x)
        { Batches++; return Batch != null ? Batch() : NeteaseResult<List<TrackInfo>>.Success(Enumerable.Range(Batches * 3, 3).Select(id => new TrackInfo { Id = id, Name = "曲目" + id }).ToList()); }
        public NeteaseResult<bool> Trash(long id, NeteaseAccountContext c, NeteaseRequestCancellation x) { Trashes++; return TrashResult != null ? TrashResult() : NeteaseResult<bool>.Success(true); }
    }
    private static void FavoritesTests()
    {
        var api = new Fake(); var s = new Scheduler(); var c = Account();
        var f = new NeteaseFavorites(api, s.Work.Add, s.Ui.Enqueue, () => s.Now); f.Bind(c); s.Flush();
        Check(f.Known && f.Ids.Count == 0, "valid empty snapshot is known");
        f.SetLiked(1, true); f.SetLiked(1, false);
        Check(s.Work.Count == 1 && f.Pending(1) && !f.Target(1), "rapid favorite intents serialized and merged");
        s.Flush(); Check(api.Writes.Count == 2 && !f.IsLiked(1) && !api.Cloud.Contains(1), "last favorite intent wins");
        api.Cloud.Add(2); f.Refresh(); s.Flush(); Check(f.IsLiked(2), "manual refresh imports other client changes");
        api.Read = () => NeteaseResult<List<long>>.Fail(NeteaseFailure.Network, "offline"); f.Refresh(); s.Flush();
        Check(f.IsLiked(2) && f.Error != null, "failed read preserves confirmed snapshot"); api.Read = null;
        f.Refresh(); s.Worker(); f.SetLiked(3, true); s.Worker(); s.Commit();
        Check(f.IsLiked(3), "old snapshot cannot overwrite new write");
        f.SetLiked(4, true); f.Refresh(); s.Worker(1); s.Commit(); s.Flush();
        Check(f.IsLiked(4), "snapshot during pending write cannot roll it back");
        api.Write = (id, liked) => { api.Cloud.Add(id); return NeteaseResult<bool>.Fail(NeteaseFailure.UnknownWrite, "timeout"); };
        int writes = api.Writes.Count; f.SetLiked(5, true); s.Flush();
        Check(f.IsLiked(5) && api.Writes.Count == writes + 1, "timeout readback prevents duplicate write");
        api.Write = (id, liked) => NeteaseResult<bool>.Fail(NeteaseFailure.UnknownWrite, "timeout");
        writes = api.Writes.Count; f.SetLiked(6, true); s.Flush();
        Check(!f.IsLiked(6) && f.SongError(6) != null && api.Writes.Count == writes + 2, "unknown write retries at most once");
        api.Write = (id, liked) => NeteaseResult<bool>.Fail(NeteaseFailure.Rejected, "refused");
        f.SetLiked(7, true); s.Flush(); Check(!f.IsLiked(7) && !f.Pending(7), "rejected write restores confirmed state");
        api.Write = null; f.SetLiked(8, true); c.Invalidate(); f.Bind(Account(22)); s.Flush();
        Check(!api.Writes.Any(w => w.Item1 == 8) && !f.IsLiked(8), "queued A write cancelled before B login");
        int reads = api.Reads; s.Now = s.Now.AddMinutes(6); f.Tick(false, true, TimeSpan.FromMinutes(5));
        Check(s.Work.Count == 0, "hidden panel never polls"); f.Tick(true, true, TimeSpan.FromMinutes(5)); f.Tick(true, true, TimeSpan.FromMinutes(5)); s.Flush();
        Check(api.Reads == reads + 1, "visible refresh is single flight");
        reads = api.Reads; f.Tick(true, true, TimeSpan.FromMinutes(5)); s.Flush(); Check(api.Reads == reads, "reopen respects refresh throttle");
    }
    private static void FmTests()
    {
        var api = new Fake(); var s = new Scheduler(); var c = Account();
        var fm = new NeteasePersonalFm(api, s.Work.Add, s.Ui.Enqueue, () => s.Now);
        var played = new List<long>(); fm.Play += t => played.Add(t.Id);
        fm.Start(c); for (int i = 0; i < 20; i++) fm.Next();
        Check(s.Work.Count == 1 && fm.Waiting, "FM next spam coalesces while waiting");
        s.Flush(); Check(fm.Current != null && !fm.CanPrevious, "FM first previous disabled");
        for (int i = 0; i < 10; i++) { fm.Next(); s.Flush(); }
        Check(api.Batches >= 3 && played.Distinct().Count() == played.Count, "FM crosses three batches without looping");
        Check(api.Trashes == 0 && api.Writes.Count == 0, "next never sends dislike or trash");
        long current = fm.Current.Id; fm.Previous(); Check(fm.Current.Id != current, "previous uses retained history");
        fm.Next(); Check(fm.Current.Id == current, "next resumes forward history");
        for (int i = 0; i < 110; i++) { fm.Next(); s.Flush(); }
        Check(fm.HistoryCount <= 20 && fm.PendingCount <= 30, "FM memory is bounded");
        api.TrashResult = () => NeteaseResult<bool>.Fail(NeteaseFailure.UnknownWrite, "timeout"); current = fm.Current.Id;
        fm.TrashCurrent(current); s.Flush(); Check(fm.Current.Id == current && fm.TrashError != null && api.Trashes == 1, "trash timeout does not advance or retry");
        api.TrashResult = null; fm.TrashCurrent(current); fm.Next(); long newer = fm.Current.Id; s.Flush();
        Check(fm.Current.Id == newer && api.Trashes == 2, "late trash cannot skip newer song");
        current = fm.Current.Id; fm.TrashCurrent(current); s.Flush(); Check(fm.Current.Id != current, "confirmed trash advances own track");
        fm.End(); api.Batch = () => NeteaseResult<List<TrackInfo>>.Success(new List<TrackInfo>()); int batches = api.Batches;
        fm.Start(c); s.Flush(); s.Now = s.Now.AddSeconds(2); fm.Tick(true); s.Flush(); s.Now = s.Now.AddSeconds(5); fm.Tick(true); s.Flush();
        for (int i = 0; i < 20; i++) { s.Now = s.Now.AddMinutes(1); fm.Tick(true); s.Flush(); }
        Check(api.Batches == batches + 3 && fm.HasError, "empty FM retries twice then stops");
        fm.Retry(); Check(s.Work.Count == 1, "explicit FM retry available"); fm.End(); s.Flush();
        api.Batch = null; fm.Start(c); fm.Suspend(); s.Flush(); Check(fm.Current == null && fm.Suspended, "late FM batch after source switch discarded");
        fm.Resume(); s.Flush(); Check(fm.Current != null && !fm.Suspended, "explicit FM resume fetches again");
        fm.End(); fm.Start(c); fm.Tick(false); int before = played.Count; s.Flush();
        Check(played.Count == before, "recovery freezes FM batch auto advance");
        fm.Tick(true); s.Flush(); Check(played.Count == before + 1, "FM selection resumes after recovery");
        fm.PlaybackFailed("output error", false); Check(fm.HasError && played.Count == before + 1, "output failure never skips tracks");
        fm.End(); fm.Start(c); s.Flush(); for (int i = 0; i < 5; i++) { fm.PlaybackFailed("unplayable", true); s.Flush(); }
        Check(fm.HasError, "five unplayable tracks stop FM");
        fm.End();
        api.Batch = () => NeteaseResult<List<TrackInfo>>.Success(new List<TrackInfo> { new TrackInfo { Id = 900, Name = "duplicate" } });
        fm.Start(c); s.Flush(); s.Now = s.Now.AddSeconds(2); fm.Tick(true); s.Flush(); s.Now = s.Now.AddSeconds(5); fm.Tick(true); s.Flush();
        Check(fm.HasError && fm.Current.Id == 900 && fm.PendingCount == 0, "all-duplicate FM batches stop after bounded retries");
        fm.End(); api.Batch = () => NeteaseResult<List<TrackInfo>>.Success(Enumerable.Range(1000, 100).Select(id => new TrackInfo { Id = id }).ToList());
        fm.Start(c); s.Flush(); Check(fm.PendingCount == 29, "oversized FM batch capped before enqueue");
        fm.End(); api.Batch = () => NeteaseResult<List<TrackInfo>>.Fail(NeteaseFailure.Unauthorized, "login expired");
        batches = api.Batches; fm.Start(c); s.Flush(); s.Now = s.Now.AddHours(1); fm.Tick(true); s.Flush();
        Check(fm.HasError && api.Batches == batches + 1, "unauthorized FM does not retry automatically");
        fm.End(); api.Batch = null; fm.Start(c); c.Invalidate(); s.Flush(); Check(fm.Current == null, "invalid account FM result discarded");
    }
    private static void ApiTests()
    {
        Check(!NeteaseApi.ParseFavorites(JObject.Parse("{code:200}")).Ok, "missing ids is not empty favorites");
        Check(!NeteaseApi.ParseFavorites(JObject.Parse("{code:200,ids:[1,'bad']}")).Ok, "malformed ids rejects snapshot");
        Check(NeteaseApi.ParseFavorites(JObject.Parse("{code:200,ids:[]}")).Ok, "explicit empty ids accepted");
        Check(NeteaseApi.BuildRequestUrl("/weapi/radio/trash/add?alg=RT", "a+b").EndsWith("?alg=RT&csrf_token=a%2Bb"), "CSRF preserves existing query");
        var c = Account(); string path = null; JObject payload = null;
        NeteaseApi.TestTransport = (p, body, account) => { path = p; payload = body; Check(ReferenceEquals(c, account), "captured account reaches transport"); return JObject.Parse("{code:200}"); };
        Check(NeteaseApi.SetSongLiked(1, false, c, null).Ok && path == "/weapi/radio/like" && payload.Value<bool>("like") == false, "unlike uses WEAPI and explicit false");
        Check(NeteaseApi.TrashFmSong(2, c, null).Ok && path.Contains("/weapi/radio/trash/add?") && payload.Value<long>("songId") == 2, "trash uses distinct endpoint");
        NeteaseApi.TestTransport = (p, body, account) => JObject.Parse("{code:200,data:[{id:1,name:'FM',artists:[{name:'Artist'}],album:{name:'Album'},duration:200000}]}");
        var batch = NeteaseApi.GetPersonalFmBatch(c, null); Check(batch.Ok && batch.Value[0].Artists == "Artist" && batch.Value[0].DurationMs == 200000, "FM legacy metadata normalized");
        var response = JObject.Parse("{code:200,data:[{id:1,code:200,url:'https://example.invalid/audio',type:'mp3',br:128000,size:12,level:'standard',expi:60,freeTrialInfo:null}]}");
        NeteaseApi.TestTransport = (p, body, account) => { path = p; payload = body; return response; };
        var source = NeteaseApi.GetPlaybackSource(1, NeteaseQuality.Exhigh, c, null);
        Check(source.Ok && path.EndsWith("/url/v1") && payload.Value<string>("level") == "exhigh" && payload.Value<string>("encodeType") == "mp3", "320 request explicitly uses exhigh MP3");
        Check(source.Value.Bitrate == 128000 && source.Value.QualityLabel.Contains("已降级") && source.Value.IsTrial == false, "actual fallback bitrate displayed");
        ((JObject)response["data"][0]).Remove("br"); ((JObject)response["data"][0]).Remove("freeTrialInfo");
        source = NeteaseApi.ParsePlaybackSource(response, 1, NeteaseQuality.Standard, DateTime.UtcNow);
        Check(source.Ok && source.Value.Bitrate == null && source.Value.IsTrial == null, "unknown source fields remain unknown");
        response["data"][0]["freeTrialInfo"] = JObject.Parse("{start:30,end:60}");
        source = NeteaseApi.ParsePlaybackSource(response, 1, NeteaseQuality.Standard, DateTime.UtcNow);
        Check(source.Ok && source.Value.IsTrial == true && source.Value.TrialStartSeconds == 30, "trial range preserved");
        response["data"][0]["type"] = "flac";
        Check(NeteaseApi.ParsePlaybackSource(response, 1, NeteaseQuality.Standard, DateTime.UtcNow).Failure == NeteaseFailure.UnsupportedFormat, "FLAC rejected by MPEG-only release");
        response["data"][0]["url"] = null; response["data"][0]["code"] = 404;
        Check(NeteaseApi.ParsePlaybackSource(response, 1, NeteaseQuality.Standard, DateTime.UtcNow).Failure == NeteaseFailure.Copyright, "copyright failure distinguished");
        response["data"][0]["code"] = -1; response["data"][0]["fee"] = 1;
        Check(NeteaseApi.ParsePlaybackSource(response, 1, NeteaseQuality.Standard, DateTime.UtcNow).Failure == NeteaseFailure.Subscription, "subscription failure distinguished");
        NeteaseApi.TestTransport = (p, body, account) => JObject.Parse("{code:301}");
        Check(NeteaseApi.SetSongLiked(1, true, c, null).Failure == NeteaseFailure.Unauthorized, "business failure never write success");
        NeteaseApi.TestTransport = (p, body, account) => throw new IOException();
        Check(NeteaseApi.SetSongLiked(1, true, c, null).Failure == NeteaseFailure.UnknownWrite, "uncertain transport write classified");
        c.Invalidate(); bool sent = false; NeteaseApi.TestTransport = (p, body, account) => { sent = true; return null; };
        NeteaseApi.SetSongLiked(1, true, c, null); Check(!sent, "invalid context cannot reach transport");
        var cancelled = new NeteaseRequestCancellation(); cancelled.Cancel();
        Check(NeteaseApi.SetSongLiked(1, true, Account(), cancelled).Failure == NeteaseFailure.Cancelled && !sent, "pre-cancelled write never reaches transport");
        NeteaseApi.TestTransport = null;
    }
    private static void CacheTests()
    {
        var root = AudioDiskCache.Root; if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root); File.WriteAllBytes(Path.Combine(root, "1.mp3"), new byte[] { 1, 2 });
        var source = new NeteasePlaybackSource { SongId = 1, RequestedQuality = NeteaseQuality.Standard, Bitrate = 128000, Format = "mp3", SizeBytes = 8, IsTrial = false };
        byte[] bytes = { 255, 251, 144, 0, 0, 0, 0, 0 };
        Check(AudioDiskCache.TryGet(11, source) == null, "legacy cache never v2 hit"); AudioDiskCache.Store(11, source, bytes, () => true);
        using (var lease = AudioDiskCache.TryGet(11, source)) Check(lease != null, "v2 standard cache hit");
        Check(AudioDiskCache.TryGet(22, source) == null, "cache isolated by account"); source.RequestedQuality = NeteaseQuality.Exhigh;
        Check(AudioDiskCache.TryGet(11, source) == null, "128 never impersonates 320 request"); AudioDiskCache.Store(11, source, bytes, () => true);
        using (var lease = AudioDiskCache.TryGet(11, source)) Check(lease != null, "320 fallback has distinct index"); source.Bitrate = 320000;
        Check(AudioDiskCache.TryGet(11, source) == null, "fresh 320 cannot use 128 fallback"); source.Bitrate = 128000; source.IsTrial = true;
        Check(AudioDiskCache.TryGet(11, source) == null, "trial and full indices isolated"); source.IsTrial = false;
        using (var lease = AudioDiskCache.TryGet(11, source)) File.WriteAllBytes(new Uri(lease.Uri).LocalPath, new byte[] { 255, 251, 144, 0, 1, 2, 3, 4 });
        Check(AudioDiskCache.TryGet(11, source) == null, "same-size corrupt cache rejected");
        source.SongId = 2; AudioDiskCache.Store(11, source, bytes, () => false); Check(AudioDiskCache.TryGet(11, source) == null, "cancelled download never commits");
        AudioDiskCache.Store(11, source, new byte[8], () => true); Check(AudioDiskCache.TryGet(11, source) == null, "non-MP3 cannot commit");
        source.SizeBytes = 9; AudioDiskCache.Store(11, source, bytes, () => true); Check(AudioDiskCache.TryGet(11, source) == null, "incomplete download cannot commit"); source.SizeBytes = 8;
        var blocked = Path.Combine(root, "v2", "netease", "33"); File.WriteAllText(blocked, "blocked");
        AudioDiskCache.Store(33, source, bytes, () => true); Check(AudioDiskCache.TryGet(33, source) == null, "disk write failure contained"); File.Delete(blocked);
        Check(File.Exists(Path.Combine(root, "1.mp3")), "upgrade preserves old cache below capacity");
        int calls = 0; source.SongId = 3; AudioDiskCache.Store(11, source, bytes, () => calls++ == 0);
        Check(AudioDiskCache.TryGet(11, source) == null && !Directory.GetFiles(root, "*.tmp.*", SearchOption.AllDirectories).Any(), "cancel between data and index commit leaves no hit or temp");
        source.SongId = 4; AudioDiskCache.Store(11, source, bytes, () => true);
        var first = AudioDiskCache.TryGet(11, source); var second = AudioDiskCache.TryGet(11, source); string pinned = new Uri(second.Uri).LocalPath;
        first.Dispose(); long capacity = MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes;
        MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes = 1;
        source.SongId = 5; AudioDiskCache.Store(11, source, bytes, () => true);
        Check(File.Exists(pinned), "LRU respects remaining lease after another lease released");
        second.Dispose(); source.SongId = 6; AudioDiskCache.Store(11, source, bytes, () => true);
        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length) <= 1, "LRU includes audio and indices across namespaces");
        MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes = capacity;
        Directory.Delete(root, true);
    }
    private static void ConfigTests()
    {
        string path = BridgePaths.Resolve("config", "musicbridge.options.json"); Directory.CreateDirectory(Path.GetDirectoryName(path));
        string old = "{\"SchemaVersion\":1,\"Netease\":{\"RepeatQueue\":false},\"Shared\":{\"HttpTimeout\":\"00:00:25\"}}";
        File.WriteAllText(path, old); MusicBridgeOptions.Load();
        Check(MusicBridgeOptions.Current.Netease.PreferredQuality == NeteaseQuality.Standard && MusicBridgeOptions.Current.Netease.AutoRefreshFavorites, "old options use new defaults");
        Check(MusicBridgeOptions.SaveQuality(NeteaseQuality.Exhigh, out _) && !MusicBridgeOptions.Current.Netease.RepeatQueue && MusicBridgeOptions.Current.Shared.HttpTimeout == TimeSpan.FromSeconds(25), "save preserves unrelated fields");
        Check(File.ReadAllText(path + ".before-netease-v1") == old, "rollback config backup retained");
        Check(!MusicBridgeOptions.SaveQuality((NeteaseQuality)999, out _) && MusicBridgeOptions.Current.Netease.PreferredQuality == NeteaseQuality.Exhigh, "invalid setting never changes current value");
        File.WriteAllText(path, "invalid"); Check(!MusicBridgeOptions.SaveQuality(NeteaseQuality.Standard, out _) && File.ReadAllText(path) == "invalid", "invalid config not overwritten");
        File.Delete(path); Directory.CreateDirectory(path);
        Check(!MusicBridgeOptions.SaveQuality(NeteaseQuality.Standard, out _) && MusicBridgeOptions.Current.Netease.PreferredQuality == NeteaseQuality.Exhigh, "atomic save failure retains current quality");
        Directory.Delete(path); File.Delete(path + ".before-netease-v1"); MusicBridgeOptions.Load();
    }
    public static void Run() { ApiTests(); FavoritesTests(); FmTests(); CacheTests(); ConfigTests(); Console.WriteLine("NETEASE ENHANCEMENT ASSERTIONS: " + _count); }
}
