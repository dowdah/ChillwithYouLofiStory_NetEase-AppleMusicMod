using System;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using MusicBridge;

internal static class CacheSafetyTests
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

    public static void Run()
    {
        string root = BridgePaths.Resolve("test-artifacts", "safe-delete-" + Guid.NewGuid().ToString("N"));
        string inside = Path.Combine(root, "inside");
        string outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(inside); Directory.CreateDirectory(outside);
        string marker = Path.Combine(outside, "keep.mp3");
        string normal = Path.Combine(inside, "remove.mp3");
        string link = Path.Combine(inside, "linked.mp3");
        string directoryLink = Path.Combine(root, "linked-directory");
        try
        {
            File.WriteAllBytes(marker, new byte[] { 1 });
            File.WriteAllBytes(normal, new byte[] { 2 });
            Check(SafeCacheFiles.DeleteRegular(normal) && !File.Exists(normal), "directory-relative regular cache unlink works");
            File.CreateSymbolicLink(link, marker);
            bool rejected = false;
            try { SafeCacheFiles.DeleteRegular(link); }
            catch (IOException) { rejected = true; }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && File.Exists(marker), "symlinked file is rejected without deleting target");
            Directory.CreateSymbolicLink(directoryLink, outside);
            rejected = false;
            try { SafeCacheFiles.DeleteRegular(Path.Combine(directoryLink, "keep.mp3")); }
            catch (InvalidOperationException) { rejected = true; }
            catch (IOException) { rejected = true; }
            Check(rejected && File.Exists(marker), "symlinked directory cannot redirect deletion");

            long account = DateTime.UtcNow.Ticks;
            string partition = Path.Combine(AudioDiskCache.Root, "v2", "netease", account.ToString());
            Directory.CreateDirectory(partition);
            try
            {
                string hash = new string('a', 64);
                string audio = Path.Combine(partition, "1-full-128-128000-" + hash + ".mp3");
                string forgedIndex = Path.Combine(partition, "1-320-full.json");
                File.WriteAllBytes(audio, new byte[] { 255, 251, 144, 0, 0, 0, 0, 0 });
                File.WriteAllText(forgedIndex, JsonConvert.SerializeObject(new AudioDiskCache.Entry
                { Account = account, SongId = 1, Size = 8, Requested = NeteaseQuality.Standard,
                    Bitrate = 128000, Format = "mp3", File = Path.GetFileName(audio), Hash = hash, Trial = false }));
                Check(AudioDiskCache.BuildCleanupPlan(account).FileCount == 0 &&
                    File.Exists(audio) && File.Exists(forgedIndex),
                    "index name must match owned song and quality before cleanup eligibility");
            }
            finally { Directory.Delete(partition, true); }

            string work = Path.Combine(AudioDiskCache.Root, "v2", "work");
            Directory.CreateDirectory(work);
            string session = Guid.NewGuid().ToString("N");
            string staleLock = Path.Combine(work, "session-" + session + ".lock");
            string stalePart = Path.Combine(work, session + "-" + Guid.NewGuid().ToString("N") + ".part");
            File.WriteAllBytes(staleLock, Array.Empty<byte>());
            File.WriteAllBytes(stalePart, new byte[] { 1, 2, 3 });
            var active = AudioDiskCache.CreateTemporary();
            File.WriteAllBytes(active.Path, new byte[] { 9, 8 });
            string unpinnedActive = TemporarySessionRegistry.NewFile(work);
            File.WriteAllBytes(unpinnedActive, new byte[] { 7 });
            var plan = AudioDiskCache.BuildStaleWorkPlan();
            Check(plan.FileCount == 1 && plan.EstimatedBytes == 3,
                "stale plan excludes both pinned and session-locked active work files");
            var outcome = AudioDiskCache.ExecuteStaleWork(plan);
            Check(outcome.Deleted == 1 && outcome.DeletedBytes == 3 && !File.Exists(stalePart) &&
                File.Exists(active.Path), "stale cleanup preserves active session work file");
            string activePath = active.Path;
            active.Dispose();
            Check(SpinWait.SpinUntil(() => !File.Exists(activePath), 5000), "active work file leaves after lease disposal");
            SafeCacheFiles.DeleteRegular(unpinnedActive);
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (Directory.Exists(directoryLink)) Directory.Delete(directoryLink);
            Directory.Delete(root, true);
        }
    }
}
