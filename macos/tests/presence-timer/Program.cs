using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Bulbul;
using MusicBridge;

static class Program
{
    static int assertions, failures;
    static void Check(bool value, string message)
    { assertions++; if (!value) { failures++; Console.Error.WriteLine("FAIL: " + message); } }
    static void Main()
    {
        Presenter(); Readers(); Signature(); Lifecycle();
        Console.WriteLine("Timer tests: " + assertions + " assertions, " + failures + " failures (fake game/runtime only)");
        if (failures > 0) Environment.Exit(1);
    }
    static TimerSnapshot Snapshot(PresenceTimerState state = PresenceTimerState.Running,
        PresenceTimerMode mode = PresenceTimerMode.Pomodoro, PresenceTimerPhase phase = PresenceTimerPhase.Work,
        double elapsed = 0, double? remaining = 60, long epoch = 1) => new TimerSnapshot(true, mode, state, phase, elapsed, remaining, epoch);
    static void Presenter()
    {
        var t = new TimerPresenceText();
        Check(t.Format(TimerSnapshot.Unavailable, 0) == null, "unavailable omitted");
        Check(t.Format(Snapshot(PresenceTimerState.Idle), 0) == null, "never started omitted");
        Check(t.Format(Snapshot(remaining: 60.001), 1).Contains("剩余 2 分钟"), "countdown ceil just above minute");
        Check(t.Format(Snapshot(remaining: 60), 2).Contains("剩余 1 分钟"), "countdown exact minute");
        Check(t.Format(Snapshot(remaining: .001), 3).Contains("剩余 1 分钟"), "countdown last fraction");
        Check(t.Format(Snapshot(remaining: 0), 4).Contains("剩余 0 分钟"), "running zero not prematurely complete");
        Check(t.Format(Snapshot(PresenceTimerState.Paused, remaining: 61), 5).Contains("（已暂停） · 剩余 2 分钟"), "pause explicit");
        string pause = t.Format(Snapshot(PresenceTimerState.Paused, remaining: 61), 100);
        Check(pause.Contains("剩余 2 分钟"), "paused clock not wall extrapolated");
        Check(t.Format(Snapshot(phase: PresenceTimerPhase.Break, remaining: 300), 101).StartsWith("番茄钟休息"), "break phase");
        Check(t.Format(Snapshot(mode: PresenceTimerMode.Countup, elapsed: 59.999, remaining: null, epoch: 2), 102) == "正计时 · 已过 0 分钟", "countup floor");
        Check(t.Format(Snapshot(mode: PresenceTimerMode.Countup, elapsed: 60, remaining: null, epoch: 2), 103) == "正计时 · 已过 1 分钟", "countup minute boundary");
        Check(t.Format(Snapshot(PresenceTimerState.Paused, PresenceTimerMode.Countup, elapsed: 119.9, remaining: null, epoch: 2), 104) == "正计时（已暂停） · 已过 1 分钟", "countup pause");
        var idle = Snapshot(PresenceTimerState.Idle, epoch: 2);
        Check(t.Format(idle, 105) == "计时完成", "completion transition");
        Check(t.Format(idle, 114.99) == "计时完成", "completion lasts10");
        Check(t.Format(idle, 115) == null, "completion exact expiry");
        Check(t.Format(idle, 120) == null, "idle cannot extend completion");
        t.Format(Snapshot(epoch: 3), 121); t.Format(Snapshot(PresenceTimerState.Idle, epoch: 3), 122);
        Check(t.Format(Snapshot(phase: PresenceTimerPhase.Break, epoch: 3), 123).StartsWith("番茄钟休息"), "new phase supersedes completion");
        Check(t.Format(Snapshot(PresenceTimerState.Idle, epoch: 4), 124) == null, "new epoch does not report old completion");
        t.Format(Snapshot(epoch: 4), 125); t.Format(Snapshot(PresenceTimerState.Idle, epoch: 4), 126);
        Check(t.Format(TimerSnapshot.Unavailable, 127) == null, "invalidation removes completion");
        Check(t.Format(Snapshot(PresenceTimerState.Idle, epoch: 4), 128) == null, "no completion replay after invalidation");
        t.Reset(); Check(t.Format(idle, 130) == null, "explicit reset");
    }
    static TimerInstanceReader PomodoroReader() => new TimerInstanceReader(typeof(PomodoroService), typeof(PomodoroTimerService), PresenceTimerMode.Pomodoro);
    static TimerInstanceReader CountupReader() => new TimerInstanceReader(typeof(CountupService), typeof(CountupTimerService), PresenceTimerMode.Countup);
    static void Readers()
    {
        var p = new PomodoroService(); var reader = PomodoroReader();
        Check(!reader.HasInstance && !reader.Capture(0).Available, "reader starts unbound");
        reader.Observe(new object(), 0); Check(!reader.HasInstance, "wrong instance ignored");
        reader.Observe(p, 10); long epoch = reader.Epoch;
        Check(reader.Capture(10).State == PresenceTimerState.Idle, "idle available");
        p._mainState = PomodoroService.MainState.Work; p._currentPomodoroType = PomodoroService.PomodoroType.Work;
        var v = reader.Capture(10);
        Check(v.Available && v.State == PresenceTimerState.Running && v.Phase == PresenceTimerPhase.Work, "pomodoro running work");
        Check(v.ElapsedSeconds == 300 && v.RemainingSeconds == 1200, "game duration-minus-remaining semantics");
        p._mainState = PomodoroService.MainState.Rest; p._currentPomodoroType = PomodoroService.PomodoroType.Break;
        Check(reader.Capture(10).Phase == PresenceTimerPhase.Break, "pomodoro break");
        p._mainState = PomodoroService.MainState.Pause;
        Check(reader.Capture(10).State == PresenceTimerState.Paused, "paused service state wins even if clock flag stale");
        p._mainState = PomodoroService.MainState.Work; p._pomodoroTimerService._isRunning = false;
        Check(reader.Capture(10).State == PresenceTimerState.Paused, "stopped clock not running");
        reader.Observe(p, 11); Check(reader.Epoch == epoch, "same instance heartbeat stable epoch");
        Check(reader.Capture(13.5).Available && !reader.Capture(13.501).Available, "heartbeat deadline");
        Check(!reader.Capture(10).Available, "backward clock rejected");
        reader.Observe(p, 14); Check(reader.Capture(14).Available, "fresh heartbeat recovers");
        p._pomodoroTimerService.Remaining = 1501; Check(!reader.Capture(14).Available, "remaining cannot exceed total");
        p._pomodoroTimerService.Remaining = -1; Check(!reader.Capture(14).Available, "negative remaining rejected");
        p._pomodoroTimerService.Remaining = 0; p._pomodoroTimerService.Throw = true;
        Check(!reader.Capture(14).Available, "reflection reader exception isolated");
        p._pomodoroTimerService.Throw = false; p._mainState = (PomodoroService.MainState)100;
        Check(!reader.Capture(14).Available, "invalid state rejected");
        p._mainState = PomodoroService.MainState.Work; p._currentPomodoroType = PomodoroService.PomodoroType.Complete;
        Check(!reader.Capture(14).Available, "active completed transitional state unavailable");
        p._mainState = PomodoroService.MainState.Idle;
        Check(reader.Capture(14).Available, "idle complete is readable");
        p._pomodoroTimerService = null; Check(!reader.Capture(14).Available, "missing clock unavailable");
        reader.Forget(new PomodoroService()); Check(reader.HasInstance, "unrelated disposal ignored");
        reader.Forget(p); Check(!reader.HasInstance && reader.Epoch != epoch, "observed disposal invalidates");
        var c = new CountupService(); var cr = CountupReader(); cr.Observe(c, 0);
        c._mainState = CountupService.MainState.Work; c._currentCountupType = CountupService.CountupTimerType.Work;
        c._countupTimerService.Elapsed = 75;
        Check(cr.Capture(0).ElapsedSeconds == 75 && cr.Capture(0).RemainingSeconds == null, "countup game elapsed semantics");
        // Shipped CountupService.Pause sets both MainState.Pause and CountupTimerType.Break.
        c._mainState = CountupService.MainState.Pause; c._currentCountupType = CountupService.CountupTimerType.Break;
        c._countupTimerService._isRunning = false;
        Check(cr.Capture(0).Available && cr.Capture(0).State == PresenceTimerState.Paused, "real countup pause uses Break phase and remains available");
        c._currentCountupType = CountupService.CountupTimerType.Work;
        c._countupTimerService.Elapsed = float.NaN; Check(!cr.Capture(0).Available, "NaN elapsed rejected");
        c._countupTimerService.Elapsed = float.PositiveInfinity; Check(!cr.Capture(0).Available, "infinite elapsed rejected");
        c._countupTimerService.Elapsed = -1; Check(!cr.Capture(0).Available, "negative elapsed rejected");
        var weakReader = CountupReader(); WeakReference weak = ObserveTemporary(weakReader);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Check(!weak.IsAlive && !weakReader.HasInstance && !weakReader.Capture(0).Available, "reader weak reference does not root disposed object");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ObserveTemporary(TimerInstanceReader reader)
    { var service = new CountupService(); reader.Observe(service, 0); return new WeakReference(service); }
    static void Throws(Action action, string message)
    { try { action(); Check(false, message); } catch (MissingMemberException) { Check(true, message); } }
    static void Signature()
    {
        Throws(() => new TimerInstanceReader(typeof(CountupService), typeof(PomodoroTimerService), PresenceTimerMode.Countup), "clock exact name checked");
        Throws(() => new TimerInstanceReader(typeof(object), typeof(CountupTimerService), PresenceTimerMode.Countup), "service exact name checked");
        Type malformed = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("TimerMalformed"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("timer").DefineType("Bulbul.CountupService", TypeAttributes.Public).CreateType();
        Throws(() => new TimerInstanceReader(malformed, typeof(CountupTimerService), PresenceTimerMode.Countup), "missing nested enum signature rejected");
        Throws(() => TimerInstanceReader.Method(typeof(WrongMethods), "Update", typeof(void)), "wrong method result rejected");
        Throws(() => TimerInstanceReader.Method(typeof(WrongMethods), "Setup", typeof(void)), "static method rejected");
    }
    sealed class WrongMethods { public int Update() => 0; public static void Setup() { } }
    static void Observe(string method, object service, double now)
    {
        UnityEngine.Time.realtimeSinceStartupAsDouble = now;
        typeof(TimerSnapshotService).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new[] { service });
    }
    static void Lifecycle()
    {
        var harmony = new HarmonyLib.Harmony(); TimerSnapshotService.Initialize(harmony); TimerSnapshotService.Initialize(harmony);
        Check(harmony.Patches.Count == 6, "binds six postfix observers once");
        Check(!TimerSnapshotService.Capture(0).Available, "service starts unavailable");
        var p = new PomodoroService { _mainState = PomodoroService.MainState.Work, _currentPomodoroType = PomodoroService.PomodoroType.Work };
        var c = new CountupService();
        Observe("ObservePomodoro", p, 1); Observe("ObserveCountup", c, 1);
        var first = TimerSnapshotService.Capture(1);
        Check(first.Mode == PresenceTimerMode.Pomodoro && first.State == PresenceTimerState.Running, "active timer selected over idle");
        c._mainState = CountupService.MainState.Work; c._currentCountupType = CountupService.CountupTimerType.Work;
        Check(!TimerSnapshotService.Capture(1).Available, "two active timers fail closed");
        p._mainState = PomodoroService.MainState.Idle; p._currentPomodoroType = PomodoroService.PomodoroType.Complete;
        Check(TimerSnapshotService.Capture(1).Mode == PresenceTimerMode.Countup, "new active mode supersedes old idle");
        c._mainState = CountupService.MainState.Idle; c._currentCountupType = CountupService.CountupTimerType.Complete;
        Check(TimerSnapshotService.Capture(1).Mode == PresenceTimerMode.Countup, "last active idle retained for completion");
        UnityEngine.SceneManagement.SceneManager.Unload();
        Check(!TimerSnapshotService.Capture(1).Available, "scene unload invalidates immediately");
        p._mainState = PomodoroService.MainState.Work; p._currentPomodoroType = PomodoroService.PomodoroType.Work;
        Observe("ObservePomodoro", p, 2); var rebound = TimerSnapshotService.Capture(2);
        Check(rebound.Available && rebound.Epoch != first.Epoch, "post-scene observed instance rebind increments epoch");
        UnityEngine.SceneManagement.SceneManager.Change(); Check(!TimerSnapshotService.Capture(2).Available, "active scene change invalidates");
        Observe("ObservePomodoro", p, 3); Observe("DisposePomodoro", p, 3);
        Check(!TimerSnapshotService.Capture(3).Available, "dispose callback invalidates");
        Observe("ObservePomodoro", p, 4); Check(!TimerSnapshotService.Capture(7).Available, "service stale heartbeat unavailable");
        TimerSnapshotService.Shutdown(); Observe("ObservePomodoro", p, 8);
        Check(!TimerSnapshotService.Capture(8).Available, "shutdown ignores new observations");
    }
}
