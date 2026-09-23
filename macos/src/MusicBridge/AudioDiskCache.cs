using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace MusicBridge;

internal static class AudioDiskCache
{
    internal sealed class Entry
    {
        public int Version = 2;
        public long Account, SongId, Size;
        public NeteaseQuality Requested;
        public int? Bitrate, SampleRate, BitsPerSample, Channels;
        public long? PcmFrames;
        public string ReturnedLevel;
        public string Format, File, Hash, ServerMd5;
        public bool? Trial;
        public double? TrialStart, TrialEnd;
    }
    internal sealed class Lease : IDisposable
    {
        public string Uri { get; internal set; }
        internal string Index, File;
        internal bool DeleteOnDispose;
        internal PcmFormat VerifiedFormat;
        internal string VerifiedHash;
        public string Path => File;
        public void Dispose()
        {
            string remove = null;
            lock (Gate)
            {
                if (File == null) return;
                if (Pins.TryGetValue(File, out int count) && count > 1) Pins[File] = count - 1;
                else { Pins.Remove(File); if (DeleteOnDispose) remove = File; }
                File = null;
            }
            // Leases may close on the main thread: disk cleanup must not block it.
            if (remove != null) System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { System.IO.File.Delete(BridgePaths.ValidateWritePath(remove)); } catch { } });
        }
    }
    private static readonly object Gate = new object();
    private static readonly object IoGate = new object();
    private static bool Pinned(string file) { lock (Gate) return Pins.ContainsKey(file); }
    private static void Pin(string file) { lock (Gate) { Pins.TryGetValue(file, out int n); Pins[file] = n + 1; } }
    private static readonly Dictionary<string, int> Pins = new Dictionary<string, int>(StringComparer.Ordinal);
    internal static int ActiveLeases { get { lock (Gate) return Pins.Values.Sum(); } }
    internal static string Root => BridgePaths.Resolve("cache", "audio");
    private static string Partition(long account) => Path.Combine(Root, "v2", "netease", account.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static string IndexPath(long account, NeteasePlaybackSource source) => Path.Combine(Partition(account), source.SongId + "-" + (int)source.RequestedQuality + "-" + (source.IsTrial == true ? "trial" : source.IsTrial == false ? "full" : "unknown") + ".json");
    private static string Digest(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    internal static string DigestFile(string path) { using var file = System.IO.File.OpenRead(path); using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant(); }
    public static Lease CreateTemporary()
    {
        lock (IoGate)
        {
            string directory = BridgePaths.ValidateWritePath(System.IO.Path.Combine(Root, "v2", "work"));
            Directory.CreateDirectory(directory);
            string file = BridgePaths.ValidateWritePath(System.IO.Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part"));
            Pin(file);
            return new Lease { File = file, Uri = new Uri(file).AbsoluteUri, DeleteOnDispose = true };
        }
    }
    // Worker only. The validated temporary file remains playable if cache publication fails.
    public static void StoreValidatedFile(long account, NeteasePlaybackSource source, Lease lease, Func<bool> current)
    {
        if (!source.IsTrial.HasValue || !source.Bitrate.HasValue || !source.SizeBytes.HasValue ||
            MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes == 0) return;
        lock (IoGate)
        {
            string file = null, staging = null;
            try
            {
                var info = new FileInfo(lease.Path);
                if (!current() || info.Length != source.SizeBytes || info.Length > MusicBridgeOptions.Current.Netease.AudioCacheMaximumFileBytes || info.Length > MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes) return;
                string hash = DigestFile(lease.Path);
                string name = source.SongId + "-" + (source.IsTrial == true ? "trial" : "full") + "-" + (int)source.RequestedQuality + "-" + (source.Bitrate?.ToString() ?? "unknown") + "-" + hash + ".flac";
                file = BridgePaths.ValidateWritePath(System.IO.Path.Combine(Partition(account), name));
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
                string index = IndexPath(account, source);
                staging = file + "." + Guid.NewGuid().ToString("N") + ".part";
                using (var input = System.IO.File.OpenRead(lease.Path))
                using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var buffer = new byte[65536]; int n;
                    while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                    { if (!current()) return; output.Write(buffer, 0, n); }
                    output.Flush(true);
                }
                if (!current()) return;
                if (!System.IO.File.Exists(file)) System.IO.File.Move(staging, file);
                else System.IO.File.Delete(staging);
                var entry = new Entry { Account = account, SongId = source.SongId, Requested = source.RequestedQuality,
                    Bitrate = source.Bitrate, Format = source.Format, File = name, Hash = hash, Size = info.Length,
                    ServerMd5 = source.ServerMd5, ReturnedLevel = source.ReturnedLevel, Trial = source.IsTrial,
                    TrialStart = source.TrialStartSeconds, TrialEnd = source.TrialEndSeconds,
                    SampleRate = source.SampleRate, BitsPerSample = source.BitsPerSample, Channels = source.Channels, PcmFrames = source.PcmFrames };
                AtomicFile.WriteAllText(index, JsonConvert.SerializeObject(entry));
                // Playback keeps the temporary lease; no rename races with an open decoder.
                Evict();
            }
            catch { BridgeLog.Warn("FLAC缓存登记失败，本次临时文件继续播放。"); }
            finally { if (staging != null) try { System.IO.File.Delete(staging); } catch { } }
        }
    }
    internal static bool Matches(Entry entry, long account, NeteasePlaybackSource source) =>
        entry != null && entry.Version == 2 && entry.Account == account && entry.SongId == source.SongId && entry.Requested == source.RequestedQuality &&
        entry.Bitrate == source.Bitrate && entry.Format == source.Format && entry.Trial == source.IsTrial && entry.TrialStart == source.TrialStartSeconds && entry.TrialEnd == source.TrialEndSeconds &&
        (!source.SizeBytes.HasValue || entry.Size == source.SizeBytes) && entry.ServerMd5 == source.ServerMd5 && (entry.ReturnedLevel == null || entry.ReturnedLevel == source.ReturnedLevel);
    internal static bool SameSource(NeteasePlaybackSource a, NeteasePlaybackSource b) =>
        a != null && b != null && a.SongId == b.SongId && a.RequestedQuality == b.RequestedQuality &&
        a.Format == b.Format && a.ReturnedLevel == b.ReturnedLevel && a.Bitrate.HasValue && a.Bitrate == b.Bitrate &&
        a.SizeBytes.HasValue && a.SizeBytes == b.SizeBytes && a.ServerMd5 == b.ServerMd5 &&
        a.IsTrial.HasValue && a.IsTrial == b.IsTrial && a.TrialStartSeconds == b.TrialStartSeconds && a.TrialEndSeconds == b.TrialEndSeconds;
    internal static bool IsMp3File(string path)
    {
        using var stream = File.OpenRead(path);
        var header = new byte[10]; if (stream.Read(header, 0, header.Length) < 4) return false;
        long offset = 0;
        if (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
        {
            for (int i = 6; i < 10; i++) if (header[i] >= 128) return false;
            offset = 10L + (header[6] << 21) + (header[7] << 14) + (header[8] << 7) + header[9];
        }
        if (offset > stream.Length - 4) return false;
        stream.Position = offset;
        var scan = new byte[Math.Min(4096, stream.Length - offset)];
        int read = 0, n; while (read < scan.Length && (n = stream.Read(scan, read, scan.Length - read)) > 0) read += n;
        return IsMp3(scan);
    }
    public static Lease TryGet(long account, NeteasePlaybackSource source)
    {
        lock (IoGate)
        {
            try
            {
                string index = IndexPath(account, source);
                if (!source.IsTrial.HasValue || !source.Bitrate.HasValue || !source.SizeBytes.HasValue || !File.Exists(index)) return null;
                var entry = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(index));
                if (!Matches(entry, account, source) || string.IsNullOrEmpty(entry.File) || Path.GetFileName(entry.File) != entry.File) return null;
                string file = BridgePaths.ValidateWritePath(Path.Combine(Partition(account), entry.File));
                var info = new FileInfo(file);
                if (!info.Exists || info.Length != entry.Size || info.Length <= 0 || info.Length > MusicBridgeOptions.Current.Netease.AudioCacheMaximumFileBytes) return null;
                if (DigestFile(file) != entry.Hash) { Remove(account, source); return null; }
                File.SetLastAccessTimeUtc(index, DateTime.UtcNow); Pin(file);
                return new Lease { Uri = new Uri(file).AbsoluteUri, Index = index, File = file };
            }
            catch { BridgeLog.Warn("音频缓存读取失败，将从网络加载。"); return null; }
        }
    }
    internal static bool IsMp3(byte[] bytes)
    {
        if (bytes == null || bytes.Length < 4) return false;
        int start = 0;
        if (bytes.Length >= 10 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3')
        {
            for (int i = 6; i < 10; i++) if (bytes[i] >= 128) return false;
            start = 10 + (bytes[6] << 21) + (bytes[7] << 14) + (bytes[8] << 7) + bytes[9];
        }
        for (int i = start; i < Math.Min(bytes.Length - 3, start + 4096); i++)
            if (bytes[i] == 255 && (bytes[i + 1] & 224) == 224 && (bytes[i + 1] & 6) != 0 && (bytes[i + 2] & 240) != 0 && (bytes[i + 2] & 240) != 240) return true;
        return false;
    }
    // Called only after Unity has decoded the complete download successfully.
    public static void Store(long account, NeteasePlaybackSource source, byte[] bytes, Func<bool> current)
    {
        if (!source.IsTrial.HasValue || !source.Bitrate.HasValue || !source.SizeBytes.HasValue || bytes == null || bytes.LongLength > MusicBridgeOptions.Current.Netease.AudioCacheMaximumFileBytes || !IsMp3(bytes) ||
            (source.SizeBytes.HasValue && source.SizeBytes.Value != bytes.LongLength) || MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes == 0) return;
        lock (IoGate)
        {
            string file = null;
            try
            {
                if (!current()) return;
                string hash = Digest(bytes);
                string name = source.SongId + "-" + (source.IsTrial == true ? "trial" : "full") + "-" + (int)source.RequestedQuality + "-" + (source.Bitrate?.ToString() ?? "unknown") + "-" + hash + ".mp3";
                file = Path.Combine(Partition(account), name);
                string index = IndexPath(account, source);
                bool existed = System.IO.File.Exists(file);
                AtomicFile.WriteAllBytes(file, bytes);
                if (!current()) { if (!existed && !Pinned(file)) System.IO.File.Delete(file); return; }
                var entry = new Entry { Account = account, SongId = source.SongId, Requested = source.RequestedQuality, Bitrate = source.Bitrate,
                    Format = source.Format, File = name, Hash = hash, Size = bytes.LongLength, ServerMd5 = source.ServerMd5, ReturnedLevel = source.ReturnedLevel,
                    Trial = source.IsTrial, TrialStart = source.TrialStartSeconds, TrialEnd = source.TrialEndSeconds };
                AtomicFile.WriteAllText(index, JsonConvert.SerializeObject(entry));
                Evict();
            }
            catch { BridgeLog.Warn("音频缓存写入失败，本次播放不受影响。"); }
        }
    }
    public static void Remove(long account, NeteasePlaybackSource source)
    {
        lock (IoGate)
        {
            try
            {
                string index = IndexPath(account, source);
                if (!File.Exists(index)) return;
                var entry = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(index));
                File.Delete(BridgePaths.ValidateWritePath(index));
                if (entry != null && Path.GetFileName(entry.File) == entry.File)
                {
                    string file = BridgePaths.ValidateWritePath(Path.Combine(Partition(account), entry.File));
                    if (!Pinned(file) && File.Exists(file)) File.Delete(file);
                }
            }
            catch { BridgeLog.Warn("失效音频缓存清理失败。"); }
        }
    }
    private static void Evict()
    {
        if (!Directory.Exists(Root)) return;
        // Scan only our regular directories; never follow a user-created symlink.
        var files = new List<FileInfo>();
        Collect(new DirectoryInfo(Root), files);
        long total = files.Sum(f => f.Length);
        var referenced = new HashSet<string>();
        var entries = new List<Tuple<FileInfo, string>>();
        foreach (var info in files.Where(f => f.Extension == ".json"))
        {
            try
            {
                var entry = JsonConvert.DeserializeObject<Entry>(File.ReadAllText(info.FullName));
                if (entry != null && Path.GetFileName(entry.File) == entry.File)
                { string audio = Path.Combine(info.DirectoryName, entry.File); referenced.Add(audio); entries.Add(Tuple.Create(info, audio)); }
            }
            catch { }
        }
        foreach (var index in files.Where(f => f.Extension == ".json" && !entries.Any(e => e.Item1.FullName == f.FullName)))
        { long size = index.Length; File.Delete(BridgePaths.ValidateWritePath(index.FullName)); total -= size; }
        foreach (var info in files.Where(f => f.Extension != ".json" && !referenced.Contains(f.FullName)).OrderBy(f => f.LastAccessTimeUtc))
        {
            bool orphan = info.FullName.Contains(Path.DirectorySeparatorChar + "v2" + Path.DirectorySeparatorChar);
            if (!Pinned(info.FullName) && (total > MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes || orphan))
            { File.Delete(BridgePaths.ValidateWritePath(info.FullName)); total -= info.Length; }
        }
        foreach (var entry in entries.OrderBy(e => e.Item1.LastAccessTimeUtc))
        {
            if (total <= MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes) break;
            if (Pinned(entry.Item2)) continue;
            File.Delete(BridgePaths.ValidateWritePath(entry.Item1.FullName)); total -= entry.Item1.Length;
            var audio = new FileInfo(entry.Item2);
            if (audio.Exists) { long size = audio.Length; File.Delete(BridgePaths.ValidateWritePath(audio.FullName)); total -= size; }
        }
    }
    private static void Collect(DirectoryInfo directory, List<FileInfo> files)
    {
        foreach (var file in directory.GetFiles()) if ((file.Attributes & FileAttributes.ReparsePoint) == 0) files.Add(file);
        foreach (var child in directory.GetDirectories()) if ((child.Attributes & FileAttributes.ReparsePoint) == 0) Collect(child, files);
    }
}
