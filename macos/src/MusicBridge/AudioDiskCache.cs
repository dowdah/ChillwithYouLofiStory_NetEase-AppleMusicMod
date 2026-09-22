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
        public int? Bitrate;
        public string Format, File, Hash, ServerMd5;
        public bool? Trial;
        public double? TrialStart, TrialEnd;
    }
    internal sealed class Lease : IDisposable
    {
        public string Uri { get; internal set; }
        internal string Index, File;
        public void Dispose() { lock (Gate) { if (File != null) { if (Pins.TryGetValue(File, out int count) && count > 1) Pins[File] = count - 1; else Pins.Remove(File); File = null; } } }
    }
    private static readonly object Gate = new object();
    private static readonly Dictionary<string, int> Pins = new Dictionary<string, int>(StringComparer.Ordinal);
    internal static string Root => BridgePaths.Resolve("cache", "audio");
    private static string Partition(long account) => Path.Combine(Root, "v2", "netease", account.ToString(System.Globalization.CultureInfo.InvariantCulture));
    private static string IndexPath(long account, NeteasePlaybackSource source) => Path.Combine(Partition(account), source.SongId + "-" + (int)source.RequestedQuality + "-" + (source.IsTrial == true ? "trial" : source.IsTrial == false ? "full" : "unknown") + ".json");
    private static string Digest(byte[] bytes) { using var hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    internal static bool Matches(Entry entry, long account, NeteasePlaybackSource source) =>
        entry != null && entry.Version == 2 && entry.Account == account && entry.SongId == source.SongId && entry.Requested == source.RequestedQuality &&
        entry.Bitrate == source.Bitrate && entry.Format == source.Format && entry.Trial == source.IsTrial && entry.TrialStart == source.TrialStartSeconds && entry.TrialEnd == source.TrialEndSeconds &&
        (!source.SizeBytes.HasValue || entry.Size == source.SizeBytes) && entry.ServerMd5 == source.ServerMd5;
    public static Lease TryGet(long account, NeteasePlaybackSource source)
    {
        lock (Gate)
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
                if (Digest(File.ReadAllBytes(file)) != entry.Hash) { Remove(account, source); return null; }
                Pins.TryGetValue(file, out int count); Pins[file] = count + 1; File.SetLastAccessTimeUtc(index, DateTime.UtcNow);
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
        lock (Gate)
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
                if (!current()) { if (!existed && !Pins.ContainsKey(file)) System.IO.File.Delete(file); return; }
                var entry = new Entry { Account = account, SongId = source.SongId, Requested = source.RequestedQuality, Bitrate = source.Bitrate,
                    Format = source.Format, File = name, Hash = hash, Size = bytes.LongLength, ServerMd5 = source.ServerMd5,
                    Trial = source.IsTrial, TrialStart = source.TrialStartSeconds, TrialEnd = source.TrialEndSeconds };
                AtomicFile.WriteAllText(index, JsonConvert.SerializeObject(entry));
                Evict();
            }
            catch { BridgeLog.Warn("音频缓存写入失败，本次播放不受影响。"); }
        }
    }
    public static void Remove(long account, NeteasePlaybackSource source)
    {
        lock (Gate)
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
                    if (!Pins.ContainsKey(file) && File.Exists(file)) File.Delete(file);
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
            if (!Pins.ContainsKey(info.FullName) && (total > MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes || orphan))
            { File.Delete(BridgePaths.ValidateWritePath(info.FullName)); total -= info.Length; }
        }
        foreach (var entry in entries.OrderBy(e => e.Item1.LastAccessTimeUtc))
        {
            if (total <= MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes) break;
            if (Pins.ContainsKey(entry.Item2)) continue;
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
