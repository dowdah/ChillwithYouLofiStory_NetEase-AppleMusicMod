using System;
using System.IO;
using MusicBridge;
using Newtonsoft.Json.Linq;

internal static class Program
{
    private static int _count;
    private static void Check(bool condition, string label)
    { if (!condition) throw new Exception("FAIL: " + label); _count++; Console.WriteLine("PASS: " + label); }
    private static void Reject(string json, string label)
    {
        bool rejected = false;
        try { SteamPresenceOptions.Parse(json); } catch { rejected = true; }
        Check(rejected, label);
    }

    private static void Main()
    {
        var defaults = new SteamPresenceOptions();
        Check(!defaults.Enabled && defaults.ShowMusic && defaults.ShowTimer && !defaults.ShareTrackTitle,
            "default publishing and track title are off; music and timer sources on");
        Check(SteamPresenceOptions.Parse(defaults.Serialize()).SameAs(defaults), "schema1 exact roundtrip");
        Check(!SteamPresenceOptions.Parse("{\"SchemaVersion\":1}").Enabled, "missing boolean fields retain safe defaults");
        Reject("{}", "schema required");
        Reject("{\"SchemaVersion\":2}", "future schema rejected");
        Reject("{\"SchemaVersion\":\"1\"}", "string schema rejected");
        Reject("{\"SchemaVersion\":1.0}", "floating schema rejected");
        Reject("{\"SchemaVersion\":1,\"SecretField\":true}", "unknown field rejected");
        Reject("{\"SchemaVersion\":1,\"Enabled\":\"true\"}", "string bool rejected");
        Reject("{\"SchemaVersion\":1,\"Enabled\":1}", "number bool rejected");
        Reject("{\"SchemaVersion\":1,\"Enabled\":null}", "null bool rejected");
        Reject("{\"SchemaVersion\":1,\"Enabled\":{}}", "object bool rejected");
        Reject("{\"SchemaVersion\":1,\"Enabled\":true,\"Enabled\":false}", "duplicate field rejected");
        Reject("{\"SchemaVersion\":1} {}", "trailing JSON rejected");
        Reject("{\"SchemaVersion\":1", "truncated JSON rejected");
        string directory = BridgePaths.Resolve("presence-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "config", "steam-presence.options.json");
        try
        {
            var store = new SteamPresenceSettingsStore(path);
            Check(store.Read().SameAs(defaults) && store.CanSave && !File.Exists(path), "absent config read does not write");
            var canceled = store.Capture(); canceled.Working.Enabled = true;
            Check(!store.Current.Enabled && !File.Exists(path), "editing/canceling draft never changes applied settings or disk");
            canceled.ResetDefaults(); Check(!canceled.Dirty, "restore defaults changes only draft");
            var draft = store.Capture(); draft.Working.Enabled = true;
            var result = store.Save(draft, draft.Working);
            Check(result.Success && store.Current.Enabled && File.Exists(path), "save creates directory atomically and commits after success");
            Check(!store.Current.ShareTrackTitle, "saving Enabled does not opt into track title");
            Check(store.IsCurrent(result), "current successful result may apply");
            var copy = store.Current; copy.Enabled = false;
            Check(store.Current.Enabled, "current returned as independent copy");
            Check(new SteamPresenceSettingsStore(path).Read().Enabled, "saved config survives reload");
            draft = store.Capture(); draft.Working.ShowMusic = false;
            string previous = File.ReadAllText(path);
            var failing = new SteamPresenceSettingsStore(path, (p, s) => throw new IOException("PRIVATE path"));
            failing.Read(); var failDraft = failing.Capture(); failDraft.Working.Enabled = false;
            var failure = failing.Save(failDraft, failDraft.Working);
            Check(!failure.Success && failing.Current.Enabled && File.ReadAllText(path) == previous,
                "write failure preserves disk and applied settings");
            Check(!failure.Error.Contains("PRIVATE"), "write failure redacts underlying message");
            var next = store.Save(draft, draft.Working);
            Check(next.Success && !store.IsCurrent(result), "stale callback cannot publish old options");
            Check(!store.Save(draft, draft.Working).Success, "stale draft rejected");
            draft = store.Capture(); draft.Working.Enabled = false;
            AtomicFile.WriteAllText(path, File.ReadAllText(path) + "\n");
            previous = File.ReadAllText(path);
            Check(!store.Save(draft, draft.Working).Success && File.ReadAllText(path) == previous,
                "external revision change never overwritten");
            foreach (string invalid in new[] { "{\"SchemaVersion\":99,\"Enabled\":true}", "not json", "{\"SchemaVersion\":1,\"private-path\":true}" })
            {
                AtomicFile.WriteAllText(path, invalid);
                Check(!store.Read().Enabled && !store.CanSave, "invalid config disables feature and saving");
                draft = store.Capture(); draft.ResetDefaults();
                Check(!store.Save(draft, draft.Working).Success && File.ReadAllText(path) == invalid,
                    "defaults cannot overwrite rejected config");
                Check(!store.Error.Contains("private-path"), "load errors do not expose unknown values");
            }
            AtomicFile.WriteAllText(path, defaults.Serialize());
            Check(store.Read().SameAs(defaults) && store.CanSave, "explicit external repair plus reload unlocks config");

            string mainPath = Path.Combine(directory, "config", "musicbridge.options.json");
            string mainJson = JObject.FromObject(new MusicBridgeOptions()).ToString();
            AtomicFile.WriteAllText(mainPath, mainJson);
            draft = store.Capture(); draft.Working.Enabled = true;
            Check(store.Save(draft, draft.Working).Success && File.ReadAllText(mainPath) == mainJson,
                "presence save never changes main config");
            var parsed = MusicBridgeOptions.Parse(File.ReadAllText(mainPath));
            Check(parsed.SchemaVersion == 2 && JObject.Parse(mainJson)["SteamPresence"] == null,
                "unchanged legacy main parser still accepts original schema2 file");
            bool pathRejected = false;
            try { new SteamPresenceSettingsStore(Path.Combine(BridgePaths.Root, "..", "outside.json")); }
            catch (InvalidOperationException) { pathRejected = true; }
            Check(pathRejected, "same plugin path boundary applied");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS: " + _count + " presence settings assertions");
    }
}

namespace MusicBridge
{
    internal static class MainThreadDispatcher { public static void Enqueue(Action action) => action(); }
    internal static class BridgeLog { public static void Warn(string text) { } public static void Info(string text) { } }
    internal sealed class AudioPlayer
    { public static AudioPlayer Instance => null; public void ApplySettings(MusicBridgeOptions before, MusicBridgeOptions after) { } }
    internal static class AudioDiskCache { public static void EnforceCapacityAsync() { } }
    internal enum NeteaseQuality { Standard = 128, Exhigh = 320, Lossless = 1000, HiRes = 2000 }
}
