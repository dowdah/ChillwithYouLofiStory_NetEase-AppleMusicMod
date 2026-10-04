using System;
using System.IO;
using System.Threading;
using MusicBridge;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

internal static class SettingsTests
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

    public static void Run()
    {
        string path = BridgePaths.Resolve("test-artifacts", "settings-" + Guid.NewGuid().ToString("N") + ".json");
        string backup = path + ".before-v2";
        try
        {
            var v1 = JObject.FromObject(new MusicBridgeOptions());
            v1["SchemaVersion"] = 1;
            ((JObject)v1["Netease"]).Remove("NoRepeatShuffle");
            v1.Remove("Overlay"); v1.Remove("SystemMedia");
            ((JObject)v1["Netease"])["NextAudioPreload"] = false;
            string original = v1.ToString(Formatting.Indented);
            AtomicFile.WriteAllText(path, original);
            var store = new SettingsStore(path);
            var migrated = store.Read(out bool exists);
            Check(exists && migrated.SchemaVersion == 2 && migrated.Netease.NoRepeatShuffle &&
                !migrated.Netease.NextAudioPreload, "V1 migrates in memory and retains user preference");
            Check(File.ReadAllText(path) == original && !File.Exists(backup), "load does not write V1");
            var baseline = store.Capture(migrated);
            var saved = store.SavePatch(baseline, JObject.Parse("{Netease:{PreferredQuality:1000}}"));
            Check(saved.Success && File.ReadAllText(backup) == original &&
                JObject.Parse(File.ReadAllText(path)).Value<int>("SchemaVersion") == 2,
                "first V2 save backs up original V1 without changing it");

            var draft = store.Capture(saved.Options);
            var external = JObject.Parse(File.ReadAllText(path));
            ((JObject)external["Netease"])["RepeatQueue"] = false;
            AtomicFile.WriteAllText(path, external.ToString(Formatting.Indented));
            var merged = store.SavePatch(draft, JObject.Parse("{Netease:{NextAudioPreload:true}}"));
            Check(merged.Success && !merged.Options.Netease.RepeatQueue && merged.Options.Netease.NextAudioPreload,
                "unrelated external change merges with field patch");
            Check(!store.IsCurrent(saved) && store.IsCurrent(merged),
                "late callback from an older settings revision cannot publish over the newer save");

            draft = store.Capture(merged.Options);
            external = JObject.Parse(File.ReadAllText(path));
            ((JObject)external["Netease"])["PreferredQuality"] = 320;
            AtomicFile.WriteAllText(path, external.ToString(Formatting.Indented));
            string unchanged = File.ReadAllText(path);
            var conflict = store.SavePatch(draft, JObject.Parse("{Netease:{PreferredQuality:2000}}"));
            Check(!conflict.Success && conflict.Error.Contains("PreferredQuality") && File.ReadAllText(path) == unchanged,
                "same-field external change is a visible conflict");

            external["UnknownFutureOption"] = 7;
            AtomicFile.WriteAllText(path, external.ToString(Formatting.Indented));
            unchanged = File.ReadAllText(path);
            Check(!store.SavePatch(draft, JObject.Parse("{Netease:{RepeatQueue:true}}")).Success &&
                File.ReadAllText(path) == unchanged, "unknown field cannot be silently deleted");

            external.Remove("UnknownFutureOption"); external["SchemaVersion"] = 99;
            AtomicFile.WriteAllText(path, external.ToString(Formatting.Indented));
            unchanged = File.ReadAllText(path);
            Check(!store.SavePatch(draft, JObject.Parse("{Netease:{RepeatQueue:true}}")).Success &&
                File.ReadAllText(path) == unchanged, "future schema cannot be downgraded");

            using var completed = new ManualResetEventSlim(false);
            SettingsSaveResult repaired = null;
            store.RestoreDefaultsAsync(result => { repaired = result; completed.Set(); });
            Check(completed.Wait(5000) && repaired.Success &&
                JObject.Parse(File.ReadAllText(path)).Value<int>("SchemaVersion") == 2 &&
                Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".before-explicit-repair-*").Length == 1,
                "explicit repair backs up future config before writing defaults");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(backup)) File.Delete(backup);
            foreach (var repair in Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".before-explicit-repair-*"))
                File.Delete(repair);
        }
    }
}
