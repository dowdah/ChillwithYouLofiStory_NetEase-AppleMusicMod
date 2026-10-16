using System;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace MusicBridge;

// All capture and Steam calls occur on the existing Unity main-thread dispatcher.
internal static class SteamPresenceService
{
    private static SteamPresencePublisher _publisher;
    private static SteamPresenceSettingsStore _store;
    private static SteamPresenceOptions _options = new SteamPresenceOptions();
    private static readonly TimerPresenceText TimerText = new TimerPresenceText();
    private static readonly SteamPresenceApi CapabilityApi = new SteamPresenceApi();
    private static double _nextSample;
    private static bool _initialized, _quitting;
    private static PlaybackSnapshot _music;
    private static TimerSnapshot _timer;
    private static string _timerText, _error, _initializationError, _steamReady = "尚未检查";
    private static DateTime? _lastSuccessUtc;
    private static int _publishCount;
    internal static bool Enabled => _options.Enabled;
    internal static SteamPresenceSettingsStore Store => _store;
    internal static string Diagnostics => "协议：status 纯文本，AppID 3548580；模板：不使用（详情通路已实测）\n" +
        "Steam：" + _steamReady + "；" + (_publisher?.Status ?? "未初始化") + "\n" +
        TimerSnapshotService.Capability + "\n" + TimerDiagnostic + "\n" +
        "发布次数：" + (_publisher?.PublishCount ?? 0) + "；最后成功：" +
        (_lastSuccessUtc?.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? "无") + "\n" +
        "最近错误：" + (_error ?? _initializationError ?? _publisher?.LastError ?? "无") +
        (string.IsNullOrEmpty(_store?.Error) ? "" : "\n" + _store.Error);
    private static string TimerDiagnostic => !_timer.Available ? "当前计时器：不可用/等待场景实例" :
        "当前计时器：" + _timer.Mode + "/" + _timer.State + "/" + _timer.Phase +
        "；已过秒：" + _timer.ElapsedSeconds.ToString("0.000", CultureInfo.InvariantCulture) +
        "；剩余秒：" + (_timer.RemainingSeconds?.ToString("0.000", CultureInfo.InvariantCulture) ?? "无");

    internal static void Initialize(Harmony harmony)
    {
        if (_initialized || _quitting) return;
        _initialized = true;
        try
        {
            _publisher = new SteamPresencePublisher(new SteamPresenceApi(), () => Time.realtimeSinceStartupAsDouble);
            _store = new SteamPresenceSettingsStore(BridgePaths.Resolve("config", "steam-presence.options.json"));
            Apply(_store.Read());
        }
        catch (Exception ex) { _options = new SteamPresenceOptions(); _initializationError = "初始化失败（" + ex.GetType().Name + "）"; }
        try { TimerSnapshotService.Initialize(harmony); }
        catch (Exception ex) { _error = "计时器初始化失败（" + ex.GetType().Name + "）"; }
    }

    internal static void Apply(SteamPresenceOptions options)
    {
        if (_quitting) return;
        _options = options?.Clone() ?? new SteamPresenceOptions();
        try { _publisher?.SetEnabled(_options.Enabled); } // Disabling attempts conditional cleanup immediately.
        catch (Exception ex) { _error = "应用设置失败（" + ex.GetType().Name + "）"; }
        _nextSample = 0;
    }

    internal static void Tick()
    {
        if (!_initialized || _quitting) return;
        try
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now < _nextSample) return;
            _nextSample = now + 1;
            _error = null;
            try { _music = PlaybackSnapshotService.Capture(); }
            catch (Exception ex) { _music = default; _error = "音乐读取失败（" + ex.GetType().Name + "）"; }
            try { _timer = TimerSnapshotService.Capture(now); }
            catch (Exception ex) { _timer = default; _error = "计时器读取失败（" + ex.GetType().Name + "）"; }
            _timerText = TimerText.Format(_timer, now);
            _steamReady = CapabilityApi.TryConnect(out _, out string reason) ? "已就绪" : reason;
            _publisher?.Tick(Format(_options)); // Also tick while disabled for pending conditional cleanup.
            if (_publisher != null && _publisher.PublishCount != _publishCount)
            { _publishCount = _publisher.PublishCount; _lastSuccessUtc = DateTime.UtcNow; }
        }
        catch (Exception ex) { _error = "状态模块异常（" + ex.GetType().Name + "）"; }
    }

    private static string Format(SteamPresenceOptions options) => SteamPresenceFormatter.Format(new PresenceTextState(
        _music.State, _music.Title, options.ShowTimer ? _timerText : null, options.ShowMusic, options.ShareTrackTitle, _music.TrackArtist));

    internal static string Preview(SteamPresenceOptions draft)
    {
        if (draft == null) return "预览不可用";
        string text = Format(draft) ?? "无可展示内容";
        return (draft.Enabled ? "保存后将按此内容发布：\n" : "功能关闭；仅本机预览：\n") + text;
    }

    internal static void Retry()
    {
        if (_quitting) return;
        _publisher?.Retry(); _nextSample = 0;
    }
    internal static void Shutdown()
    {
        if (_quitting) return;
        _quitting = true;
        try { _publisher?.Shutdown(); } catch { }
        try { TimerSnapshotService.Shutdown(); } catch { }
        TimerText.Reset(); _timer = default; _music = default; _timerText = null;
    }
}
