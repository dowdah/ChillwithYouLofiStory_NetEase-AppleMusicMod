using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MusicBridge;

internal sealed class SettingsDraft
{
    public readonly JObject Json;
    public readonly string Revision;
    public SettingsDraft(JObject json, string revision) { Json = json; Revision = revision; }
}

internal sealed class SettingsSaveResult
{
    public bool Success;
    public string Error;
    public string Revision;
    public MusicBridgeOptions Options;
}

// All writes to the main options file, including the quick quality buttons, pass here.
internal sealed class SettingsStore
{
    private readonly object _gate = new object();
    private volatile string _revision = "missing";
    private long _writeFailures, _conflicts;
    internal string Revision => _revision;
    internal bool IsCurrent(SettingsSaveResult result) => result != null && result.Success &&
        result.Revision == _revision;
    internal long WriteFailures => Interlocked.Read(ref _writeFailures);
    internal long Conflicts => Interlocked.Read(ref _conflicts);
    public string Path { get; }
    public SettingsStore(string path) { Path = BridgePaths.ValidateWritePath(path); }

    private static string RevisionOf(string text)
    {
        if (text == null) return "missing";
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "");
    }

    public MusicBridgeOptions Read(out bool exists)
    {
        lock (_gate)
        {
            exists = File.Exists(Path);
            string original = exists ? File.ReadAllText(Path) : null;
            var value = original == null ? new MusicBridgeOptions() : MusicBridgeOptions.Parse(original);
            _revision = RevisionOf(original);
            return value;
        }
    }

    public SettingsDraft Capture(MusicBridgeOptions current)
    {
        lock (_gate) return new SettingsDraft(JObject.FromObject(current), _revision);
    }

    public void SavePatchAsync(SettingsDraft baseline, JObject patch, Action<SettingsSaveResult> completed)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            SettingsSaveResult result = SavePatch(baseline, patch);
            MainThreadDispatcher.Enqueue(() => completed?.Invoke(result));
        });
    }

    internal void RestoreDefaultsAsync(Action<SettingsSaveResult> completed)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var result = new SettingsSaveResult();
            lock (_gate)
            {
                try
                {
                    if (File.Exists(Path))
                    {
                        string backup = BridgePaths.ValidateWritePath(Path + ".before-explicit-repair-" +
                            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
                        using (var source = File.OpenRead(Path))
                        using (var target = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { source.CopyTo(target); target.Flush(true); }
                    }
                    var defaults = new MusicBridgeOptions();
                    string saved = JObject.FromObject(defaults).ToString(Formatting.Indented);
                    AtomicFile.WriteAllText(Path, saved);
                    _revision = RevisionOf(saved);
                    result.Success = true; result.Options = defaults; result.Revision = _revision;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _writeFailures);
                    result.Error = "显式恢复失败，原配置已保留（" + ex.GetType().Name + "）";
                }
            }
            MainThreadDispatcher.Enqueue(() => completed?.Invoke(result));
        });
    }

    // Also used by the isolated settings tests; production invokes it only on a worker.
    internal SettingsSaveResult SavePatch(SettingsDraft baseline, JObject patch)
    {
        var result = new SettingsSaveResult();
        lock (_gate)
        {
            try
            {
                if (baseline == null || patch == null) throw new InvalidDataException("缺少设置草稿");
                byte[] originalBytes = File.Exists(Path) ? File.ReadAllBytes(Path) : null;
                string original = null;
                if (originalBytes != null)
                {
                    using var stream = new MemoryStream(originalBytes);
                    using var reader = new StreamReader(stream, Encoding.UTF8, true);
                    original = reader.ReadToEnd();
                }
                var disk = original == null ? new MusicBridgeOptions() : MusicBridgeOptions.Parse(original);
                JObject current = JObject.FromObject(disk);
                var leaves = new List<KeyValuePair<string, JToken>>();
                Flatten(patch, "", leaves);
                if (leaves.Count == 0) throw new InvalidDataException("没有可保存的设置");
                foreach (var leaf in leaves)
                {
                    JToken before = baseline.Json.SelectToken(leaf.Key);
                    JToken now = current.SelectToken(leaf.Key);
                    if (before == null || now == null) throw new InvalidDataException("未知设置：" + leaf.Key);
                    if (!JToken.DeepEquals(before, now) && !JToken.DeepEquals(now, leaf.Value))
                        throw new InvalidDataException("设置冲突：" + leaf.Key + " 已在别处修改，请重新打开设置");
                    SetPath(current, leaf.Key, leaf.Value);
                }
                current["SchemaVersion"] = MusicBridgeOptions.CurrentSchemaVersion;
                MusicBridgeOptions candidate = MusicBridgeOptions.Parse(current.ToString(Formatting.None));
                if (original != null && JObject.Parse(original).Value<int?>("SchemaVersion") == 1)
                    BackupV1(originalBytes);
                string saved = current.ToString(Formatting.Indented);
                AtomicFile.WriteAllText(Path, saved);
                _revision = RevisionOf(saved);
                result.Success = true; result.Options = candidate; result.Revision = _revision;
            }
            catch (Exception ex)
            {
                if (ex is InvalidDataException && ex.Message.StartsWith("设置冲突：", StringComparison.Ordinal))
                    Interlocked.Increment(ref _conflicts);
                else Interlocked.Increment(ref _writeFailures);
                result.Error = ex is InvalidDataException ? ex.Message : "设置写入失败，原配置已保留（" + ex.GetType().Name + "）";
            }
        }
        return result;
    }

    private void BackupV1(byte[] original)
    {
        string backup = BridgePaths.ValidateWritePath(Path + ".before-v2");
        if (File.Exists(backup))
        {
            if (!File.ReadAllBytes(backup).SequenceEqual(original))
                throw new InvalidDataException("已有不同内容的V1备份，拒绝覆盖或继续迁移");
            return;
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
        bool created = false;
        try
        {
            using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            stream.Write(original, 0, original.Length);
            stream.Flush(true);
        }
        catch
        {
            // A partially written backup cannot authorize a later V2 commit.
            if (created) try { File.Delete(backup); } catch { }
            throw;
        }
    }

    private static void Flatten(JObject patch, string prefix, List<KeyValuePair<string, JToken>> leaves)
    {
        foreach (var property in patch.Properties())
        {
            string name = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            if (property.Value is JObject child) Flatten(child, name, leaves);
            else leaves.Add(new KeyValuePair<string, JToken>(name, property.Value));
        }
    }

    private static void SetPath(JObject json, string path, JToken value)
    {
        var parts = path.Split('.');
        JObject node = json;
        for (int i = 0; i < parts.Length - 1; i++) node = (JObject)node[parts[i]];
        node[parts[parts.Length - 1]] = value.DeepClone();
    }
}
