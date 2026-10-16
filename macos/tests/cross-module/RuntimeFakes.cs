using System;
using System.Collections.Generic;

// Only Unity/native/external service boundaries are fakes. Modules, relay, volume persistence,
// snapshot capture, presence service, formatter and publisher are linked production sources.
namespace UnityEngine
{
    public class Sprite { }
    public struct Color { public static Color white; }
    internal static class Time { internal static double Now; internal static double realtimeSinceStartupAsDouble => Now; internal static float realtimeSinceStartup => (float)Now; }
    internal static class Mathf { internal static float Clamp01(float value) => Math.Max(0, Math.Min(1, value)); }
    internal class AudioSource
    {
        private float _volume;
        internal int Writes;
        internal float volume { get => _volume; set { Writes++; _volume = value; } }
    }
}
namespace UnityEngine.UI { public class Image { public UnityEngine.Sprite sprite; public UnityEngine.Color color; } }
namespace HarmonyLib { internal class Harmony { } }
namespace MusicBridge
{
    static class MusicModules
    {
        internal static readonly IMusicModule Netease = new NeteaseModule(), Apple = new AppleModule(), Game = new GameBuiltInModule();
        internal static IMusicModule Current = Netease;
    }
    static class MusicTransport { internal static bool CanNext => true; internal static bool CanPrevious => true; }
    static class PlaybackCoordinator { internal static bool IsSwitching => false; }
    static class AudioOutputRecovery { internal static bool OutputUnavailable; }
    internal sealed partial class AudioPlayer
    {
        internal static AudioPlayer Instance;
        private UnityEngine.AudioSource _source = new UnityEngine.AudioSource();
        internal PlayerVolumeStore VolumeStore { get => _volumeStore; set { _volumeStore = value; _source.volume = value.Volume; } }
        internal int VolumeWrites { get => _source.Writes; set => _source.Writes = value; }
        internal long SessionEpoch => 1; internal int PlaybackGeneration => 1;
        internal TrackInfo CurrentTrack;
        internal PlaybackState State = PlaybackState.Playing;
        internal bool IsBuffering, IsFm, Shuffle, RepeatOne;
        internal bool DesiredPlaying => State == PlaybackState.Playing || State == PlaybackState.Loading;
        internal bool IsActive => CurrentTrack != null && State != PlaybackState.Idle;
        internal float PositionSeconds => 5; internal float DurationSeconds => 100;
        internal NeteasePlaybackSource PlaybackSource = new NeteasePlaybackSource();
        internal void TogglePlayPause() { } internal void Next() { } internal void Previous() { }
        internal void PauseIfPlaying() { } internal void Seek(float seconds) { }
    }
    static class BridgeLog { internal static void Warn(string message) { } }
    class NeteasePlaybackSource { internal string QualityLabel => "PRIVATE_QUALITY"; }
    static class NeteaseRuntime { internal static FmState Fm = new FmState(); }
    class FmState { internal bool Active, Suspended; internal string Error; }
    static class CoverCache { internal static void Apply(UnityEngine.UI.Image target, string url, int size, UnityEngine.Color color) { } }
    enum AmConnState { Connected }
    static class AppleMusicService
    {
        internal static bool HasTrack => NowPlaying?.Valid == true;
        internal static bool IsPlaying => NowPlaying?.IsPlaying == true;
        internal static SmtcSnapshot NowPlaying;
        internal static double GetPosition() => 5; internal static double Duration => 100;
        internal static bool Shuffle, RepeatOne;
        internal static float Volume = 0.25f;
        internal static int VolumeWrites;
        internal static AmConnState ConnState => AmConnState.Connected;
        internal static string StatusText => "Connected";
        internal static UnityEngine.Sprite CoverSprite => null;
        internal static void TogglePlayPause() { } internal static void NextInQueue() { } internal static void PreviousInQueue() { }
        internal static void PauseIfPlaying() { } internal static void Seek(double seconds) { }
        internal static void SetVolume(float value) { Volume = value; VolumeWrites++; }
    }
    static class UiKit { internal static UnityEngine.Color CoverPlaceholder; }
    static class LocalMusicSource
    {
        internal static bool HasTrack = true;
        internal static string Title = "GameSong", Credit = "GameArtist";
        internal static bool TryGet(out string title, out string credit, out float progress)
        { title = Title; credit = Credit; progress = 0.5f; return HasTrack; }
        internal static double PlayingDuration => 100;
        internal static bool IsShuffle => false; internal static bool IsRepeatOne => false;
        internal static void InvokeFacility(string name) { } internal static void Pause() { } internal static void SeekNormalized(float value) { }
    }
    static class GameNowPlayingBar
    {
        internal static bool GameThinksItIsPlaying = true;
        internal static float GameVolume = 0.7f;
        internal static void SetGameVolume(float value) { GameVolume = value; }
    }
    internal interface ISteamPresenceApi
    {
        bool TryConnect(out string session, out string error);
        Dictionary<string, string> ReadOwnPresence();
        bool Set(string key, string value);
    }
    internal sealed class SteamPresenceApi : ISteamPresenceApi
    {
        internal static readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        internal static int Writes;
        public bool TryConnect(out string session, out string error) { session = "FAKE_ACCOUNT"; error = null; return true; }
        public Dictionary<string, string> ReadOwnPresence() => new Dictionary<string, string>(Values);
        public bool Set(string key, string value)
        {
            if (key != "status") throw new Exception("Unexpected key");
            Writes++;
            if (value == null) Values.Remove(key); else Values[key] = value;
            return true;
        }
        internal static string ErrorKind(Exception ex) => ex.GetType().Name;
    }
    internal static class TimerSnapshotService
    {
        internal static string Capability => "fake timer";
        internal static void Initialize(HarmonyLib.Harmony harmony) { }
        internal static TimerSnapshot Capture(double now) => default;
        internal static void Shutdown() { }
    }
    internal sealed class SteamPresenceSettingsStore
    {
        internal string Error => "";
        internal SteamPresenceSettingsStore(string path) { }
        internal SteamPresenceOptions Read() => new SteamPresenceOptions();
    }
}
