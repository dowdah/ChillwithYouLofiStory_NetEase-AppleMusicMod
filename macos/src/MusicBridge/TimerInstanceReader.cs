using System;
using System.Reflection;

namespace MusicBridge;

// Signature-checked binding to existing instances. Never resolves/constructs a game service.
internal sealed class TimerInstanceReader
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly PresenceTimerMode _mode;
    private readonly Type _serviceType;
    private readonly FieldInfo _state, _phase, _clock, _running;
    private readonly MethodInfo _elapsed, _remaining, _duration;
    private WeakReference _instance;
    private double _lastSeen;
    internal long Epoch { get; private set; }
    private static long _nextEpoch;
    internal bool HasInstance => _instance?.IsAlive == true;

    internal TimerInstanceReader(Type service, Type clock, PresenceTimerMode mode)
    {
        _serviceType = service ?? throw new MissingMemberException();
        _mode = mode;
        bool pomodoro = mode == PresenceTimerMode.Pomodoro;
        if (service.FullName != (pomodoro ? "Bulbul.PomodoroService" : "Bulbul.CountupService") ||
            clock?.FullName != (pomodoro ? "Bulbul.PomodoroTimerService" : "Bulbul.CountupTimerService"))
            throw new MissingMemberException();
        Type state = service.GetNestedType("MainState", BindingFlags.Public | BindingFlags.NonPublic);
        Type phase = service.GetNestedType(pomodoro ? "PomodoroType" : "CountupTimerType", BindingFlags.Public | BindingFlags.NonPublic);
        ValidateEnum(state, pomodoro ? new[] { "Idle", "Work", "Rest", "Pause" } : new[] { "Idle", "Work", "Pause" });
        ValidateEnum(phase, new[] { "Work", "Break", "Complete" });
        _state = Field(service, "_mainState", state);
        _phase = Field(service, pomodoro ? "_currentPomodoroType" : "_currentCountupType", phase);
        _clock = Field(service, pomodoro ? "_pomodoroTimerService" : "_countupTimerService", clock);
        _running = Field(clock, "_isRunning", typeof(bool));
        if (pomodoro)
        {
            _remaining = Method(clock, "RemainTimeSpan", typeof(TimeSpan));
            _duration = Method(clock, "CurrentTimeSpan", typeof(TimeSpan));
        }
        else _elapsed = Method(clock, "get_ElapsedTimeSeconds", typeof(float));
        Method(service, "Setup", typeof(void)); Method(service, "Update", typeof(void)); Method(service, "Dispose", typeof(void));
    }

    internal static MethodInfo Method(Type type, string name, Type result)
    {
        var method = type.GetMethod(name, InstanceFlags, null, Type.EmptyTypes, null);
        if (method == null || method.ReturnType != result || method.IsStatic) throw new MissingMethodException();
        return method;
    }
    private static FieldInfo Field(Type type, string name, Type expected)
    {
        var field = type.GetField(name, InstanceFlags);
        if (field == null || field.FieldType != expected || field.IsStatic) throw new MissingFieldException();
        return field;
    }
    private static void ValidateEnum(Type type, string[] names)
    {
        if (type == null || !type.IsEnum || Enum.GetUnderlyingType(type) != typeof(int) || Enum.GetNames(type).Length != names.Length)
            throw new MissingMemberException();
        for (int i = 0; i < names.Length; i++)
            if (!Enum.IsDefined(type, names[i]) || Convert.ToInt32(Enum.Parse(type, names[i])) != i) throw new MissingMemberException();
    }
    internal void Observe(object instance, double now)
    {
        if (instance == null || instance.GetType() != _serviceType) return;
        if (!ReferenceEquals(_instance?.Target, instance)) { _instance = new WeakReference(instance); Epoch = ++_nextEpoch; }
        _lastSeen = now;
    }
    internal void Forget(object instance = null)
    {
        if (instance != null && !ReferenceEquals(_instance?.Target, instance)) return;
        _instance = null; Epoch = ++_nextEpoch;
    }
    internal TimerSnapshot Capture(double now)
    {
        object instance = _instance?.Target;
        if (instance == null || now < _lastSeen || now - _lastSeen > 2.5) return TimerSnapshot.Unavailable;
        try
        {
            int state = Convert.ToInt32(_state.GetValue(instance)), phase = Convert.ToInt32(_phase.GetValue(instance));
            if (state < 0 || state > (_mode == PresenceTimerMode.Pomodoro ? 3 : 2) || phase < 0 || phase > 2) return TimerSnapshot.Unavailable;
            object clock = _clock.GetValue(instance);
            if (clock == null) return TimerSnapshot.Unavailable;
            bool running = (bool)_running.GetValue(clock);
            double elapsed;
            double? remaining = null;
            if (_mode == PresenceTimerMode.Pomodoro)
            {
                double total = ((TimeSpan)_duration.Invoke(clock, null)).TotalSeconds;
                remaining = ((TimeSpan)_remaining.Invoke(clock, null)).TotalSeconds;
                if (!Valid(total) || !Valid(remaining.Value) || remaining > total) return TimerSnapshot.Unavailable;
                elapsed = total - remaining.Value;
            }
            else elapsed = (float)_elapsed.Invoke(clock, null);
            if (!Valid(elapsed)) return TimerSnapshot.Unavailable;
            if (state == 0)
                return new TimerSnapshot(true, _mode, PresenceTimerState.Idle, PresenceTimerPhase.None, elapsed, remaining, Epoch);
            // CountupService.Pause explicitly changes its phase to Break while freezing its clock.
            if (phase == 2 || (_mode == PresenceTimerMode.Countup && phase == 1 && state != 2)) return TimerSnapshot.Unavailable;
            bool paused = !running || state == (_mode == PresenceTimerMode.Pomodoro ? 3 : 2);
            return new TimerSnapshot(true, _mode, paused ? PresenceTimerState.Paused : PresenceTimerState.Running,
                phase == 0 ? PresenceTimerPhase.Work : PresenceTimerPhase.Break, elapsed, remaining, Epoch);
        }
        catch { return TimerSnapshot.Unavailable; }
    }
    private static bool Valid(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
}
