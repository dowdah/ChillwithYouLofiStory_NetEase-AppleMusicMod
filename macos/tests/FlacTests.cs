using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using MusicBridge;
using Newtonsoft.Json.Linq;

internal static class FlacTests
{
    private static void Check(bool value, string message)
    { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS: " + message); }
    private static void Wait(Func<bool> predicate)
    { if (!SpinWait.SpinUntil(predicate, 10000)) throw new Exception("FLAC test deadline"); }
    private static float Sample(long frame, int channel, int bits) =>
        (float)(((frame * 31 + channel * 101) % (1L << bits)) - (1L << (bits - 1))) / (1L << (bits - 1));
    public static void LongFile()
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../test-artifacts/netease-phase2/long-60min.flac"));
        var format = NativeFlacDecoder.Validate(path, () => false);
        Check(format.SampleRate == 192000 && format.BitsPerSample == 24 && format.Channels == 2 && format.Duration == 3600,
            "60-minute 24/192 stereo file fully decoded and PCM MD5 checked (accelerated, not real-time soak)");
        var stream = new FlacPcmStream(path, null);
        Wait(() => stream.Ready || stream.Error != null);
        Check(stream.Error == null && stream.BufferedBytes == 192000 * 2 * 8 * 4, "60-minute file uses exactly 8 seconds of PCM storage");
        var pcm = new float[4096];
        foreach (long frame in new long[] { 0, 192000L * 1800, 192000L * 3599 })
        {
            stream.Seek(frame); Wait(() => stream.Ready || stream.Error != null);
            stream.Enable(true); stream.Read(pcm);
            Check(stream.PositionFrames == frame + 2048 && pcm.All(x => x == 0), "long file seeks to reference silence at " + frame);
        }
        stream.Dispose(); Wait(() => stream.Released);
        Check(NativeFlacDecoder.ActiveHandles == 0, "long file closes decoder");
    }
    public static void Run()
    {
        string fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../test-artifacts/netease-phase2/fixtures"));
        Check(Directory.GetFiles(fixtures, "*.flac").Length == 16, "all real FLAC fixture combinations exist");
        foreach (string file in Directory.GetFiles(fixtures, "*.flac"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('-').Select(int.Parse).ToArray();
            int rate = parts[0], bits = parts[1], channels = parts[2];
            var info = NativeFlacDecoder.Validate(file, () => false);
            Check(info.SampleRate == rate && info.BitsPerSample == bits && info.Channels == channels && info.Frames == rate * 3,
                "FLAC STREAMINFO, complete PCM count and MD5: " + Path.GetFileName(file));
            using (var decoder = new NativeFlacDecoder(file))
            {
                var pcm = new float[1024 * channels];
                foreach (long position in new long[] { 0, rate + 127, rate * 3 - 11, 17 })
                {
                    decoder.Seek(position);
                    int count = decoder.Read(pcm, 1024);
                    Check(count == Math.Min(1024, rate * 3 - position), "seek frame count " + position);
                    bool equal = true;
                    for (int frame = 0; frame < count; frame++) for (int c = 0; c < channels; c++)
                        equal &= Math.Abs(pcm[frame * channels + c] - Sample(position + frame, c, bits)) <= 1e-7;
                    Check(equal, "seek matches reference PCM " + position);
                }
            }
        }
        string fixture = Path.Combine(fixtures, "192000-24-2.flac");
        string bad = Path.Combine(BridgePaths.Root, "bad.flac");
        try
        {
            byte[] bytes = File.ReadAllBytes(fixture);
            File.WriteAllBytes(bad, bytes.Take(bytes.Length / 2).ToArray());
            bool rejected = false; try { NativeFlacDecoder.Validate(bad, () => false); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "truncated real FLAC rejected");
            bytes[26] ^= 1; File.WriteAllBytes(bad, bytes);
            rejected = false; try { NativeFlacDecoder.Validate(bad, () => false); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "corrupt PCM digest rejected");
            rejected = false; try { NativeFlacDecoder.Validate(fixture, () => true); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "integrity scan is cancellable");
        }
        finally { File.Delete(bad); }
        var ring = new PcmRingBuffer(6);
        ring.Write(new float[] { 1, 2, 3, 4 }, 4); var small = new float[3]; ring.Read(small, 3);
        ring.Write(new float[] { 5, 6, 7, 8, 9 }, 5); var wrapped = new float[6]; ring.Read(wrapped, 6);
        Check(wrapped.SequenceEqual(new float[] { 4, 5, 6, 7, 8, 9 }), "PCM ring wraps without losing samples");
        for (int round = 0; round < 100; round++)
        {
            var stream = new FlacPcmStream(fixture, null);
            Wait(() => stream.Ready || stream.Error != null);
            Check(stream.Error == null && stream.BufferedBytes <= 32 * 1024 * 1024, "bounded stream ready " + round);
            var pcm = new float[512 * 2];
            stream.Enable(true); stream.Read(pcm);
            Check(pcm[0] == Sample(0, 0, 24) && pcm[1] == Sample(0, 1, 24), "stream first PCM " + round);
            var oldReader = stream.CreateClipReader();
            stream.Seek(10000); stream.Seek(30000); stream.Seek(50000);
            Wait(() => stream.Ready || stream.Error != null);
            stream.Enable(true); oldReader.Read(pcm);
            Check(pcm.All(x => x == 0) && stream.PositionFrames == 50000, "destroyed clip callback cannot consume a newer seek " + round);
            stream.Enable(true); stream.Read(pcm);
            Check(pcm[0] == Sample(50000, 0, 24), "latest seek wins without old PCM " + round);
            stream.Enable(false); long paused = stream.PositionFrames; stream.Read(pcm);
            Check(pcm.All(x => x == 0) && stream.PositionFrames == paused, "pause does not consume PCM " + round);
            stream.Dispose(); stream.Dispose(); stream.Read(pcm);
            Wait(() => stream.Released);
            Check(pcm.All(x => x == 0), "late callback after close is silence " + round);
        }
        Check(NativeFlacDecoder.ActiveHandles == 0 && FlacPcmStream.ActiveWorkers == 0, "100 sessions release every native handle and worker");
        CacheTests(fixture);
        var terminal = new FlacPcmStream(fixture, null, 192000 * 3 - 200);
        Wait(() => terminal.Ready); terminal.Enable(true);
        var tail = new float[1024]; terminal.Read(tail);
        Check(terminal.Drained && terminal.PositionFrames == 192000 * 3 && tail.Skip(400).All(x => x == 0), "EOF requires draining real PCM and zero-pads tail");
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) terminal.Read(tail);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated, "steady PCM callback performs zero managed allocations");
        terminal.Dispose(); Wait(() => terminal.Released);
        ConcurrentReader(fixture);
        DownloadFailureTests();
        QualityTests();
    }
    private static void CacheTests(string fixture)
    {
        var source = new NeteasePlaybackSource { SongId = 987654321, RequestedQuality = NeteaseQuality.HiRes,
            AttemptedQuality = NeteaseQuality.HiRes, ReturnedLevel = "hires", Format = "flac", Bitrate = 3000000,
            SizeBytes = new FileInfo(fixture).Length, IsTrial = false };
        NativeFlacDecoder.Validate(fixture, () => false).Apply(source);
        var temporary = AudioDiskCache.CreateTemporary(); string path = temporary.Path;
        File.Copy(fixture, path);
        AudioDiskCache.StoreValidatedFile(42, source, temporary, () => true);
        var hit = AudioDiskCache.TryGet(42, source);
        Check(hit != null, "validated FLAC atomically published and matched");
        source.ReturnedLevel = "lossless";
        Check(AudioDiskCache.TryGet(42, source) == null, "changed returned level invalidates FLAC cache");
        source.ReturnedLevel = "hires";
        var context = new NeteaseAccountContext(42, new CookieContainer(), "");
        var preparation = new AudioFilePreparation(context, source, hit);
        Wait(() => preparation.Done);
        Check(preparation.Error == null && preparation.Format.SampleRate == 192000, "cached FLAC uses full real integrity validation");
        var transferred = preparation.TakeFile(); preparation.Dispose();
        Check(transferred != null && File.Exists(transferred.Path), "preparation transfers rather than closes active file");
        transferred.Dispose(); temporary.Dispose(); Wait(() => !File.Exists(path));
        var cancelled = AudioDiskCache.CreateTemporary(); string cancelPath = cancelled.Path;
        File.Copy(fixture, cancelPath); source.SongId++;
        AudioDiskCache.StoreValidatedFile(42, source, cancelled, () => false);
        Check(AudioDiskCache.TryGet(42, source) == null, "cancelled FLAC cache write not published");
        cancelled.Dispose(); Wait(() => !File.Exists(cancelPath));
    }
    private static void ConcurrentReader(string fixture)
    {
        var stream = new FlacPcmStream(fixture, null);
        Wait(() => stream.Ready || stream.Error != null);
        var samples = new float[2048]; int stop = 0; Exception readerError = null;
        var reader = new Thread(() => {
            try { while (Volatile.Read(ref stop) == 0) { stream.Read(samples); Thread.Yield(); } }
            catch (Exception ex) { readerError = ex; }
        });
        reader.Start();
        for (int i = 0; i < 100; i++) { stream.Seek(i * 1024); stream.Enable(true); Thread.Yield(); }
        stream.Dispose(); Wait(() => stream.Released);
        Volatile.Write(ref stop, 1); Check(reader.Join(1000) && readerError == null, "audio callback races 100 seeks and release without exception or deadlock");
        Check(NativeFlacDecoder.ActiveHandles == 0 && FlacPcmStream.ActiveWorkers == 0, "concurrent release drains all workers and native handles");
    }
    private static void DownloadFailureTests()
    {
        Check(AudioFilePreparation.Transient(WebExceptionStatus.ReceiveFailure) &&
            !AudioFilePreparation.Transient(WebExceptionStatus.ProtocolError) &&
            !AudioFilePreparation.Transient(WebExceptionStatus.TrustFailure), "retry only transient transport failures, never HTTP permission or TLS trust failure");
        var options = MusicBridgeOptions.Current.Netease;
        var oldStall = options.AudioStallTimeout; var oldTotal = options.FlacRequestTimeout;
        try
        {
            options.AudioStallTimeout = TimeSpan.FromMilliseconds(100);
            options.FlacRequestTimeout = TimeSpan.FromSeconds(3);
            foreach (bool cancel in new[] { false, true })
            {
                var server = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                server.Start(); var accepted = new ManualResetEventSlim(false);
                var worker = new Thread(() => {
                    try {
                        using var client = server.AcceptTcpClient();
                        using var input = client.GetStream();
                        var buffer = new byte[4096]; input.Read(buffer, 0, buffer.Length);
                        accepted.Set(); Thread.Sleep(800); // Never send a first response byte.
                    } catch { accepted.Set(); }
                });
                worker.Start();
                var context = new NeteaseAccountContext(101, new CookieContainer(), "");
                var source = new NeteasePlaybackSource { Url = "http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/test.flac", Format = "flac" };
                var timer = System.Diagnostics.Stopwatch.StartNew();
                using var preparation = new AudioFilePreparation(context, source, null);
                try
                {
                    Check(accepted.Wait(2000), "test server receives FLAC request");
                    if (cancel) context.Invalidate();
                    Wait(() => preparation.Done);
                    Check(preparation.Error != null && !preparation.DecodeFailure && preparation.TakeFile() == null,
                        cancel ? "account invalidation cancels download without decoder fallback" : "first-byte stall classified as network failure");
                    Check(timer.ElapsedMilliseconds < 2000, "stalled or cancelled request stops before total deadline");
                    Check(AudioDiskCache.ActiveLeases == 0, "failed download releases temporary lease");
                }
                finally { server.Stop(); worker.Join(2000); accepted.Dispose(); }
            }
        }
        finally { options.AudioStallTimeout = oldStall; options.FlacRequestTimeout = oldTotal; }
    }
    private static void QualityTests()
    {
        Check(NeteaseQualityPolicy.Lower(NeteaseQuality.HiRes) == NeteaseQuality.Lossless && NeteaseQualityPolicy.Lower(NeteaseQuality.Standard) == null, "bounded quality ladder");
        Check(!NeteaseQualityPolicy.CanFallback(NeteaseFailure.Network) && !NeteaseQualityPolicy.CanFallback(NeteaseFailure.Unauthorized) && !NeteaseQualityPolicy.CanFallback(NeteaseFailure.Cancelled), "network auth and cancel never downgrade");
        var c = new NeteaseAccountContext(1, new CookieContainer(), "");
        foreach (var quality in new[] { NeteaseQuality.Standard, NeteaseQuality.Exhigh, NeteaseQuality.Lossless, NeteaseQuality.HiRes })
        {
            string level = null, encode = null;
            NeteaseApi.TestTransport = (_, body, context) => {
                level = body.Value<string>("level"); encode = body.Value<string>("encodeType");
                return JObject.Parse("{code:200,data:[{id:1,code:200,url:'https://example.org/test',type:'flac',level:'lossless',br:900000,size:100,freeTrialInfo:null}]}");
            };
            var result = NeteaseApi.GetPlaybackSource(1, quality, c, null);
            Check(result.Ok && level == NeteaseQualityPolicy.Level(quality) && encode == (NeteaseQualityPolicy.IsLossless(quality) ? "flac" : "mp3"), "quality request " + quality);
            if (quality == NeteaseQuality.HiRes) Check(result.Value.QualityLabel.Contains("已降级") && !result.Value.QualityLabel.Contains("192"), "server fallback displayed without fabricated specs");
        }
        NeteaseApi.TestTransport = null;
    }
    // Explicit developer invocation only. Reads existing keychain session; never prints,
    // exports, persists or changes credentials, favorites, or recommendation state.
    private static NeteaseAccountContext ExistingContext()
    {
        if (SessionStore.TryLoad(out string session) != SessionStore.LoadResult.Ok) throw new Exception("No existing session available");
        var cookies = JObject.Parse(session)["cookies"].ToObject<System.Collections.Generic.Dictionary<string, string>>();
        NeteaseApi.RestoreCookies(cookies); cookies.Clear(); session = null;
        if (NeteaseApi.GetAccount(out var account) != AccountCheck.Valid) throw new Exception("Existing account unavailable");
        return NeteaseApi.CaptureContext(account.UserId);
    }
    public static void SourceLatency(long[] ids)
    {
        if (ids.Length == 0 || ids.Length > 3 || ids.Any(id => id <= 0)) throw new ArgumentException("Supply 1–3 song IDs");
        var context = ExistingContext();
        try
        {
            for (int i = 0; i < 20; i++)
            {
                long id = ids[i % ids.Length]; var timer = System.Diagnostics.Stopwatch.StartNew();
                var result = NeteaseApi.GetPlaybackSource(id, NeteaseQuality.HiRes, context, null);
                Console.WriteLine("lookup_sample=" + (i + 1) + " songId=" + id + " seconds=" + timer.Elapsed.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " ok=" + result.Ok);
                if (!result.Ok) throw new Exception("Read-only source latency probe failed: " + result.Failure);
            }
        }
        finally { context.Invalidate(); }
    }
    public static void ProbeSong(long id)
    {
        var context = ExistingContext();
        try
        {
            var result = NeteaseApi.GetPlaybackSource(id, NeteaseQuality.HiRes, context, null);
            if (!result.Ok) throw new Exception("Source failed: " + result.Failure);
            var source = result.Value;
            Console.WriteLine("songId=" + id + " returned=" + source.ReturnedLevel + " format=" + source.Format + " size=" + source.SizeBytes + " host=" + new Uri(source.Url).Host);
            using var preparation = new AudioFilePreparation(context, source, null);
            if (!SpinWait.SpinUntil(() => preparation.Done, 240000)) throw new Exception("Probe deadline");
            Console.WriteLine("result=" + (preparation.Error ?? "PASS") + " network=" + preparation.NetworkStatus + " download_s=" + preparation.DownloadSeconds.ToString("F3") + " validate_s=" + preparation.ValidationSeconds.ToString("F3"));
            if (preparation.Error != null) throw new Exception("Download probe failed");
            using var file = preparation.TakeFile();
        }
        finally { context.Invalidate(); }
    }
    public static void ProbeAccount(bool download = false)
    {
        var context = ExistingContext();
        var favorites = NeteaseApi.RefreshFavorites(context, null);
        if (!favorites.Ok) throw new Exception("Cannot read existing favorite IDs");
        int downloaded = 0;
        foreach (long id in favorites.Value.Take(10))
        foreach (var quality in new[] { NeteaseQuality.Lossless, NeteaseQuality.HiRes })
        {
            var result = NeteaseApi.GetPlaybackSource(id, quality, context, null);
            if (!result.Ok) { Console.WriteLine("source song=" + id + " request=" + quality + " failure=" + result.Failure); continue; }
            var source = result.Value;
            Console.WriteLine("source song=" + id + " request=" + quality + " level=" + source.ReturnedLevel + " format=" + source.Format + " br=" + source.Bitrate + " size=" + source.SizeBytes + " trial=" + source.IsTrial);
            if (!source.IsFlac) continue;
            if (download && quality == NeteaseQuality.HiRes && source.ReturnedLevel == "hires")
            {
                using var preparation = new AudioFilePreparation(context, source, null);
                if (!SpinWait.SpinUntil(() => preparation.Done, 240000)) throw new Exception("Full FLAC probe deadline");
                Console.WriteLine("full_file result=" + (preparation.Error ?? "PASS") + " download_s=" + preparation.DownloadSeconds.ToString("F3") + " validation_s=" + preparation.ValidationSeconds.ToString("F3"));
                if (preparation.Error != null) throw new Exception("Full FLAC validation failed");
                using var file = preparation.TakeFile();
                var pcm = new FlacPcmStream(file.Path, null);
                Wait(() => pcm.Ready || pcm.Error != null);
                Check(pcm.Error == null, "real account FLAC prefills");
                pcm.Seek(preparation.Format.Frames / 2); Wait(() => pcm.Ready || pcm.Error != null);
                Check(pcm.Error == null, "real account FLAC seeks to midpoint");
                pcm.Dispose(); Wait(() => pcm.Released);
                if (++downloaded == 2) { context.Invalidate(); return; }
            }

            var req = (HttpWebRequest)WebRequest.Create(source.Url); req.AddRange(0, 41); req.Timeout = 15000; req.ReadWriteTimeout = 15000;
            try
            {
                using var response = req.GetResponse(); using var input = response.GetResponseStream();
                byte[] head = new byte[42]; int n = 0, read;
                while (n < head.Length && (read = input.Read(head, n, head.Length - n)) > 0) n += read;
                if (n != 42 || head[0] != 'f' || head[1] != 'L' || head[2] != 'a' || head[3] != 'C') { Console.WriteLine("header=unsupported"); continue; }
                ulong packed = 0; for (int i = 18; i < 26; i++) packed = (packed << 8) | head[i];
                Console.WriteLine("STREAMINFO rate=" + (packed >> 44) + " bits=" + (((packed >> 36) & 31) + 1) + " channels=" + (((packed >> 41) & 7) + 1));
            }
            catch (Exception ex) { Console.WriteLine("header failure=" + ex.GetType().Name); }
        }
        context.Invalidate();
    }
}
