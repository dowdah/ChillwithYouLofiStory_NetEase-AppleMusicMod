using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Threading;
using Newtonsoft.Json;

namespace MusicBridge;

internal sealed class CacheUsageSnapshot
{
    public DateTime CompletedUtc;
    public long PersistentBytes, ActiveTemporaryBytes, StaleTemporaryBytes, ReclaimableBytes, ProtectedBytes, ResidualBytes;
    public int IndexedFiles, ProtectedFiles, ResidualFiles;
    public string Error;
}

internal sealed class CacheCleanupPlan
{
    internal sealed class Item
    {
        internal string Index, Audio, IndexText;
        internal long IndexBytes, AudioBytes;
    }
    internal readonly List<Item> Items = new List<Item>();
    internal readonly List<Tuple<string, string, long>> WorkItems = new List<Tuple<string, string, long>>();
    internal bool StaleWork;
    internal long Account;
    public Guid Id = Guid.NewGuid();
    public long EstimatedBytes;
    public int FileCount => Items.Count * 2 + WorkItems.Count;
}

internal sealed class CacheCleanupResult
{
    public int Deleted, Skipped, Failed;
    public long DeletedBytes;
    public string Error;
    public bool Temporary;
}

internal static partial class AudioDiskCache
{
    private static int _managementBusy;
    private static int _managementCancel;
    private static int _scanGeneration;
    private static int _scanActive;
    private static int _evictionRunning, _evictionRequested;
    internal static bool CleanupActive => Volatile.Read(ref _managementBusy) != 0;
    internal static bool ScanActive => Volatile.Read(ref _scanActive) != 0;
    internal static bool EvictionActive => Volatile.Read(ref _evictionRunning) != 0 ||
        Volatile.Read(ref _evictionRequested) != 0;
    internal static void CancelActiveCleanup() { Volatile.Write(ref _managementCancel, 1); }
    internal static CacheCleanupResult LastCleanupResult { get; private set; }
    internal static CacheUsageSnapshot LastUsageSnapshot { get; private set; }

