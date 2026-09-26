using System;
using UnityEngine;

namespace MusicBridge;

// Runs on the existing persistent Unity dispatcher. Native callbacks only enqueue data.
internal static class SystemMediaService
{
    private static readonly MacSystemMediaBridge Bridge = new MacSystemMediaBridge();
    private static readonly MediaCommandGate Gate = new MediaCommandGate();
    private static bool _attempted, _active;
    private static ulong _activeEpoch;
    private static string _lastPublication;
    private static float _lastPublishedAt;
    private static int _received, _executed, _stale;
    internal static string StatusText { get; private set; } = "已关闭";
    internal static int RegisteredTargets => Bridge.RegisteredTargets;
    internal static string Counters => "收到 " + _received + "，执行 " + _executed + "，丢弃过期 " + _stale;

    internal static void Retry()
    {
        if (_active) Deactivate();
        Bridge.Shutdown();
        Gate.Reset();
        _attempted = false;
        StatusText = "等待初始化";
    }

    internal static void Tick()
    {
        if (!MusicBridgeOptions.Current.SystemMedia.Enabled)
        {
            if (_active) Deactivate();
            if (Bridge.Loaded) Bridge.Shutdown();
            _attempted = false;
            StatusText = "已关闭";
            return;
        }
        if (!_attempted)
        {
            _attempted = true;
            if (!Bridge.Initialize())
            { StatusText = "不可用：" + Bridge.Error; BridgeLog.Warn("系统媒体控制不可用：" + Bridge.Error); return; }
            StatusText = "桥接已加载，等待有效曲目";
        }
        if (!Bridge.Loaded) return;
        PlaybackSnapshot snapshot = PlaybackSnapshotService.Capture();
        bool owns = PlaybackCoordinator.UserHasChosen && snapshot.Owner == MusicProvider.Netease &&
            NeteaseRuntime.Context != null && NeteaseRuntime.Context.Active &&
            !string.IsNullOrEmpty(snapshot.TrackKey) && snapshot.State != PlaybackState.Failed &&
            snapshot.State != PlaybackState.Idle;
        if (!owns)
        {
            if (_active) Deactivate();
            StatusText = snapshot.Owner == MusicProvider.AppleMusic ? "Music.app 接管" : "等待网易云有效曲目";
            return;
        }
        string publication = snapshot.OwnerEpoch + "|" + snapshot.TrackKey + "|" + snapshot.Title + "|" +
            snapshot.Artist + "|" + snapshot.State + "|" + snapshot.DesiredPlaying + "|" +
            snapshot.CanNext + "|" + snapshot.CanPrevious + "|" + snapshot.CanSeek;
        if (!_active || publication != _lastPublication || Time.unscaledTime - _lastPublishedAt >= 5f)
        {
            if (Bridge.Publish(snapshot))
            {
                _active = true; _activeEpoch = (ulong)snapshot.OwnerEpoch;
                _lastPublication = publication; _lastPublishedAt = Time.unscaledTime;
            }
            else
            {
                StatusText = "发布失败：" + (Bridge.Error ?? "原生桥接拒绝元数据");
                Deactivate();
                return;
            }
        }
        StatusText = Bridge.RegisteredTargets == 6 ? "已启用 · 网易云" : "正在等待系统注册媒体命令";
        for (int i = 0; i < 16 && Bridge.Poll(out var command); i++)
        {
            _received++;
            PlaybackSnapshot current = PlaybackSnapshotService.Capture();
            if (current.Owner != MusicProvider.Netease || current.OwnerEpoch != (long)command.OwnerEpoch ||
                string.IsNullOrEmpty(current.TrackKey))
            { _stale++; continue; }
            var kind = (MediaCommandKind)command.Type;
            if (!Gate.Accept(command, _activeEpoch, MacSystemMediaBridge.TrackTokenOf(current.TrackKey), true))
            { _stale++; continue; }
            bool accepted = false;
            switch (kind)
            {
                case MediaCommandKind.Play: accepted = MusicTransport.EnsurePlaying(); break;
                case MediaCommandKind.Pause: accepted = MusicTransport.EnsurePaused(); break;
                case MediaCommandKind.Toggle:
                    accepted = current.DesiredPlaying ? MusicTransport.EnsurePaused() : MusicTransport.EnsurePlaying();
                    break;
                case MediaCommandKind.Next:
                    if (MusicTransport.CanNext) { MusicTransport.Next(); accepted = true; }
                    break;
                case MediaCommandKind.Previous:
                    if (MusicTransport.CanPrevious) { MusicTransport.Previous(); accepted = true; }
                    break;
                case MediaCommandKind.Seek:
                    if (current.CanSeek && !double.IsNaN(command.SeekSeconds) && !double.IsInfinity(command.SeekSeconds) &&
                        command.SeekSeconds >= 0 && command.SeekSeconds <= current.Duration)
                    { MusicTransport.SeekNormalized((float)(command.SeekSeconds / current.Duration)); accepted = true; }
                    break;
            }
            if (accepted) _executed++; else _stale++;
        }
    }

    private static void Deactivate()
    {
        if (_active) Bridge.Deactivate(_activeEpoch);
        _active = false; _activeEpoch = 0; _lastPublication = null;
    }

    internal static void Shutdown()
    {
        Deactivate(); Bridge.Shutdown(); _attempted = false;
        Gate.Reset();
        StatusText = "已关闭";
    }
}
