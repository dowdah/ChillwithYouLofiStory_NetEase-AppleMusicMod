using System;
using System.IO;
using MusicBridge;

static class Program
{
    private static int _checks;
    private static string Status => SteamPresenceApi.Values.TryGetValue("status", out var s) ? s : "";
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception("FAIL: " + label); _checks++; Console.WriteLine("PASS: " + label); }
    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.00001f;
    private static void Tick() { UnityEngine.Time.Now += 6; SteamPresenceService.Tick(); }

    static void Main()
    {
        string root = BridgePaths.Resolve("cross-module-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string volumePath = Path.Combine(root, "volume.json");
            AudioPlayer.Instance = new AudioPlayer { VolumeStore = new PlayerVolumeStore(volumePath),
                CurrentTrack = new TrackInfo { Id = 42, Name = "NeteaseSong", Artists = "NeteaseArtist · Literal", Album = "PRIVATE_NETEASE_ALBUM" } };
            AppleMusicService.NowPlaying = new SmtcSnapshot { Valid = true, Status = 4,
                Title = "AppleSong", Artist = "AppleArtist · Literal", AlbumTitle = "PRIVATE_APPLE_ALBUM" };
            VolumeHandoffs(volumePath);
            SteamPresenceService.Initialize(new HarmonyLib.Harmony());
            Tick(); Check(SteamPresenceApi.Writes == 0, "cross-module default still publishes nothing");
            Metadata(); OutputFailure();
            SteamPresenceService.Shutdown();
            Check(Status == "", "real publisher conditionally clears after cross-module run");
            Console.WriteLine("PASS: " + _checks + " cross-module assertions (fake Unity/Steam boundaries)");
        }
        finally { AudioPlayer.Instance.VolumeStore.Close(); Directory.Delete(root, true); }
    }

    static void VolumeHandoffs(string path)
    {
        var p = AudioPlayer.Instance;
        p.Volume = 0.1f; p.VolumeStore.Flush(); p.VolumeWrites = 0;
        var relay = new GameVolumeRelay();
        Check(!relay.Observe(MusicModules.Netease, 0.7f), "first native slider sample is only a baseline");
        Check(!relay.Observe(MusicModules.Game, 0.7f), "returning slider to game does not adjust NetEase");
        MusicModules.Game.SetVolume(0.9f);
        Check(!relay.Observe(MusicModules.Game, GameNowPlayingBar.GameVolume), "game volume adjustment stays game-owned");
        Check(!relay.Observe(MusicModules.Netease, 0.9f) && p.VolumeWrites == 0 && Near(p.Volume, 0.1f),
            "NetEase-game-NetEase handoff never imports the game's high volume");
        p.VolumeStore.Flush();
        Check(Near(new PlayerVolumeStore(path).Volume, 0.1f), "handoff leaves persisted low volume intact across reload");
        Check(!relay.Observe(MusicModules.Netease, 0.9f), "unchanged high slider is not replayed on subsequent ticks");
        Check(relay.Observe(MusicModules.Netease, 0.12f) && p.VolumeWrites == 1 && Near(p.Volume, 0.12f),
            "genuine active-owner slider adjustment goes through actual NetEase module");
        p.VolumeStore.Flush(); Check(Near(new PlayerVolumeStore(path).Volume, 0.12f), "genuine adjustment persists");
        Check(!relay.Observe(MusicModules.Apple, 0.12f) && AppleMusicService.VolumeWrites == 0 && Near(AppleMusicService.Volume, 0.25f),
            "direct NetEase-Apple handoff preserves independent Music.app volume");
        Check(relay.Observe(MusicModules.Apple, 0.4f) && Near(AppleMusicService.Volume, 0.4f), "active Apple edit reaches actual Apple module");
        Check(!relay.Observe(MusicModules.Netease, 0.4f) && Near(p.Volume, 0.12f), "direct Apple-NetEase handoff preserves NetEase volume");
        p.State = PlaybackState.Idle;
        Check(!relay.Observe(MusicModules.Netease, 0.8f), "inactive source only rebases");
        p.State = PlaybackState.Playing;
        Check(!relay.Observe(MusicModules.Netease, 0.8f) && Near(p.Volume, 0.12f), "track activation does not import idle slider changes");
        relay.Reset(); Check(!relay.Observe(MusicModules.Netease, 1f), "reattached UI ignores initial slider value");
        p.IsFm = true; NeteaseRuntime.Fm.Active = true;
        Check(relay.Observe(MusicModules.Netease, 0) && p.Volume == 0, "FM genuine mute remains supported");
        p.VolumeStore.Flush(); Check(new PlayerVolumeStore(path).Volume == 0, "mute persists through relay-module-store path");
        Check(!relay.Observe(MusicModules.Netease, float.NaN), "invalid sample ignored");
        Check(!relay.Observe(MusicModules.Netease, 0.8f) && p.Volume == 0, "invalid-sample recovery rebases instead of raising volume");
        p.IsFm = false; NeteaseRuntime.Fm.Active = false;
    }

    static void Metadata()
    {
        var options = new SteamPresenceOptions { Enabled = true, ShareTrackTitle = true };
        SteamPresenceService.Apply(options);
        foreach (var module in new[] { MusicModules.Apple, MusicModules.Netease })
        {
            MusicModules.Current = module; Tick();
            Check(module.Artist.Contains("PRIVATE_"), "local UI retains decorated album/quality text for " + module.Id);
            var snapshot = PlaybackSnapshotService.Capture();
            Check(snapshot.Artist == module.Artist && snapshot.TrackArtist == module.TrackArtist,
                "snapshot separates UI and sharing metadata for " + module.Id);
            Check(Status.Contains(module.Title + " — " + module.TrackArtist) && !Status.Contains("PRIVATE_"),
                "actual module-snapshot-service-publisher shares only title and artist for " + module.Id);
            Check(!SteamPresenceService.Diagnostics.Contains(module.Title) && !SteamPresenceService.Diagnostics.Contains(module.TrackArtist),
                "diagnostics do not leak metadata for " + module.Id);
            options.ShareTrackTitle = false; SteamPresenceService.Apply(options); Tick();
            Check(Status == "音乐播放中", "privacy off removes all metadata for " + module.Id);
            options.ShareTrackTitle = true; SteamPresenceService.Apply(options);
        }
        MusicModules.Current = MusicModules.Apple;
        AppleMusicService.NowPlaying.Artist = ""; Tick();
        Check(Status == "音乐播放中：AppleSong", "missing Apple artist never falls back to album");
        AppleMusicService.NowPlaying.Status = 5; Tick();
        Check(Status == "音乐已暂停：AppleSong", "paused Apple metadata remains album-free");
        MusicModules.Current = MusicModules.Netease; AudioPlayer.Instance.CurrentTrack.Artists = ""; Tick();
        Check(Status == "音乐播放中：NeteaseSong", "missing NetEase artist never falls back to album or quality");
        AudioPlayer.Instance.CurrentTrack = null; NeteaseRuntime.Fm.Active = true; NeteaseRuntime.Fm.Error = "PRIVATE_FM_ERROR";
        Tick(); Check(!Status.Contains("PRIVATE_FM_ERROR"), "FM status/error UI text is not published as artist");
        NeteaseRuntime.Fm.Active = false;
        AudioPlayer.Instance.CurrentTrack = new TrackInfo { Id = 42, Name = "NeteaseSong", Artists = "NeteaseArtist" };
        MusicModules.Current = MusicModules.Game; LocalMusicSource.Credit = ""; Tick();
        Check(Status == "音乐播放中：GameSong" && MusicModules.Game.Artist == "游戏自带音乐",
            "missing game credit omits artist while preserving local UI fallback");
        LocalMusicSource.Credit = "GameArtist"; Tick();
        Check(Status == "音乐播放中：GameSong — GameArtist", "game raw credit reaches shared metadata");
    }

    static void OutputFailure()
    {
        AudioOutputRecovery.OutputUnavailable = true;
        foreach (var module in new[] { MusicModules.Game, MusicModules.Netease })
        {
            MusicModules.Current = module; Tick(); var snapshot = PlaybackSnapshotService.Capture();
            Check(snapshot.State == PlaybackState.Loading && snapshot.DesiredPlaying && Status == "音乐缓冲中",
                "Unity output failure suppresses playing and stale metadata for " + module.Id);
            string key = snapshot.TrackKey; long epoch = snapshot.OwnerEpoch;
            AudioOutputRecovery.OutputUnavailable = false; Tick(); snapshot = PlaybackSnapshotService.Capture();
            Check(snapshot.State == PlaybackState.Playing && snapshot.TrackKey == key && snapshot.OwnerEpoch == epoch && Status.StartsWith("音乐播放中："),
                "output recovery keeps same track identity and restores state for " + module.Id);
            AudioOutputRecovery.OutputUnavailable = true;
        }
        MusicModules.Current = MusicModules.Apple; AppleMusicService.NowPlaying.Status = 4; Tick();
        Check(Status.StartsWith("音乐播放中：AppleSong"), "Unity output failure never degrades independent Music.app");
        MusicModules.Current = MusicModules.Game; GameNowPlayingBar.GameThinksItIsPlaying = false; Tick();
        Check(Status.StartsWith("音乐已暂停"), "output failure does not change paused game music into buffering");
        LocalMusicSource.HasTrack = false; Tick(); Check(Status == "", "output failure does not invent a game track");
        LocalMusicSource.HasTrack = true; GameNowPlayingBar.GameThinksItIsPlaying = true;
        MusicModules.Current = MusicModules.Netease; AudioPlayer.Instance.State = PlaybackState.Failed; Tick();
        Check(Status == "音乐播放失败", "output failure preserves explicit playback failure");
        AudioPlayer.Instance.State = PlaybackState.Paused; Tick(); Check(Status.StartsWith("音乐已暂停"), "paused NetEase stays paused");
        AudioOutputRecovery.OutputUnavailable = false; AudioPlayer.Instance.State = PlaybackState.Playing;
        AudioPlayer.Instance.IsBuffering = true; Tick(); Check(Status == "音乐缓冲中", "NetEase decoder buffering remains distinct");
        AudioPlayer.Instance.IsBuffering = false;
    }
}
