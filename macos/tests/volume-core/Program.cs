using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MusicBridge;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        _checks++; Console.WriteLine("PASS: " + label);
    }
    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.00001f;
    private static void Reject(string json)
    {
        bool rejected = false;
        try { PlayerVolumeStore.Parse(json); } catch { rejected = true; }
        Check(rejected, "reject invalid/future volume data");
    }

    private static void Main()
    {
        string root = BridgePaths.Resolve("volume-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "config", "musicbridge.volume.json");
            var store = new PlayerVolumeStore(path);
            Check(Near(store.Volume, 0.7f) && !File.Exists(path), "first launch uses legacy default without writing");
            store.Set(0.14f, 0); store.Flush();
            Check(Near(new PlayerVolumeStore(path).Volume, 0.14f), "low volume restored across a fresh instance");
            store.Set(0, 1); store.Close(); store.Close();
            Check(new PlayerVolumeStore(path).Volume == 0, "mute preserved including immediate quit and repeated cleanup");
            store.Set(0.9f, 2); store.Tick(3);
            Check(new PlayerVolumeStore(path).Volume == 0, "closed store cannot overwrite saved preference");

            store = new PlayerVolumeStore(path);
            store.Set(float.NaN, 0); store.Set(float.PositiveInfinity, 0); store.Set(float.NegativeInfinity, 0);
            Check(store.Volume == 0, "non-finite inputs ignored");
            store.Set(5, 0); Check(store.Volume == 1, "clamp high slider input");
            store.Set(-1, 0); Check(store.Volume == 0, "clamp low slider input");
            store.Flush();

            int writes = 0;
            using var saved = new ManualResetEventSlim();
            store = new PlayerVolumeStore(path, (p, s) => { AtomicFile.WriteAllText(p, s); Interlocked.Increment(ref writes); saved.Set(); });
            store.Set(0.1f, 1); store.Set(0.2f, 1.1); store.Set(0.3f, 1.2);
            store.Tick(1.69); Check(writes == 0, "drag debounce does not write early");
            store.Tick(1.71); Check(saved.Wait(5000), "background save completes");
            store.Close();
            Check(writes == 1 && Near(new PlayerVolumeStore(path).Volume, 0.3f), "rapid drag coalesces latest volume");

            string before = File.ReadAllText(path);
            bool fail = true;
            store = new PlayerVolumeStore(path, (p, s) => { if (fail) throw new IOException("PRIVATE_PATH"); AtomicFile.WriteAllText(p, s); });
            store.Set(0.12f, 0); store.Flush();
            Check(File.ReadAllText(path) == before && !store.LastError.Contains("PRIVATE_PATH"), "failed write preserves original and redacts error");
            fail = false; store.Flush();
            Check(store.LastError == null && Near(new PlayerVolumeStore(path).Volume, 0.12f), "failed write remains pending for retry");

            store = new PlayerVolumeStore(path);
            store.Set(0.1f, 0);
            AtomicFile.WriteAllText(path, "{\"SchemaVersion\":1,\"NeteaseVolume\":0.25}");
            store.Flush();
            Check(store.LastError != null && Near(new PlayerVolumeStore(path).Volume, 0.25f), "external modification is not overwritten");

            foreach (string json in new[] {
                "{}", "[]", "null", "{\"SchemaVersion\":2,\"NeteaseVolume\":0.2}",
                "{\"SchemaVersion\":1,\"NeteaseVolume\":-0.1}", "{\"SchemaVersion\":1,\"NeteaseVolume\":1.1}",
                "{\"SchemaVersion\":1,\"NeteaseVolume\":NaN}", "{\"SchemaVersion\":1,\"NeteaseVolume\":Infinity}",
                "{\"SchemaVersion\":1,\"NeteaseVolume\":\"0.2\"}", "{\"SchemaVersion\":1,\"NeteaseVolume\":null}",
                "{\"SchemaVersion\":1,\"NeteaseVolume\":0.2,\"Extra\":0}",
                "{\"SchemaVersion\":1,\"NeteaseVolume\":0.2,\"NeteaseVolume\":0.9}",
                "{\"SchemaVersion\":1.0,\"NeteaseVolume\":0.2}", "{\"SchemaVersion\":1,\"NeteaseVolume\":0.2",
                "{\"SchemaVersion\":1,\"NeteaseVolume\":0.2} {}" }) Reject(json);
            AtomicFile.WriteAllText(path, "{\"SchemaVersion\":99,\"NeteaseVolume\":0.2}");
            before = File.ReadAllText(path); store = new PlayerVolumeStore(path); store.Set(0.15f, 0); store.Close();
            Check(File.ReadAllText(path) == before && store.LastError != null && Near(store.Volume, 0.15f),
                "rejected file preserved while volume still adjustable in memory");

            string concurrentPath = Path.Combine(root, "concurrent.json");
            using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
            int saves = 0;
            store = new PlayerVolumeStore(concurrentPath, (p, s) => {
                if (Interlocked.Increment(ref saves) == 1) { entered.Set(); if (!release.Wait(5000)) throw new TimeoutException(); }
                AtomicFile.WriteAllText(p, s);
            });
            store.Set(0.3f, 0); store.Tick(1);
            Check(entered.Wait(5000), "background writer started");
            store.Set(0.07f, 1.1); // New setting while the older disk snapshot is in flight.
            Task close = Task.Run(store.Close); release.Set();
            Check(close.Wait(5000) && Near(new PlayerVolumeStore(concurrentPath).Volume, 0.07f),
                "exit serializes against in-flight write and saves newest setting");

            var outside = new PlayerVolumeStore(Path.Combine(Path.GetTempPath(), "volume-outside.json"));
            outside.Set(0.1f, 0); outside.Close();
            Check(outside.LastError != null, "outside-plugin write rejected");
            Check(PlayerVolumeStore.Parse("{\"NeteaseVolume\":1,\"SchemaVersion\":1}") == 1, "field order independent and max valid");
            Console.WriteLine("PASS: " + _checks + " volume assertions");
        }
        finally { Directory.Delete(root, true); } // Only this test's newly created GUID directory.
    }
}
