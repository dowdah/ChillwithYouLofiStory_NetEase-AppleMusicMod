using System;
using System.Collections.Generic;
using MusicBridge;

internal static class Program
{
    private const string SecretTitle = "PRIVATE_TRACK_测试";
    private static int _count;
    private static string Status => SteamPresenceApi.Values.TryGetValue("status", out var value) ? value : "";
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL: " + label); _count++; Console.WriteLine("PASS: " + label); }
    private static void Tick(double seconds)
    { UnityEngine.Time.Now = seconds; SteamPresenceService.Tick(); }
    private static TimerSnapshot Running(long epoch = 1) => new TimerSnapshot(true, PresenceTimerMode.Pomodoro,
        PresenceTimerState.Running, PresenceTimerPhase.Work, 60, 1440, epoch);

    private static void Main()
    {
        PlaybackSnapshotService.Snapshot = new PlaybackSnapshot { State = PlaybackState.Playing, Title = SecretTitle };
        TimerSnapshotService.Snapshot = Running();
        // All external boundaries are fakes; the service, scheduler, formatter and publisher are production code.
        SteamPresenceSettingsStore.ThrowRead = true;
        TimerSnapshotService.ThrowInitialize = true;
        SteamPresenceService.Initialize(new HarmonyLib.Harmony());
        Check(!SteamPresenceService.Enabled, "configuration initialization exception disables module without escaping");
        Check(SteamPresenceService.Diagnostics.Contains("计时器初始化失败"), "timer initialization exception isolated");
        Check(!SteamPresenceService.Diagnostics.Contains("PRIVATE_EXCEPTION"), "initialization exception text redacted");
        int init = TimerSnapshotService.InitializeCalls;
        SteamPresenceService.Initialize(new HarmonyLib.Harmony());
        Check(TimerSnapshotService.InitializeCalls == init, "initialize idempotent");
        Tick(0);
        Check(SteamPresenceApi.WriteCalls == 0, "defaults do not publish on tick without settings UI");
        Check(PlaybackSnapshotService.CaptureCalls == 1 && TimerSnapshotService.CaptureCalls == 1,
            "sampling continues without any settings or player UI");
        Tick(0.5);
        Check(PlaybackSnapshotService.CaptureCalls == 1, "wrapper throttles local sampling to one second");
        var options = new SteamPresenceOptions { Enabled = true };
        SteamPresenceService.Apply(options); Tick(1);
        Check(Status.StartsWith("音乐播放中 · 番茄钟") && Status.Contains("剩余 24 分钟"), "actual snapshots publish music before timer");
        Check(!Status.Contains(SecretTitle), "private title default omitted from external status");
        string diagnostics = SteamPresenceService.Diagnostics;
        Check(diagnostics.Contains("发布次数：1") && diagnostics.Contains("API 已提交") && !diagnostics.Contains(SecretTitle),
            "diagnostics counts API submission but never includes title");
        var draft = options.Clone(); draft.ShareTrackTitle = true;
        Check(SteamPresenceService.Preview(draft).Contains(SecretTitle), "local-only draft preview can show opted-in title");
        Check(!Status.Contains(SecretTitle) && SteamPresenceApi.WriteCalls == 1, "preview does not apply or publish draft");
        options.Enabled = false; // Caller mutation must not change service copy.
        Check(SteamPresenceService.Enabled, "applied options cloned from caller");

        PlaybackSnapshotService.ThrowCapture = true; Tick(6);
        Check(Status.Contains("番茄钟") && !Status.Contains("音乐播放中"), "music exception clears old music without stopping timer publish");
        Check(!SteamPresenceService.Diagnostics.Contains("PRIVATE_EXCEPTION"), "capture exception message excluded from diagnostics");
        PlaybackSnapshotService.ThrowCapture = false;
        TimerSnapshotService.ThrowCapture = true; Tick(11);
        Check(Status == "音乐播放中", "timer exception clears old timer without stopping music publish");
        TimerSnapshotService.ThrowCapture = false; Tick(16);
        Check(Status.Contains("番茄钟"), "timer recovers on later samples");
        TimerSnapshotService.Snapshot = default; Tick(21);
        Check(Status == "音乐播放中", "scene invalidation removes stale timer and completion text");
        TimerSnapshotService.Snapshot = new TimerSnapshot(true, PresenceTimerMode.Countup, PresenceTimerState.Paused,
            PresenceTimerPhase.None, 189, null, 2); Tick(26);
        Check(Status.Contains("正计时（已暂停）") && Status.Contains("已过 3 分钟") && !Status.Contains("计时完成"),
            "new scene epoch rebind uses new timer without old completion");
        TimerSnapshotService.Snapshot = new TimerSnapshot(true, PresenceTimerMode.Countup, PresenceTimerState.Paused,
            PresenceTimerPhase.Break, 10.75, null, 2); Tick(27);
        Check(SteamPresenceService.Diagnostics.Contains("已过秒：10.750"),
            "timer diagnostics retain fractional seconds instead of rounding to misleading whole second");

        SteamPresenceApi.Connected = false;
        SteamPresenceService.Apply(new SteamPresenceOptions());
        Check(!SteamPresenceService.Enabled && Status.Length > 0, "offline disable does not claim immediate successful cleanup");
        SteamPresenceApi.Connected = true; Tick(32); // Five seconds after the extra diagnostic sample at 27.
        Check(Status == "", "disabled ticks retry and complete conditional cleanup after reconnection");
        int writes = SteamPresenceApi.WriteCalls;
        Tick(36); Check(SteamPresenceApi.WriteCalls == writes, "disabled feature never resumes publishing after cleanup");

        options = new SteamPresenceOptions { Enabled = true, ShareTrackTitle = true, ShowTimer = false };
        SteamPresenceService.Apply(options); Tick(41);
        Check(Status.Contains(SecretTitle) && !Status.Contains("正计时"), "saved title opt-in and timer visibility applied independently");
        Check(!SteamPresenceService.Diagnostics.Contains(SecretTitle), "diagnostics stays title-free even when publishing titles");
        SteamPresenceApi.ThrowSet = true;
        PlaybackSnapshotService.Snapshot = new PlaybackSnapshot { State = PlaybackState.Paused, Title = SecretTitle };
        Tick(46);
        Check(!SteamPresenceService.Diagnostics.Contains("PRIVATE_EXCEPTION"), "Steam write error remains isolated and redacted");
        SteamPresenceApi.ThrowSet = false; SteamPresenceService.Retry(); Tick(51);
        Check(Status.Contains("音乐已暂停"), "manual retry recovers isolated publish failure");
        UnityEngine.Time.ThrowRead = true;
        SteamPresenceService.Tick();
        Check(SteamPresenceService.Diagnostics.Contains("状态模块异常"), "unexpected module sampling exception cannot interrupt dispatcher");
        SteamPresenceService.Apply(new SteamPresenceOptions());
        Check(!SteamPresenceService.Enabled && SteamPresenceService.Diagnostics.Contains("应用设置失败"),
            "disable isolates clock exceptions and retains disabled intent");
        UnityEngine.Time.ThrowRead = false;
        Tick(56);
        Check(Status == "", "disabled cleanup recovers after unexpected clock exception");
        SteamPresenceService.Apply(options); Tick(61);
        SteamPresenceService.Shutdown();
        Check(Status == "" && TimerSnapshotService.ShutdownCalls == 1, "shutdown restores owned status and releases timer service");
        writes = SteamPresenceApi.WriteCalls;
        int captures = PlaybackSnapshotService.CaptureCalls;
        SteamPresenceService.Apply(new SteamPresenceOptions { Enabled = true });
        SteamPresenceService.Retry(); Tick(100); SteamPresenceService.Shutdown();
        Check(SteamPresenceApi.WriteCalls == writes && PlaybackSnapshotService.CaptureCalls == captures &&
            TimerSnapshotService.ShutdownCalls == 1, "shutdown is idempotent and prevents later sampling/publication");
        Check(!SteamPresenceService.Preview(new SteamPresenceOptions { ShareTrackTitle = true }).Contains(SecretTitle),
            "shutdown clears cached private title");
        Console.WriteLine("PASS: " + _count + " presence lifecycle assertions");
    }
}

