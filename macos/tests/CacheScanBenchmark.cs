using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MusicBridge;

internal static class CacheScanBenchmark
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }

    public static void Run()
    {
        long account = DateTime.UtcNow.Ticks;
        string partition = Path.Combine(AudioDiskCache.Root, "v2", "netease", account.ToString());
        Directory.CreateDirectory(partition);
        try
        {
            int built = 0;
            foreach (int count in new[] { 100, 1000, 10000 })
            {
                for (int i = built; i < count; i++)
                    File.WriteAllBytes(Path.Combine(partition, i.ToString("D5") + ".residual"), new byte[] { 1 });
                built = count;
                var watch = Stopwatch.StartNew();
                CacheUsageSnapshot usage = AudioDiskCache.Scan(account);
                watch.Stop();
                Check(usage.ResidualFiles == count && usage.ResidualBytes == count && usage.Error == null,
                    count + "-file scan counts residuals, elapsed_ms=" + watch.ElapsedMilliseconds);
            }

            int inspected = 0;
            bool cancelled = false;
            try { AudioDiskCache.Scan(account, () => Interlocked.Increment(ref inspected) > 100); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && inspected < 10000, "large scan honors cancellation before full traversal");

            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var scan = Task.Run(() => AudioDiskCache.Scan(account, () =>
            {
                if (!entered.IsSet) { entered.Set(); release.Wait(5000); }
                return false;
            }));
            Check(entered.Wait(5000), "background scan entered read-only traversal");
            var foreground = Stopwatch.StartNew();
            using (var lease = AudioDiskCache.CreateTemporary()) { }
            foreground.Stop();
            release.Set();
            Check(foreground.ElapsedMilliseconds < 1000 && scan.Wait(10000),
                "foreground temporary preparation is not held by scan-wide I/O lock");
        }
        finally { Directory.Delete(partition, true); }
    }
}
