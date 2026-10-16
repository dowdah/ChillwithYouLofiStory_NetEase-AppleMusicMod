using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MusicBridge;

internal sealed class SteamPresenceSettingsDraft
{
    internal readonly SteamPresenceOptions Original;
    internal readonly string Revision;
    internal SteamPresenceOptions Working;
    internal bool Dirty => !Working.SameAs(Original);
    internal SteamPresenceSettingsDraft(SteamPresenceOptions current, string revision)
    { Original = current.Clone(); Working = current.Clone(); Revision = revision; }
    internal void ResetDefaults() { Working = new SteamPresenceOptions(); }
}

internal sealed class SteamPresenceSettingsSaveResult
{
    internal bool Success;
    internal string Error, Revision;
    internal SteamPresenceOptions Options;
}

internal sealed class SteamPresenceSettingsStore
{
    private readonly object _gate = new object();
    private readonly Action<string, string> _write;
    private string _revision = "missing";
    private SteamPresenceOptions _current = new SteamPresenceOptions();
    internal string Path { get; }
    internal bool CanSave { get; private set; } = true;
    internal string Error { get; private set; } = "";
    internal SteamPresenceOptions Current { get { lock (_gate) return _current.Clone(); } }

    internal SteamPresenceSettingsStore(string path, Action<string, string> write = null)
    {
        Path = BridgePaths.ValidateWritePath(path);
        _write = write ?? AtomicFile.WriteAllText;
    }

    internal SteamPresenceOptions Read()
    {
        lock (_gate)
        {
            try
            {
                string original = ReadText();
                _current = original == null ? new SteamPresenceOptions() : SteamPresenceOptions.Parse(original);
                _revision = RevisionOf(original);
                CanSave = true; Error = "";
            }
            catch (Exception ex)
            {
                _current = new SteamPresenceOptions();
                CanSave = false;
                string reason = ex is InvalidDataException ? ex.Message : ex.GetType().Name;
                Error = "Steam 状态配置未加载，功能已关闭；原文件保留，请手动修复后重新打开设置（" + reason + "）";
            }
            return _current.Clone();
        }
    }

    internal SteamPresenceSettingsDraft Capture()
    { lock (_gate) return new SteamPresenceSettingsDraft(_current, _revision); }

    internal bool IsCurrent(SteamPresenceSettingsSaveResult result)
    { lock (_gate) return result != null && result.Success && result.Revision == _revision; }

    // Caller runs on a worker and publishes Options on Unity's main thread after success.
    internal SteamPresenceSettingsSaveResult Save(SteamPresenceSettingsDraft baseline, SteamPresenceOptions working)
    {
        lock (_gate)
        {
            var result = new SteamPresenceSettingsSaveResult();
            if (!CanSave) { result.Error = Error; return result; }
            if (baseline == null || working == null) { result.Error = "缺少 Steam 状态设置草稿"; return result; }
            try
            {
                string original = ReadText();
                if (RevisionOf(original) != baseline.Revision || _revision != baseline.Revision)
                { result.Error = "Steam 状态配置已在别处修改，请取消并重新打开设置"; return result; }
                if (original != null) SteamPresenceOptions.Parse(original);
                string saved = working.Serialize();
                var validated = SteamPresenceOptions.Parse(saved);
                _write(Path, saved);
                _revision = RevisionOf(saved);
                _current = validated.Clone();
                result.Success = true; result.Options = validated; result.Revision = _revision;
            }
            catch (Exception ex)
            {
                result.Error = "Steam 状态设置未保存，原配置和运行设置保持不变（" + ex.GetType().Name + "）";
            }
            return result;
        }
    }

    private string ReadText()
    {
        BridgePaths.ValidateWritePath(Path);
        // File.Exists hides access failures; only a genuinely missing path means defaults.
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 16384) throw new InvalidDataException("配置文件过大");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string RevisionOf(string value)
    {
        if (value == null) return "missing";
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
    }
}