namespace UnityEngine
{
    internal static class Time
    {
        internal static double Now;
        internal static bool ThrowRead;
        internal static double realtimeSinceStartupAsDouble => ThrowRead ? throw new InvalidOperationException("PRIVATE_EXCEPTION") : Now;
    }
}
namespace HarmonyLib { internal sealed class Harmony { } }
namespace MusicBridge
{
    internal interface ISteamPresenceApi
    {
        bool TryConnect(out string session, out string error);
        Dictionary<string, string> ReadOwnPresence();
        bool Set(string key, string value);
    }
    internal sealed class SteamPresenceApi : ISteamPresenceApi
    {
        internal static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        internal static bool Connected = true, ThrowSet;
        internal static int WriteCalls;
        public bool TryConnect(out string session, out string error)
        { session = "PRIVATE_ACCOUNT"; error = Connected ? null : "测试离线"; return Connected; }
        public Dictionary<string, string> ReadOwnPresence() => new Dictionary<string, string>(Values);
        public bool Set(string key, string value)
        {
            WriteCalls++;
            if (ThrowSet) throw new InvalidOperationException("PRIVATE_EXCEPTION");
            if (value == null) Values.Remove(key); else Values[key] = value;
            return true;
        }
        internal static string ErrorKind(Exception ex) => ex.GetType().Name;
    }
    internal struct PlaybackSnapshot { internal PlaybackState State; internal string Title; }
    internal static class PlaybackSnapshotService
    {
        internal static PlaybackSnapshot Snapshot;
        internal static bool ThrowCapture;
        internal static int CaptureCalls;
        internal static PlaybackSnapshot Capture()
        { CaptureCalls++; if (ThrowCapture) throw new InvalidOperationException("PRIVATE_EXCEPTION"); return Snapshot; }
    }
    internal static class TimerSnapshotService
    {
        internal static TimerSnapshot Snapshot;
        internal static bool ThrowInitialize, ThrowCapture;
        internal static int InitializeCalls, CaptureCalls, ShutdownCalls;
        internal static string Capability => "模拟计时器能力";
        internal static void Initialize(HarmonyLib.Harmony harmony)
        { InitializeCalls++; if (ThrowInitialize) throw new InvalidOperationException("PRIVATE_EXCEPTION"); }
        internal static TimerSnapshot Capture(double now)
        { CaptureCalls++; if (ThrowCapture) throw new InvalidOperationException("PRIVATE_EXCEPTION"); return Snapshot; }
        internal static void Shutdown() { ShutdownCalls++; }
    }
    internal sealed class SteamPresenceSettingsStore
    {
        internal static bool ThrowRead;
        internal string Error => "";
        internal SteamPresenceSettingsStore(string path) { }
        internal SteamPresenceOptions Read() => ThrowRead ? throw new InvalidOperationException("PRIVATE_EXCEPTION") : new SteamPresenceOptions();
    }
    internal static class BridgePaths { internal static string Resolve(params string[] parts) => "fake-config"; }
}
