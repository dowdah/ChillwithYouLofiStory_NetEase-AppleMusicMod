using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace MusicBridge;

internal sealed class SteamPresenceProbeProfile
{
    internal readonly string Token, Parameter, Evidence;
    internal SteamPresenceProbeProfile(string token, string parameter, string evidence)
    {
        if (!Regex.IsMatch(token ?? "", @"\A#[A-Za-z0-9_]{1,62}\z") ||
            !Regex.IsMatch(parameter ?? "", @"\A[A-Za-z0-9_:]{1,63}\z") ||
            parameter.StartsWith("steam_", StringComparison.OrdinalIgnoreCase) ||
            parameter == "connect" || string.IsNullOrWhiteSpace(evidence))
            throw new ArgumentException("A source-backed display token and text parameter are required");
        Token = token; Parameter = parameter; Evidence = evidence;
    }
}

internal static class SteamPresenceProbeCatalog
{
    // Deliberately empty: no AppID 3548580 template has been established.
    // Add only a reviewed token/parameter pair with a cited source; never borrow another AppID's tokens.
    internal static readonly SteamPresenceProbeProfile[] Profiles = Array.Empty<SteamPresenceProbeProfile>();
}

// Main-thread, manually operated capability probe. No music data, config writes or automatic token guessing.
internal sealed class SteamPresenceProbe
{
    internal const string TextA = "MusicBridge 测试 A";
    internal const string TextB = "MusicBridge 测试 B";
    private readonly ISteamPresenceApi _api;
    private readonly SteamPresenceProbeProfile[] _profiles;
    private readonly Dictionary<string, string> _original = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owned = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uncertainPrevious = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly List<string> _writeOrder = new List<string>();
    private string _session;
    private bool _stopping;
    private double _deadline, _retryAt;
    internal string Status { get; private set; } = "尚未检查；未发布任何状态";
    internal string Observation { get; private set; } = "点击只读检查以查看当前能力";
    internal bool Active => _owned.Count > 0;
    internal bool CanPublish => _profiles.Length == 1 && !_stopping;
    internal string TemplateStatus => _profiles.Length == 0 ? "未确认本游戏模板，发布已禁用" :
        _profiles.Length == 1 ? "已录入来源；好友端显示仍待验收" : "多个候选尚未确定，发布已禁用";

    internal SteamPresenceProbe(ISteamPresenceApi api, SteamPresenceProbeProfile[] profiles)
    { _api = api; _profiles = (SteamPresenceProbeProfile[])profiles.Clone(); }

    internal void Inspect()
    {
        try
        {
            if (!_api.TryConnect(out _, out string error)) { Status = error; return; }
            var values = _api.ReadOwnPresence();
            string token = Value(values, "steam_display");
            string safeToken = Regex.IsMatch(token ?? "", @"\A#[A-Za-z0-9_]{1,62}\z") ? token : "无或已隐藏";
            // Show safe names and the localization token only, never arbitrary values or user identity.
            string names = string.Join(", ", values.Keys.Where(k => Regex.IsMatch(k, @"\A[A-Za-z0-9_:]{1,63}\z"))
                .OrderBy(k => k, StringComparer.Ordinal).Take(30));
            Observation = "AppID 3548580；键数 " + values.Count + "；steam_display=" + safeToken +
                "；键名：" + (names.Length == 0 ? "无" : names);
            Status = Active ? "只读检查完成；测试仍在进行" : "只读检查完成；未写入 Presence";
        }
        catch (Exception ex) { Status = "读取失败（" + SteamPresenceApi.ErrorKind(ex) + "）"; }
    }

