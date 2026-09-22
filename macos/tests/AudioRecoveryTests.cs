using System;
using MusicBridge;

internal static class AudioRecoveryTests
{
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
    }
    public static void Run()
    {
        Check(AudioRecoveryPolicy.IsOutputFailure("FMOD failed to initialize any audio devices, running on emulated software output with no sound.")
            && !AudioRecoveryPolicy.IsOutputFailure("Default audio device was changed, but the audio system failed to initialize it. Attempting to reset sound system.")
            && !AudioRecoveryPolicy.IsOutputFailure("FMOD failed to initialize the output device.: driver failed (57)")
            && AudioRecoveryPolicy.IsOutputFailure("FMOD failed to initialize the output device, attempting to initialize the null output.")
            && !AudioRecoveryPolicy.IsOutputFailure("MusicBridge: FMOD failed to initialize the output device")
            && !AudioRecoveryPolicy.IsOutputFailure("Audio download failed")
            && !AudioRecoveryPolicy.IsOutputFailure(null), "only terminal silent-output failure triggers recovery; transient retries do not create a reset loop");
        Check(AudioRecoveryPolicy.IsDeviceTransition("Default audio device was changed, but the audio system failed to initialize it. Attempting to reset sound system.")
            && !AudioRecoveryPolicy.IsOutputFailure("Default audio device was changed, but the audio system failed to initialize it. Attempting to reset sound system."),
            "device transition freezes track advancement without requesting a global reset");
        var progress = new AudioPlaybackProgress();
        progress.Reset(20, 100);
        Check(progress.TryObserve(20.2, 100.2), "normal playback advances trusted position");
        Check(!progress.TryObserve(240, 100.3) && Math.Abs(progress.Position - 20.2) < 0.001,
            "headphone reconnect jump to clip end cannot become trusted EOF");
        Check(!progress.TryObserve(0, 100.4) && progress.Position > 20,
            "device reset to zero preserves last stable seek position");
        progress.Reset(progress.Position, 103);
        Check(progress.TryObserve(20.4, 103.2), "same-track reload resumes progress after device settle");
        progress.Reset(239, 104);
        Check(progress.TryObserve(239.5, 104.5), "explicit user seek near end remains valid");
        Check(progress.TryObserve(240, 105), "genuine end-of-track remains observable");
        progress.Reset(80, 200);
        progress.Reset(80, 300);
        Check(progress.TryObserve(80.2, 300.2), "pause and resume reseed elapsed-time tracking");
        Check(!progress.TryObserve(double.NaN, 300.3) && !progress.TryObserve(double.PositiveInfinity, 300.3),
            "invalid audio timestamps cannot become completion evidence");
        float fakeSourceTime = 0;
        AudioPlaybackProgress.ResumeAt(83.5f, () => fakeSourceTime = 0, value => fakeSourceTime = value);
        Check(fakeSourceTime == 83.5f, "fresh Unity source resetting on Play still resumes saved paused position");
        AudioPlaybackProgress.ResumeAt(42f, () => fakeSourceTime = 3f, value => fakeSourceTime = value);
        Check(fakeSourceTime == 42f, "existing paused source also applies saved seek after resuming");
        var p = new AudioRecoveryPolicy();
        Check(!p.Failed && !p.TryBegin(10), "normal playback does not trigger audio resets");
        p.ReportFailure(10); p.ReportFailure(11);
        Check(!p.TryBegin(11.9) && p.TryBegin(12), "device failure is debounced while the device settles");
        p.Complete(false, 12);
        Check(!p.TryBegin(16.9) && p.TryBegin(17), "failed reset retries with delay");
        p.Complete(false, 17);
        Check(!p.TryBegin(26.9) && p.TryBegin(27), "repeated failure increases retry delay");
        p.Complete(false, 27);
        Check(p.Failed && !p.Pending && !p.TryBegin(28) && !p.RequestRetry(28), "persistent failure stops after three attempts instead of resetting forever");
        Check(!p.RequestRetry(71) && p.RequestRetry(88) && p.TryBegin(89), "manual retry becomes available after cooldown");
        p.Complete(true,89);
        Check(!p.Failed && !p.Pending, "successful recovery clears fault status");
        // Some drivers report a failure after Reset returned true. These must still count.
        p.ReportFailure(90); Check(p.TryBegin(95), "late driver failure can schedule another bounded attempt");
        p.Complete(true,95); p.ReportFailure(96); Check(p.TryBegin(106), "late failures keep retry history");
        p.Complete(true,106); p.ReportFailure(107);
        Check(p.Failed && !p.Pending && !p.RequestRetry(108), "delayed errors cannot bypass the per-minute reset limit");
        var manual = new AudioRecoveryPolicy();
        Check(manual.RequestRetry(0) && !manual.RequestRetry(0.1) && !manual.TryBegin(0.4) && manual.TryBegin(0.5), "repeated repair button clicks are coalesced");
    }
}
