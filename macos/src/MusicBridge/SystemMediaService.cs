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
    private static int _executed, _stale;
    private static readonly double[] CommandLatencyMs = new double[64];
    private static int _latencyCount, _latencyCursor;
    internal static string StatusText { get; private set; } = "已关闭";
    internal static int RegisteredTargets => Bridge.RegisteredTargets;
    internal static string BridgeVersion => Bridge.LoadedAbi == 0 ? "未加载" : "ABI " + Bridge.LoadedAbi;
    internal static string RecentError => Bridge.Error ?? "无";
    internal static string PublishedSource => _active ? "网易云" : "无";
    internal static NativeMediaDiagnostics NativeCounters => Bridge.Diagnostics;
    internal static int ExecutedCount => _executed;
    internal static int StaleDroppedCount => _stale;
    internal static int LatencySampleCount => _latencyCount;
    internal static double AcceptedLatencyP95Ms => _latencyCount < 30 ? double.NaN : LatencyP95();
    internal static string Counters
    {
        get
        {
            NativeMediaDiagnostics native = Bridge.Diagnostics;
            string latency = _latencyCount < 30 ? "样本不足" : LatencyP95().ToString("0") + " ms";
            return "原生收到 " + native.Received + "，入队 " + native.Accepted + "，拒绝 " +
                native.Rejected + "，队列 " + native.QueueDepth + "；主线程执行 " + _executed +
                "，丢弃 " + _stale + "；命令受理P95 " + latency;
        }
    }

    private static double LatencyP95()
    {
        double[] samples = new double[_latencyCount];
        Array.Copy(CommandLatencyMs, samples, _latencyCount);
        Array.Sort(samples);
        return samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
    }

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
            if (accepted)
            {
                _executed++;
                CommandLatencyMs[_latencyCursor] = command.AgeSeconds * 1000;
                _latencyCursor = (_latencyCursor + 1) % CommandLatencyMs.Length;
                if (_latencyCount < CommandLatencyMs.Length) _latencyCount++;
            }
            else _stale++;
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