    internal static void ScanAsync(long account, Action<CacheUsageSnapshot> completed)
    {
        int generation = Interlocked.Increment(ref _scanGeneration);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            CacheUsageSnapshot result;
            Interlocked.Increment(ref _scanActive);
            try { result = Scan(account, () => generation != Volatile.Read(ref _scanGeneration)); }
            catch (Exception ex) { result = new CacheUsageSnapshot { Error = ex is OperationCanceledException ? "扫描已取消" : ex.GetType().Name }; }
            finally { Interlocked.Decrement(ref _scanActive); }
            MainThreadDispatcher.Enqueue(() => completed?.Invoke(result));
        });
    }

    internal static void CancelScan() { Interlocked.Increment(ref _scanGeneration); }

    internal static void PlanAsync(long account, Action<CacheCleanupPlan, string> completed)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            CacheCleanupPlan plan = null; string error = null;
            try { plan = BuildCleanupPlan(account); }
            catch (Exception ex) { error = ex.GetType().Name; }
            MainThreadDispatcher.Enqueue(() => completed?.Invoke(plan, error));
        });
    }

    internal static void PlanStaleAsync(Action<CacheCleanupPlan, string> completed)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            CacheCleanupPlan plan = null; string error = null;
            try { plan = BuildStaleWorkPlan(); }
            catch (Exception ex) { error = ex.GetType().Name; }
            MainThreadDispatcher.Enqueue(() => completed?.Invoke(plan, error));
        });
    }

    internal static void ExecuteAsync(CacheCleanupPlan plan, Func<bool> stillCurrent, Action<CacheCleanupResult> completed)
    {
        if (Interlocked.CompareExchange(ref _managementBusy, 1, 0) != 0)
        {
            completed?.Invoke(new CacheCleanupResult { Error = "已有清理任务进行中" });
            return;
        }
        Volatile.Write(ref _managementCancel, 0);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            CacheCleanupResult result = null;
            try { result = plan != null && plan.StaleWork ? ExecuteStaleWork(plan) : ExecuteCleanup(plan, stillCurrent); }
            catch (Exception ex) { result = new CacheCleanupResult { Error = ex.GetType().Name }; }
            finally { LastCleanupResult = result; Volatile.Write(ref _managementBusy, 0); }
            MainThreadDispatcher.Enqueue(() => completed?.Invoke(result));
        });
    }

    private static bool RegularFile(string path)
    {
        string checkedPath = BridgePaths.ValidateWritePath(path);
        if (!File.Exists(checkedPath)) return false;
        return (File.GetAttributes(checkedPath) & FileAttributes.ReparsePoint) == 0;
    }

    private static bool KnownAudio(string name) =>
        name != null && (name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) ||
                         name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) &&
        name.IndexOf('-') > 0 && name == System.IO.Path.GetFileName(name);

    private static bool OwnedIndex(Entry entry, string index)
    {
        if (entry == null || entry.SongId <= 0 || !entry.Trial.HasValue || !entry.Bitrate.HasValue ||
            string.IsNullOrEmpty(entry.Hash) || entry.Hash.Length != 64 || !KnownAudio(entry.File)) return false;
        string song = entry.SongId.ToString(CultureInfo.InvariantCulture);
        string quality = ((int)entry.Requested).ToString(CultureInfo.InvariantCulture);
        string trial = entry.Trial.Value ? "trial" : "full";
        string expectedIndex = song + "-" + quality + "-" + trial + ".json";
        string expectedAudio = song + "-" + trial + "-" + quality + "-" +
            entry.Bitrate.Value.ToString(CultureInfo.InvariantCulture) + "-" + entry.Hash +
            (entry.Format == "flac" ? ".flac" : entry.Format == "mp3" ? ".mp3" : "");
        return string.Equals(System.IO.Path.GetFileName(index), expectedIndex, StringComparison.Ordinal) &&
            string.Equals(entry.File, expectedAudio, StringComparison.Ordinal);
    }

    private static List<CacheCleanupPlan.Item> Indexed(long account, out long residualBytes, out int residualFiles,
        Func<bool> cancelled = null)
    {
        residualBytes = 0; residualFiles = 0;
        string partition = Partition(account);
        var result = new List<CacheCleanupPlan.Item>();
        if (!Directory.Exists(partition)) return result;
        if ((File.GetAttributes(BridgePaths.ValidateWritePath(partition)) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("缓存分区是链接，已拒绝扫描");
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(partition, "*", SearchOption.TopDirectoryOnly))
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            bool regular;
            try { regular = RegularFile(path); }
            catch { residualFiles++; continue; }
            if (!regular) { residualFiles++; continue; }
            files.Add(path);
        }
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (string index in files.Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            try
            {
                string text = File.ReadAllText(index);
                var entry = JsonConvert.DeserializeObject<Entry>(text);
                if (entry == null || entry.Version != 2 || entry.Account != account ||
                    !OwnedIndex(entry, index)) throw new InvalidDataException();
                string audio = BridgePaths.ValidateWritePath(System.IO.Path.Combine(partition, entry.File));
                if (!files.Contains(audio) || !RegularFile(audio)) throw new InvalidDataException();
                long length = new FileInfo(audio).Length;
                if (length != entry.Size || length <= 0) throw new InvalidDataException();
                referenced.Add(index); referenced.Add(audio);
                result.Add(new CacheCleanupPlan.Item { Index = index, Audio = audio, IndexText = text,
                    IndexBytes = new FileInfo(index).Length, AudioBytes = length });
            }
            catch { /* damaged entries remain visible as residuals; never auto-delete */ }
        }
        foreach (string path in files)
        {
            if (cancelled != null && cancelled()) throw new OperationCanceledException();
            if (!referenced.Contains(path)) { residualBytes += new FileInfo(path).Length; residualFiles++; }
        }
        return result;
    }

    internal static CacheUsageSnapshot Scan(long account, Func<bool> cancelled = null)
    {
            var result = new CacheUsageSnapshot();
            var entries = Indexed(account, out result.ResidualBytes, out result.ResidualFiles, cancelled);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var counts = entries.GroupBy(e => e.Audio).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var item in entries)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                result.PersistentBytes += item.IndexBytes;
                bool first = seen.Add(item.Audio);
                if (first) result.PersistentBytes += item.AudioBytes;
                long bytes = item.IndexBytes + (first ? item.AudioBytes : 0);
                bool protectedFile = Pinned(item.Audio) || counts[item.Audio] != 1;
                if (protectedFile) { result.ProtectedFiles++; result.ProtectedBytes += bytes; }
                else result.ReclaimableBytes += bytes;
                result.IndexedFiles++;
            }
            string work = System.IO.Path.Combine(Root, "v2", "work");
            if (Directory.Exists(work) && (File.GetAttributes(BridgePaths.ValidateWritePath(work)) & FileAttributes.ReparsePoint) == 0)
                foreach (string path in Directory.EnumerateFiles(work, "*", SearchOption.TopDirectoryOnly))
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                    bool regular;
                    try { regular = RegularFile(path); }
                    catch { result.ResidualFiles++; continue; }
                    if (regular)
                    {
                        if (System.IO.Path.GetFileName(path).StartsWith("session-", StringComparison.Ordinal) &&
                            path.EndsWith(".lock", StringComparison.Ordinal)) continue;
                        long bytes = new FileInfo(path).Length;
                        if (Pinned(path)) result.ActiveTemporaryBytes += bytes;
                        else
                        {
                            result.ResidualBytes += bytes; result.ResidualFiles++;
                            if (TryWorkId(path, out string id) && TemporarySessionRegistry.TryAcquireInactive(work, id, out int handle))
                            { result.StaleTemporaryBytes += bytes; TemporarySessionRegistry.Close(handle); }
                        }
                    }
                }
            result.CompletedUtc = DateTime.UtcNow;
            LastUsageSnapshot = result;
            return result;
    }

    internal static CacheCleanupPlan BuildCleanupPlan(long account)
    {
            var plan = new CacheCleanupPlan { Account = account };
            var entries = Indexed(account, out _, out _);
            var counts = entries.GroupBy(e => e.Audio).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            foreach (var item in entries)
                if (!Pinned(item.Audio) && counts[item.Audio] == 1)
                { plan.Items.Add(item); plan.EstimatedBytes += item.IndexBytes + item.AudioBytes; }
            return plan;
    }

    private static bool TryWorkId(string path, out string id)
    {
        string name = System.IO.Path.GetFileName(path);
        id = name.Length == 70 && name[32] == '-' && name.EndsWith(".part", StringComparison.Ordinal) ?
            name.Substring(0, 32) : null;
        return id != null && Guid.TryParseExact(id, "N", out _);
    }

    internal static CacheCleanupPlan BuildStaleWorkPlan()
    {
            var plan = new CacheCleanupPlan { StaleWork = true };
            string work = System.IO.Path.Combine(Root, "v2", "work");
            if (!Directory.Exists(work)) return plan;
            if ((File.GetAttributes(BridgePaths.ValidateWritePath(work)) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("活动文件目录是符号链接");
            foreach (string path in Directory.EnumerateFiles(work, "*.part", SearchOption.TopDirectoryOnly))
            {
                bool regular;
                try { regular = RegularFile(path); }
                catch { continue; }
                if (!regular || Pinned(path) || !TryWorkId(path, out string id)) continue;
                if (!TemporarySessionRegistry.TryAcquireInactive(work, id, out int handle)) continue;
                TemporarySessionRegistry.Close(handle);
                long bytes = new FileInfo(path).Length;
                plan.WorkItems.Add(Tuple.Create(path, id, bytes));
                plan.EstimatedBytes += bytes;
            }
            return plan;
    }

    internal static CacheCleanupResult ExecuteStaleWork(CacheCleanupPlan plan)
    {
        var result = new CacheCleanupResult { Temporary = true };
        string work = System.IO.Path.Combine(Root, "v2", "work");
        var touchedSessions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plan.WorkItems)
        {
            if (Volatile.Read(ref _managementCancel) != 0) { result.Skipped++; continue; }
            lock (IoGate)
            {
                int handle = -1;
                try
                {
                    if (!RegularFile(item.Item1) || Pinned(item.Item1) ||
                        new FileInfo(item.Item1).Length != item.Item3 ||
                        !TemporarySessionRegistry.TryAcquireInactive(work, item.Item2, out handle))
                    { result.Skipped++; continue; }
                    if (SafeCacheFiles.DeleteRegular(item.Item1))
                    { result.Deleted++; result.DeletedBytes += item.Item3; touchedSessions.Add(item.Item2); }
                    else result.Skipped++;
                }
                catch { result.Failed++; }
                finally { if (handle >= 0) TemporarySessionRegistry.Close(handle); }
            }
        }
        foreach (string session in touchedSessions)
        {
            int handle = -1;
            try
            {
                lock (IoGate)
                {
                    if (!TemporarySessionRegistry.TryAcquireInactive(work, session, out handle)) continue;
                    if (!Directory.EnumerateFiles(work, session + "-*.part", SearchOption.TopDirectoryOnly).Any())
                        SafeCacheFiles.DeleteRegular(System.IO.Path.Combine(work, "session-" + session + ".lock"));
                }
            }
            catch { /* zero-byte marker can be revisited by a later cleanup */ }
            finally { if (handle >= 0) TemporarySessionRegistry.Close(handle); }
        }
        return result;
    }

    internal static CacheCleanupResult ExecuteCleanup(CacheCleanupPlan plan, Func<bool> stillCurrent = null)
    {
        var result = new CacheCleanupResult();
        if (plan == null) { result.Error = "没有待确认的清理计划"; return result; }
        foreach (var item in plan.Items)
        {
            if (Volatile.Read(ref _managementCancel) != 0) { result.Skipped += 2; continue; }
            if (stillCurrent != null && !stillCurrent()) { result.Skipped += 2; continue; }
            lock (IoGate)
            {
                try
                {
                    if (!RegularFile(item.Index) || !RegularFile(item.Audio) || Pinned(item.Audio) ||
                        File.ReadAllText(item.Index) != item.IndexText ||
                        new FileInfo(item.Audio).Length != item.AudioBytes)
                    { result.Skipped += 2; continue; }
                    // IoGate serializes cache publication, lease acquisition and eviction.
                    if (SafeCacheFiles.DeleteRegular(item.Audio))
                    { result.Deleted++; result.DeletedBytes += item.AudioBytes; }
                    else result.Skipped++;
                }
                catch { result.Failed++; result.Skipped++; continue; }
                try
                {
                    if (SafeCacheFiles.DeleteRegular(item.Index))
                    { result.Deleted++; result.DeletedBytes += item.IndexBytes; }
                    else result.Skipped++;
                }
                catch { result.Failed++; }
            }
        }
        return result;
    }

    internal static void EnforceCapacityAsync()
    {
        Volatile.Write(ref _evictionRequested, 1);
        if (Interlocked.CompareExchange(ref _evictionRunning, 1, 0) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _evictionRequested, 0);
                    try { EvictSafely(); }
                    catch (Exception ex) { BridgeLog.Warn("音频缓存容量整理未完成（" + ex.GetType().Name + "）。"); }
                }
                while (Volatile.Read(ref _evictionRequested) != 0);
            }
            finally
            {
                Volatile.Write(ref _evictionRunning, 0);
                if (Interlocked.Exchange(ref _evictionRequested, 0) != 0) EnforceCapacityAsync();
            }
        });
    }

    // Called under IoGate. Unknown files and active leases never enter automatic eviction.
    private static void EvictSafely()
    {
        string accounts = System.IO.Path.Combine(Root, "v2", "netease");
        if (!Directory.Exists(accounts)) return;
        if ((File.GetAttributes(BridgePaths.ValidateWritePath(accounts)) & FileAttributes.ReparsePoint) != 0) return;
        var all = new List<CacheCleanupPlan.Item>();
        foreach (string directory in Directory.GetDirectories(accounts, "*", SearchOption.TopDirectoryOnly))
        {
            if (!long.TryParse(System.IO.Path.GetFileName(directory), out long account)) continue;
            if ((File.GetAttributes(BridgePaths.ValidateWritePath(directory)) & FileAttributes.ReparsePoint) != 0) continue;
            all.AddRange(Indexed(account, out _, out _));
        }
        var counts = all.GroupBy(e => e.Audio).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        long total = all.Sum(e => e.IndexBytes) + all.GroupBy(e => e.Audio).Sum(g => g.First().AudioBytes);
        long capacity = MusicBridgeOptions.Current.Netease.AudioCacheCapacityBytes;
        foreach (var item in all.OrderBy(e => File.GetLastAccessTimeUtc(e.Index)))
        {
            if (total <= capacity) break;
            if (counts[item.Audio] != 1) continue;
            lock (IoGate)
            {
                try
                {
                    if (Pinned(item.Audio) || !RegularFile(item.Index) || !RegularFile(item.Audio) ||
                        File.ReadAllText(item.Index) != item.IndexText) continue;
                    if (SafeCacheFiles.DeleteRegular(item.Index)) total -= item.IndexBytes;
                    if (SafeCacheFiles.DeleteRegular(item.Audio)) total -= item.AudioBytes;
                }
                catch { BridgeLog.Warn("音频缓存容量整理跳过一个文件。"); }
            }
        }
    }
}
