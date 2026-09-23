using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using MusicBridge;
using Newtonsoft.Json.Linq;

internal static class PrefetchTests
{
    private static void Check(bool value, string name) { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static void Wait(Func<bool> condition) { if (!SpinWait.SpinUntil(condition, 10000)) throw new Exception("Prefetch test deadline"); }
    public static void Run()
    {
        Check(new NeteaseOptions().NextAudioPreload, "prefetch default is on");
        var explicitOff = new NeteaseOptions();
        Newtonsoft.Json.JsonConvert.PopulateObject("{\"NextAudioPreload\":false}", explicitOff);
        Check(!explicitOff.NextAudioPreload, "explicit old-config prefetch off remains off");
        Check(new NeteaseOptions().StreamFlacDuringDownload, "FLAC streaming default is on");
        string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../test-artifacts/netease-phase2/fixtures/48000-24-2.flac"));
        foreach (bool flac in new[] { true, false })
        {
            byte[] bytes = flac ? File.ReadAllBytes(fixture) : new byte[] { 255, 251, 144, 0, 1, 2, 3, 4 };
            var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            var worker = new Thread(() => {
                try {
                    using var client = server.AcceptTcpClient(); using var stream = client.GetStream();
                    var request = new byte[8192]; stream.Read(request, 0, request.Length);
                    var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length); stream.Write(bytes, 0, bytes.Length);
                } catch { }
            }); worker.Start();
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            var context = new NeteaseAccountContext(90001, new CookieContainer(), "");
            long id = flac ? 90000011 : 90000012;
            int calls = 0;
            NeteaseApi.TestTransport = (_, body, account) => {
                Interlocked.Increment(ref calls);
                return new JObject { ["code"] = 200, ["data"] = new JArray(new JObject {
                    ["id"] = id, ["code"] = 200, ["url"] = "https://example.org/audio", ["type"] = flac ? "flac" : "mp3",
                    ["level"] = flac ? "hires" : "exhigh", ["br"] = flac ? 1000000 : 320000, ["size"] = bytes.Length, ["freeTrialInfo"] = null }) };
            };
            // Route only the test's download URL to a local HTTP fixture server. API
            // parsing still exercises the production HTTPS and fresh-metadata checks.
            NeteaseAudioPrefetch.TestDownloadUrl = "http://127.0.0.1:" + port + "/test";
            using var job = new NeteaseAudioPrefetch(id, NeteaseQuality.HiRes, context, 1);
            try
            {
                Wait(() => job.Done);
                var fresh = NeteaseApi.GetPlaybackSource(id, NeteaseQuality.HiRes, context, null).Value;
                var changed = NeteaseApi.GetPlaybackSource(id, NeteaseQuality.HiRes, context, null).Value;
                changed.SizeBytes++;
                Check(job.Take(context, changed) == null, "changed authorization metadata cannot reuse prefetch " + flac);
                Check(job.Take(new NeteaseAccountContext(90001, new CookieContainer(), ""), fresh) == null, "new account context cannot take old prefetch " + flac);
                var file = job.Take(context, fresh);
                Check(file != null && fresh.WasPrefetched && calls == 3, "freshly authorized foreground takes completed file " + flac);
                Check(job.Take(context, fresh) == null, "prefetch transfer occurs once " + flac);
                string path = file.Path;
                if (flac)
                {
                    Check(file.VerifiedFormat?.SampleRate == 48000, "prefetch retains verified PCM metadata without PCM allocation");
                    using var preparation = new AudioFilePreparation(context, fresh, file);
                    Wait(() => preparation.Done);
                    Check(preparation.Error == null && preparation.DownloadSeconds == 0, "foreground revalidates prefetched FLAC without network");
                    file = preparation.TakeFile();
                }
                else Check(AudioDiskCache.IsMp3File(path), "MP3 prefetch validates compressed header without Unity PCM");
                file.Dispose(); Wait(() => !File.Exists(path));
                Check(AudioDiskCache.ActiveLeases == 0 && NativeFlacDecoder.ActiveHandles == 0, "prefetch file and decoder released " + flac);
            }
            finally { server.Stop(); worker.Join(1000); NeteaseApi.TestTransport = null; NeteaseAudioPrefetch.TestDownloadUrl = null; }
        }
        var blocked = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        NeteaseApi.TestTransport = (_, body, account) => { entered.Set(); blocked.Wait(10000); return JObject.Parse("{code:301}"); };
        var accountA = new NeteaseAccountContext(90002, new CookieContainer(), "");
        var cancelled = new NeteaseAudioPrefetch(3, NeteaseQuality.HiRes, accountA, 3);
        Check(entered.Wait(1000), "prefetch request entered");
        cancelled.Dispose(); accountA.Invalidate(); blocked.Set(); Wait(() => cancelled.Done);
        Check(AudioDiskCache.ActiveLeases == 0, "cancelled late prefetch never publishes a file");
        blocked.Dispose(); entered.Dispose(); NeteaseApi.TestTransport = null;
        RetryInterruptedBody();
    }
    private static void RetryInterruptedBody()
    {
        byte[] body = { 255, 251, 144, 0, 1, 2, 3, 4 };
        var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
        int requests = 0;
        var worker = new Thread(() => {
            try {
                for (int i = 0; i < 2; i++)
                {
                    using var client = server.AcceptTcpClient(); using var stream = client.GetStream();
                    stream.Read(new byte[4096], 0, 4096); Interlocked.Increment(ref requests);
                    byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\n");
                    stream.Write(header, 0, header.Length); stream.Write(body, 0, i == 0 ? 4 : 8); stream.Flush();
                }
            } catch { }
        }); worker.Start();
        var context = new NeteaseAccountContext(90003, new CookieContainer(), "");
        var source = new NeteasePlaybackSource { SongId = 99, Format = "mp3", SizeBytes = 8,
            Url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/audio" };
        using var preparation = new AudioFilePreparation(context, source, null, publishCache: false);
        try
        {
            Wait(() => preparation.Done);
            Check(preparation.Error == null && preparation.RetryCount == 1 && requests == 2, "interrupted body retries once and recovers without quality change");
            using var file = preparation.TakeFile();
            Check(file != null && File.ReadAllBytes(file.Path).Length == 8, "retry replaces only its partial temporary file");
        }
        finally { server.Stop(); worker.Join(1000); }
    }
}
