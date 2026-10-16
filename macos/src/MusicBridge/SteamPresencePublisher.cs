using System;
using System.Collections.Generic;

namespace MusicBridge;

// Unity-independent, main-thread-only transaction and scheduling core. It owns exactly one key: status.
internal sealed class SteamPresencePublisher
{
    private readonly ISteamPresenceApi _api;
    private readonly Func<double> _clock;
    private string _session, _original, _owned, _previousOwned, _desired;
    private bool _hasOwnership, _cleanup, _shutdown, _wasConnected, _republish;
    private double _nextAttempt, _lastPublish = double.NegativeInfinity;
    private int _failures;
    internal bool Enabled { get; private set; }
    internal bool Conflict { get; private set; }
    internal bool Ready { get; private set; }
    internal bool HasOwnership => _hasOwnership;
    internal bool CleanupPending => _cleanup && _hasOwnership;
    internal int PublishCount { get; private set; }
    internal double? LastSuccessAt { get; private set; }
    internal string LastError { get; private set; }
    internal string Status { get; private set; } = "已关闭";

    internal SteamPresencePublisher(ISteamPresenceApi api, Func<double> clock)
    { _api = api ?? throw new ArgumentNullException(nameof(api)); _clock = clock ?? throw new ArgumentNullException(nameof(clock)); }

    internal void SetEnabled(bool enabled)
    {
        if (_shutdown || Enabled == enabled) return;
        Enabled = enabled;
        if (!enabled)
        {
            _desired = null; _cleanup = _hasOwnership;
            Restore(_clock());
            if (!_hasOwnership) Status = Conflict ? "检测到外部接管；等待手动重试" : "已关闭";
        }
        else { _nextAttempt = 0; Status = Conflict ? "检测到外部接管；等待手动重试" : "等待采样"; }
    }

    internal void Tick(string desiredText)
    {
        if (_shutdown) return;
        double now = _clock();
        string safeText = SteamPresenceFormatter.Sanitize(desiredText);
        _desired = safeText.Length == 0 ? null : SteamPresenceFormatter.Truncate(safeText, SteamPresenceFormatter.MaxUtf8Bytes);
        if (_cleanup)
        {
            if (now >= _nextAttempt) Restore(now);
            if (_cleanup) return;
        }
        if (!Enabled || Conflict) return;
        if (_desired == null)
        {
            _cleanup = _hasOwnership;
            if (_cleanup) Restore(now);
            if (!_hasOwnership) Status = "没有可展示内容";
            return;
        }
        if (now < _nextAttempt) return;
        try
        {
            if (!Connect(now, out _)) return;
            var values = _api.ReadOwnPresence();
            string current = Value(values, "status");
            if (Value(values, "steam_display") != null ||
                (_hasOwnership && current != _owned && !(_republish && current == null)))
            { EnterConflict(now); return; }
            if (_hasOwnership && current == _desired && !_republish) return;
            if (now - _lastPublish < 5) return;
            if (!_hasOwnership)
            { _original = current; _hasOwnership = true; }
            _previousOwned = _owned;
            _owned = _desired; // Record intent before calling: false/throw can still mean a partial write.
            _lastPublish = now;
            if (!_api.Set("status", _owned)) throw new InvalidOperationException("Steam write rejected");
            var after = _api.ReadOwnPresence();
            if (Value(after, "steam_display") != null || Value(after, "status") != _owned)
            { EnterConflict(now); return; }
            _previousOwned = null; _republish = false;
            _failures = 0; _nextAttempt = 0; LastError = null;
            PublishCount++; LastSuccessAt = now;
            Status = "API 已提交（仅游戏信息详情；不代表好友端已收到）";
        }
        catch (Exception ex)
        {
            Fail(now, "发布失败（" + SteamPresenceApi.ErrorKind(ex) + "）");
            _cleanup = _hasOwnership;
            // Roll back immediately if possible; any failed recovery retains the retry schedule.
            Restore(now, true);
        }
    }

    internal void Retry()
    {
        if (_shutdown) return;
        Conflict = false; _failures = 0; _nextAttempt = 0;
        _wasConnected = false;
        Status = _cleanup ? "等待恢复后重试" : Enabled ? "等待采样" : "已关闭";
    }

    internal void Shutdown()
    {
        if (_shutdown) return;
        Enabled = false; _desired = null; _cleanup = _hasOwnership;
        Restore(_clock());
        _shutdown = true;
        Status = _hasOwnership ? "退出时恢复未确认；不再发布" : "退出清理完成";
    }

    private bool Connect(double now, out bool reconnected)
    {
        reconnected = false;
        if (!_api.TryConnect(out string session, out string error))
        {
            Ready = false; _wasConnected = false;
            // Adapter supplies only bounded capability descriptions, no identifiers or raw exception text.
            Fail(now, error ?? "Steam 暂不可用"); return false;
        }
        Ready = true;
        reconnected = !_wasConnected;
        _wasConnected = true;
        if (_session != null && _session != session)
        {
            Forget(); // Never copy the old user's original value into a new context.
            reconnected = true;
        }
        _session = session;
        _republish |= reconnected;
        return true;
    }

    private void EnterConflict(double now)
    {
        Conflict = true; _cleanup = _hasOwnership;
        LastError = "检测到原游戏或其他模块接管 Presence";
        Restore(now);
        Status = "检测到外部接管；停止竞争，等待手动重试";
    }

    private void Restore(double now, bool preserveFailureSchedule = false)
    {
        if (!_hasOwnership) { _cleanup = false; return; }
        double savedNext = _nextAttempt;
        int savedFailures = _failures;
        try
        {
            if (!Connect(now, out _)) return;
            if (!_hasOwnership) return; // Session changed.
            string current = Value(_api.ReadOwnPresence(), "status");
            if (current == _original) { Forget(); return; }
            if (current != _owned && (_previousOwned == null || current != _previousOwned))
            { Forget(); return; } // Preserve external takeover.
            if (!_api.Set("status", _original)) throw new InvalidOperationException("Steam restore rejected");
            if (Value(_api.ReadOwnPresence(), "status") != _original)
                throw new InvalidOperationException("Steam restore readback mismatch");
            Forget();
            Status = Enabled ? "已释放本模块状态" : "已关闭；已恢复原状态";
        }
        catch (Exception ex) { Fail(now, "恢复待重试（" + SteamPresenceApi.ErrorKind(ex) + "）"); }
        finally
        {
            if (preserveFailureSchedule)
            { _nextAttempt = savedNext; _failures = savedFailures; }
        }
    }

    private void Forget()
    {
        _hasOwnership = false; _cleanup = false; _original = _owned = _previousOwned = null;
    }

    private void Fail(double now, string error)
    {
        _failures = Math.Min(4, _failures + 1);
        _nextAttempt = now + (_failures == 1 ? 5 : _failures == 2 ? 15 : _failures == 3 ? 30 : 60);
        LastError = error; Status = error;
    }
    private static string Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : null;
}
