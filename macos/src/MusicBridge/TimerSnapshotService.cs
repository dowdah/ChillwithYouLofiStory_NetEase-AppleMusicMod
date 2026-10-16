using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MusicBridge;

internal static class TimerSnapshotService
{
    private static TimerInstanceReader _pomodoro, _countup;
    private static bool _installed, _quitting;
    private static string _bindingError;
    private static PresenceTimerMode _lastMode;
    internal static string Capability => "番茄钟：" + CapabilityOf(_pomodoro) + "；正计时：" + CapabilityOf(_countup) +
        (_bindingError == null ? "" : "；" + _bindingError);
    private static string CapabilityOf(TimerInstanceReader reader) => reader == null ? "签名不可用" :
        reader.HasInstance ? "已观察实例（时间语义待实机核对）" : "等待游戏实例";

    internal static void Initialize(Harmony harmony)
    {
        if (_installed) return;
        _installed = true;
        _pomodoro = Install(harmony, "Bulbul.PomodoroService", "Bulbul.PomodoroTimerService", PresenceTimerMode.Pomodoro, nameof(ObservePomodoro), nameof(DisposePomodoro));
        _countup = Install(harmony, "Bulbul.CountupService", "Bulbul.CountupTimerService", PresenceTimerMode.Countup, nameof(ObserveCountup), nameof(DisposeCountup));
        SceneManager.sceneUnloaded += OnSceneUnloaded;
        SceneManager.activeSceneChanged += OnActiveSceneChanged;
    }
    private static TimerInstanceReader Install(Harmony harmony, string serviceName, string clockName, PresenceTimerMode mode, string observe, string dispose)
    {
        try
        {
            Type service = AccessTools.TypeByName(serviceName), clock = AccessTools.TypeByName(clockName);
            var reader = new TimerInstanceReader(service, clock, mode);
            // Observe only existing service execution. Never call Setup/Update/Dispose ourselves.
            harmony.Patch(TimerInstanceReader.Method(service, "Setup", typeof(void)), postfix: new HarmonyMethod(typeof(TimerSnapshotService), observe));
            harmony.Patch(TimerInstanceReader.Method(service, "Update", typeof(void)), postfix: new HarmonyMethod(typeof(TimerSnapshotService), observe));
            harmony.Patch(TimerInstanceReader.Method(service, "Dispose", typeof(void)), postfix: new HarmonyMethod(typeof(TimerSnapshotService), dispose));
            return reader;
        }
        catch (Exception ex) { _bindingError = "计时器绑定失败（" + ex.GetType().Name + "）"; return null; }
    }
    private static void ObservePomodoro(object __instance) { try { if (!_quitting) _pomodoro?.Observe(__instance, Time.realtimeSinceStartupAsDouble); } catch { } }
    private static void ObserveCountup(object __instance) { try { if (!_quitting) _countup?.Observe(__instance, Time.realtimeSinceStartupAsDouble); } catch { } }
    private static void DisposePomodoro(object __instance) { try { _pomodoro?.Forget(__instance); } catch { } }
    private static void DisposeCountup(object __instance) { try { _countup?.Forget(__instance); } catch { } }
    private static void OnSceneUnloaded(Scene scene) => Invalidate();
    private static void OnActiveSceneChanged(Scene previous, Scene next) => Invalidate();
    private static void Invalidate() { _pomodoro?.Forget(); _countup?.Forget(); _lastMode = PresenceTimerMode.None; }
    internal static TimerSnapshot Capture(double now)
    {
        if (_quitting) return default;
        var p = _pomodoro?.Capture(now) ?? default;
        var c = _countup?.Capture(now) ?? default;
        bool pa = p.Available && p.State != PresenceTimerState.Idle, ca = c.Available && c.State != PresenceTimerState.Idle;
        if (pa && ca) return default; // Ambiguous ownership: never guess from the open UI tab.
        if (pa) { _lastMode = PresenceTimerMode.Pomodoro; return p; }
        if (ca) { _lastMode = PresenceTimerMode.Countup; return c; }
        if (_lastMode == PresenceTimerMode.Pomodoro) return p;
        if (_lastMode == PresenceTimerMode.Countup) return c;
        return p.Available ? p : c;
    }
    internal static void Shutdown()
    {
        _quitting = true; Invalidate();
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
    }
}
