using System;

namespace MusicBridge;

internal enum PresenceTimerMode { None, Pomodoro, Countup }
internal enum PresenceTimerState { Idle, Running, Paused }
internal enum PresenceTimerPhase { None, Work, Break }

internal readonly struct TimerSnapshot
{
    internal readonly bool Available;
    internal readonly PresenceTimerMode Mode;
    internal readonly PresenceTimerState State;
    internal readonly PresenceTimerPhase Phase;
    internal readonly double ElapsedSeconds;
    internal readonly double? RemainingSeconds;
    internal readonly long Epoch;
    internal TimerSnapshot(bool available, PresenceTimerMode mode, PresenceTimerState state,
        PresenceTimerPhase phase, double elapsed, double? remaining, long epoch)
    { Available = available; Mode = mode; State = state; Phase = phase; ElapsedSeconds = elapsed; RemainingSeconds = remaining; Epoch = epoch; }
    internal static TimerSnapshot Unavailable => default;
}

// No wall-clock extrapolation: paused and running times are always the game's own values.
internal sealed class TimerPresenceText
{
    private TimerSnapshot _previous;
    private double _completedUntil;
    internal void Reset() { _previous = default; _completedUntil = 0; }
    internal string Format(TimerSnapshot snapshot, double now)
    {
        if (!snapshot.Available) { Reset(); return null; }
        bool sameInstance = _previous.Available && _previous.Epoch == snapshot.Epoch;
        if (!sameInstance) _completedUntil = 0;
        if (snapshot.State == PresenceTimerState.Idle)
        {
            if (sameInstance && _previous.State != PresenceTimerState.Idle) _completedUntil = now + 10;
            _previous = snapshot;
            return now < _completedUntil ? "计时完成" : null;
        }
        _completedUntil = 0;
        _previous = snapshot;
        string pause = snapshot.State == PresenceTimerState.Paused ? "（已暂停）" : "";
        if (snapshot.Mode == PresenceTimerMode.Pomodoro && snapshot.RemainingSeconds.HasValue)
            return "番茄钟" + (snapshot.Phase == PresenceTimerPhase.Break ? "休息" : "专注") + pause +
                " · 剩余 " + Math.Ceiling(Math.Max(0, snapshot.RemainingSeconds.Value) / 60).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " 分钟";
        if (snapshot.Mode == PresenceTimerMode.Countup)
            return "正计时" + pause + " · 已过 " + Math.Floor(Math.Max(0, snapshot.ElapsedSeconds) / 60).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " 分钟";
        return null;
    }
}
