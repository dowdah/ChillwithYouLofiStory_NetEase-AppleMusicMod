using System;
using System.Collections.Generic;
using System.Text;
using MusicBridge;

static class Program
{
    static int assertions;
    static void Check(bool value, string message)
    { assertions++; if (!value) throw new Exception(message); }
    static void Main()
    {
        Format(); Scheduling(); Recovery(); Conflicts(); Failures();
        Console.WriteLine("Presence publisher tests passed: " + assertions);
    }
    static string Text(PlaybackState state, string title = "私密曲名", string timer = null,
        bool music = true, bool publicTitle = false) =>
        SteamPresenceFormatter.Format(new PresenceTextState(state, title, timer, music, publicTitle));
    static void Format()
    {
        Check(Text(PlaybackState.Idle) == null, "idle releases");
        Check(Text(PlaybackState.Loading) == "音乐缓冲中", "buffering is not playing");
        Check(Text(PlaybackState.Playing) == "音乐播放中", "private default");
        Check(Text(PlaybackState.Paused) == "音乐已暂停", "paused separate");
        Check(Text(PlaybackState.Failed) == "音乐播放失败", "failure separate");
        Check(Text(PlaybackState.Playing, music: false, publicTitle: true) == null, "music off also hides title");
        Check(Text(PlaybackState.Playing, publicTitle: true) == "音乐播放中：私密曲名", "title explicit opt-in");
        Check(Text(PlaybackState.Loading, publicTitle: true) == "音乐缓冲中", "no stale loading title");
        Check(Text(PlaybackState.Failed, publicTitle: true) == "音乐播放失败", "no stale failed title");
        Check(Text(PlaybackState.Playing, timer: "专注中，剩余 24 分钟") == "专注中，剩余 24 分钟 · 音乐播放中", "timer first");
        Check(Text(PlaybackState.Idle, timer: "计时完成") == "计时完成", "timer without music");
        Check(Text(PlaybackState.Paused, "a\n\r\0\t\u202e\u2028\ud800b\udc00", publicTitle: true) == "音乐已暂停：ab", "remove unsafe text");
        string longTitle = string.Concat(System.Linq.Enumerable.Repeat("中🎵😀", 100));
        string formatted = Text(PlaybackState.Playing, longTitle, "正计时 12 分钟", publicTitle: true);
        Check(Encoding.UTF8.GetByteCount(formatted) <= 255, "max bytes");
        Check(formatted.StartsWith("正计时 12 分钟 · 音乐播放中："), "title truncates first");
        Check(formatted.EndsWith("…"), "truncation marked");
        var strict = new UTF8Encoding(false, true);
        Check(strict.GetString(strict.GetBytes(formatted)) == formatted, "well formed UTF16");
        for (int i = 1; i <= 20; i++)
        {
            string clipped = SteamPresenceFormatter.Truncate("中😀中😀中😀", i);
            Check(strict.GetByteCount(clipped) <= i, "safe byte boundary " + i);
        }
    }
    static void Scheduling()
    {
        var r = new Rig(); r.P.Tick("A"); Check(r.Api.Sets.Count == 0, "default disabled");
        r.P.SetEnabled(true); r.P.Tick("A");
        Check(r.Api.Value == "A" && r.P.PublishCount == 1 && r.P.LastSuccessAt == 0, "first publish");
        r.Now = 1; r.P.Tick("A"); Check(r.Api.Sets.Count == 1, "deduplicate");
        r.P.Tick("B"); r.Now = 4; r.P.Tick("C"); Check(r.Api.Value == "A", "throttle");
        r.Now = 5; r.P.Tick("D"); Check(r.Api.Value == "D" && r.P.PublishCount == 2, "latest coalesced");
        r.P.Tick(null); Check(r.Api.Value == null && !r.P.HasOwnership, "empty releases immediately");
        r.Now = 6; r.P.Tick("E"); Check(r.Api.Value == null, "release does not reset interval");
        r.Now = 10; r.P.Tick("E"); Check(r.Api.Value == "E", "republish after release");
        r.P.SetEnabled(false); Check(r.Api.Value == null, "disable immediate");
        int count = r.Api.Sets.Count; r.Now = 100; r.P.Tick("private"); r.P.Shutdown(); r.P.Shutdown();
        Check(r.Api.Sets.Count == count, "disabled and repeated shutdown do not publish");
        r.P.SetEnabled(true); r.P.Tick("after shutdown"); Check(r.Api.Sets.Count == count, "shutdown terminal");
        Check(r.Api.Sets.TrueForAll(p => p.Key == "status"), "only status ever written");
        r = new Rig(); r.P.SetEnabled(true); r.P.Tick("\n" + new string('中', 200));
        Check(Encoding.UTF8.GetByteCount(r.Api.Value) <= 255 && !r.Api.Value.Contains("\n"), "publisher enforces boundary independently");
    }
    static void Recovery()
    {
        var r = new Rig(); r.Api.Value = "original"; r.Api.Values["other"] = "preserve";
        r.P.SetEnabled(true); r.P.Tick("A"); r.Api.Online = false; r.Now = 1; r.P.Tick("A");
        Check(!r.P.Ready && r.P.LastError != null, "disconnection diagnosed");
        r.Api.Online = true; r.Now = 6; r.P.Tick("A");
        Check(r.P.PublishCount == 2 && r.Api.Value == "A", "same text republished on reconnect");
        r.Api.Online = false; r.Now = 7; r.P.Tick("A"); r.Api.Value = null;
        r.Api.Online = true; r.Now = 12; r.P.Tick("A");
        Check(r.P.PublishCount == 3 && !r.P.Conflict, "cleared server value republished on reconnect");
        r.P.SetEnabled(false); Check(r.Api.Value == "original" && r.Api.Values["other"] == "preserve", "original and unrelated restored");
        r.P.SetEnabled(true); r.Now = 20; r.P.Tick("B"); r.Api.Online = false;
        r.P.SetEnabled(false); Check(r.P.CleanupPending, "offline disable recovery pending");
        r.Api.Online = true; r.Now = 25; r.P.Tick("should not publish");
        Check(r.Api.Value == "original" && !r.P.CleanupPending, "disabled tick cleanup only");
        r.P.SetEnabled(true); r.Now = 30; r.P.Tick("C");
        r.Api.Session = "new"; r.Api.Value = "new account original"; r.Now = 35; r.P.Tick("D");
        Check(r.Api.Value == "D", "new session publishes current desired");
        r.P.SetEnabled(false); Check(r.Api.Value == "new account original", "never restores old identity value");
        r = new Rig(); r.P.SetEnabled(true); r.P.Tick("A"); r.Api.Online = false;
        r.Now = 1; r.P.Tick("A"); r.Api.Online = true; r.P.Retry(); r.Now = 2; r.P.Tick("A");
        Check(r.P.PublishCount == 1, "early reconnect honors rate limit");
        r.Now = 5; r.P.Tick("A"); Check(r.P.PublishCount == 2, "reconnect republish survives throttled tick");
        r.Api.Online = false; r.P.Shutdown(); int writes = r.Api.Sets.Count;
        r.Api.Online = true; r.Now = 50; r.P.Tick("A"); r.P.Shutdown();
        Check(r.Api.Sets.Count == writes, "shutdown unavailable does not restart publishing");
    }
    static void Conflicts()
    {
        var r = new Rig(); r.Api.Values["steam_display"] = "#Existing"; r.P.SetEnabled(true); r.P.Tick("A");
        Check(r.P.Conflict && r.Api.Sets.Count == 0, "existing template fail closed");
        r.P.Retry(); r.P.Tick("A"); Check(r.P.Conflict && r.Api.Sets.Count == 0, "retry cannot override template");
        r.Api.Values.Remove("steam_display"); r.P.Retry(); r.P.Tick("A");
        Check(!r.P.Conflict && r.Api.Value == "A", "explicit retry rebaselines");
        r.Api.Value = "external"; r.Now = 1; r.P.Tick("A");
        Check(r.P.Conflict && r.Api.Value == "external", "same desired still detects takeover immediately");
        r.Now = 50; r.P.Tick("B"); Check(r.Api.Value == "external", "does not compete");
        r.P.Retry(); r.P.Tick("B"); r.P.SetEnabled(false);
        Check(r.Api.Value == "external", "manual retry captures new baseline");
        r.P.SetEnabled(true); r.Now = 60; r.P.Tick("C"); r.Api.Values["steam_display"] = "#Game"; r.P.Tick("C");
        Check(r.P.Conflict && r.Api.Value == "external" && r.Api.Values["steam_display"] == "#Game", "template takeover cleans owned status only");
        r.P.SetEnabled(false); r.P.SetEnabled(true); r.P.Tick("C"); Check(r.P.Conflict, "toggle does not silently clear conflict");
    }
    static void Failures()
    {
        var r = new Rig(); r.Api.Online = false; r.P.SetEnabled(true);
        foreach (double t in new[] { 0d, 5d, 20d, 50d, 110d }) { r.Now = t; r.P.Tick("A"); }
        Check(r.Api.Connects == 5, "5 15 30 60 backoff");
        r.Now = 111; r.P.Tick("B"); Check(r.Api.Connects == 5, "backoff no premature retry");
        r.Api.Online = true; r.Now = 170; r.P.Tick("latest"); Check(r.Api.Value == "latest", "latest retained across failures");
        r = new Rig(); r.Api.Value = "original"; r.Api.NextSet = Failure.FalseAfterWrite;
        r.P.SetEnabled(true); r.P.Tick("A");
        Check(r.Api.Value == "original" && !r.P.HasOwnership && r.P.PublishCount == 0, "uncertain false write rollback");
        r.Now = 4; r.P.Tick("B"); Check(r.Api.Value == "original", "failure retry waits5");
        r.Now = 5; r.P.Tick("B"); Check(r.Api.Value == "B", "retry after false write");
        r.Now = 10; r.Api.NextSet = Failure.ThrowBeforeWrite; r.P.Tick("C");
        Check(r.Api.Value == "original" && !r.P.HasOwnership, "failed update restores previous owned value");
        Check(!r.P.LastError.Contains("SECRET"), "raw exception text not exposed");
        r = new Rig(); r.Api.Value = "original"; r.P.SetEnabled(true); r.P.Tick("A");
        r.Api.NextSet = Failure.FalseAfterWrite; r.P.SetEnabled(false);
        Check(r.P.CleanupPending && r.Api.Value == "original", "uncertain restoration pending");
        int n = r.Api.Sets.Count; r.Now = 5; r.P.Tick(null);
        Check(!r.P.CleanupPending && r.Api.Sets.Count == n, "already restored detected without duplicate write");
        r = new Rig(); r.P.SetEnabled(true); r.P.Tick("A"); r.Api.NextSet = Failure.ExternalAfterWrite;
        r.Now = 5; r.P.Tick("B"); Check(r.P.Conflict && r.Api.Value == "external", "readback takeover preserved");
        r = new Rig(); r.Api.ThrowReads = true; r.P.SetEnabled(true); r.P.Tick("A");
        Check(r.P.PublishCount == 0 && r.Api.Sets.Count == 0, "read failure never writes");
        r.Api.ThrowReads = false; r.Now = 5; r.P.Tick("B"); Check(r.Api.Value == "B", "recover from read failure");
    }
    sealed class Rig
    {
        public double Now;
        public readonly Fake Api = new Fake();
        public readonly SteamPresencePublisher P;
        public Rig() { P = new SteamPresencePublisher(Api, () => Now); }
    }
    enum Failure { None, FalseAfterWrite, ThrowBeforeWrite, ExternalAfterWrite }
    sealed class Fake : ISteamPresenceApi
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        public readonly List<KeyValuePair<string, string>> Sets = new List<KeyValuePair<string, string>>();
        public bool Online = true;
        public string Session = "session";
        public int Connects;
        public Failure NextSet;
        public bool ThrowReads;
        public string Value { get => Values.TryGetValue("status", out string v) ? v : null;
            set { if (value == null) Values.Remove("status"); else Values["status"] = value; } }
        public bool TryConnect(out string session, out string error)
        { Connects++; session = Session; error = Online ? null : "Steam 未就绪"; return Online; }
        public Dictionary<string, string> ReadOwnPresence()
        { if (ThrowReads) throw new Exception("SECRET read details"); return new Dictionary<string, string>(Values); }
        public bool Set(string key, string value)
        {
            Check(key == "status", "fake rejects unrelated keys");
            Sets.Add(new KeyValuePair<string, string>(key, value));
            var failure = NextSet; NextSet = Failure.None;
            if (failure == Failure.ThrowBeforeWrite) throw new Exception("SECRET private song path");
            Value = failure == Failure.ExternalAfterWrite ? "external" : value;
            return failure != Failure.FalseAfterWrite;
        }
    }
}