    internal void Publish(bool second, double now)
    {
        if (!CanPublish) { Status = TemplateStatus + "；不会试写未知 token"; return; }
        var profile = _profiles[0];
        try
        {
            if (!_api.TryConnect(out string session, out string error)) { Status = error; return; }
            if (Active && session != _session)
            {
                // Never restore one user's values into another user's Steam context.
                Forget(); Status = "Steam 会话变化；放弃旧会话恢复，本次未发布"; return;
            }
            var before = _api.ReadOwnPresence();
            if (Active && _owned.Any(pair => Value(before, pair.Key) != pair.Value))
            { BeginStop(now); Status = "检测到外部改写，测试已停止；仅恢复仍持有的键"; return; }
            _session = session;
            string text = second ? TextB : TextA;
            // Parameters first, display token last. Record intent before write so uncertain writes can be recovered.
            Write(profile.Parameter, text, before);
            Write("steam_display", profile.Token, before);
            var after = _api.ReadOwnPresence();
            if (Value(after, profile.Parameter) != text || Value(after, "steam_display") != profile.Token)
                throw new InvalidOperationException("Readback mismatch");
            _deadline = now + 120;
            Status = "API 返回 true；本机读回一致（" + (second ? "B" : "A") + "）。好友列表仍待确认；120 秒后自动恢复";
        }
        catch (Exception ex)
        {
            string reason = SteamPresenceApi.ErrorKind(ex);
            BeginStop(now);
            Status = "测试写入失败（" + reason + "）；" + (Active ? "恢复待重试" : "已完成有条件恢复");
        }
    }

    private void Write(string key, string value, Dictionary<string, string> before)
    {
        if (!_original.ContainsKey(key)) _original[key] = Value(before, key);
        string previousOwned = _owned.TryGetValue(key, out string owned) ? owned : null;
        if (previousOwned != null) _uncertainPrevious[key] = previousOwned;
        _owned[key] = value;
        if (!_writeOrder.Contains(key)) _writeOrder.Add(key);
        if (!_api.Set(key, value)) throw new InvalidOperationException("SetRichPresence returned false");
        _uncertainPrevious.Remove(key);
    }

    internal void Stop(double now)
    {
        if (!Active) { Status = "没有待恢复的测试状态"; return; }
        BeginStop(now);
        Status = Active ? "Steam 暂不可用或恢复失败；将在主线程重试" : "测试结束；已恢复仍持有的键，外部改写保持原样";
    }

    private void BeginStop(double now) { _stopping = true; _retryAt = now + 5; Restore(); }

    private void Restore()
    {
        if (!Active) { Forget(); return; }
        try
        {
            if (!_api.TryConnect(out string session, out _)) return;
            if (session != _session) { Forget(); Status = "Steam 会话变化；未向新会话写入旧值"; return; }
            for (int i = _writeOrder.Count - 1; i >= 0; i--)
            {
                string key = _writeOrder[i];
                if (!_owned.TryGetValue(key, out string expected)) continue;
                // Check immediately before each write; Steam offers no atomic compare-and-set.
                string current = Value(_api.ReadOwnPresence(), key);
                bool previous = _uncertainPrevious.TryGetValue(key, out string oldOwned) && current == oldOwned;
                if (current != expected && !previous) { _owned.Remove(key); continue; }
                string original = _original[key];
                if (!_api.Set(key, original) || Value(_api.ReadOwnPresence(), key) != original) return;
                _owned.Remove(key);
            }
            if (!Active) Forget();
        }
        catch { /* Retain pending entries for a later main-thread retry. No raw exception data. */ }
    }

    internal void Tick(double now)
    {
        if (!Active) return;
        if (!_stopping && now >= _deadline) Stop(now);
        else if (_stopping && now >= _retryAt)
        {
            _retryAt = now + 5; Restore();
            if (!Active) Status = "测试恢复已完成；未覆盖检测到的外部改写";
        }
    }

    internal void Shutdown()
    {
        Restore();
        Status = Active ? "退出时 Steam 不可用，恢复未确认；不再重试" : "退出清理完成";
        Forget();
    }
    private void Forget() { _owned.Clear(); _uncertainPrevious.Clear(); _original.Clear(); _writeOrder.Clear(); _session = null; _stopping = false; }
    private static string Value(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string value) && !string.IsNullOrEmpty(value) ? value : null;
}
