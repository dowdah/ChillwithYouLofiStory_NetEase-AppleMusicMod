using System;

namespace Bulbul
{
    // Exact names/signatures only; these fakes never run the actual game or its timer controls.
    internal sealed class PomodoroService
    {
        public enum MainState { Idle, Work, Rest, Pause }
        public enum PomodoroType { Work, Break, Complete }
        public MainState _mainState = MainState.Idle;
        public PomodoroType _currentPomodoroType = PomodoroType.Complete;
        public PomodoroTimerService _pomodoroTimerService = new PomodoroTimerService();
        public void Setup() { }
        public void Update() { }
        public void Dispose() { }
    }
    internal sealed class PomodoroTimerService
    {
        public bool _isRunning = true;
        public double Total = 1500, Remaining = 1200;
        public bool Throw;
        public TimeSpan CurrentTimeSpan() => TimeSpan.FromSeconds(Total);
        public TimeSpan RemainTimeSpan() => Throw ? throw new InvalidOperationException() : TimeSpan.FromSeconds(Remaining);
    }
    internal sealed class CountupService
    {
        public enum MainState { Idle, Work, Pause }
        public enum CountupTimerType { Work, Break, Complete }
        public MainState _mainState = MainState.Idle;
        public CountupTimerType _currentCountupType = CountupTimerType.Complete;
        public CountupTimerService _countupTimerService = new CountupTimerService();
        public void Setup() { }
        public void Update() { }
        public void Dispose() { }
    }
    internal sealed class CountupTimerService
    {
        public bool _isRunning = true;
        public float Elapsed;
        public float ElapsedTimeSeconds => Elapsed;
    }
}
