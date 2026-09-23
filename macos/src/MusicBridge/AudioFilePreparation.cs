using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace MusicBridge;

// A cancellable worker owns the file until TakeFile transfers its lease to the PCM stream.
internal sealed class AudioFilePreparation : IDisposable
{
    private readonly object _gate = new object();
    private readonly NeteaseAccountContext _context;
    private readonly NeteasePlaybackSource _source;
    private readonly NeteaseRequestCancellation _cancel = new NeteaseRequestCancellation();
    private AudioDiskCache.Lease _file;
    private readonly bool _fromCache, _publishCache;
    private int _done, _timeoutKind;
    public bool Done => Volatile.Read(ref _done) != 0;
    public string Error { get; private set; }
    public bool DecodeFailure { get; private set; }
    public bool RefreshUrl { get; private set; }
    public string NetworkStatus { get; private set; }
    public int RetryCount { get; private set; }
    public double DownloadSeconds { get; private set; }
    public double ValidationSeconds { get; private set; }
    public PcmFormat Format { get; private set; }
    public AudioFilePreparation(NeteaseAccountContext context, NeteasePlaybackSource source, AudioDiskCache.Lease cached, bool publishCache = true)
    {
        _context = context; _source = source; _file = cached; _fromCache = cached != null; _publishCache = publishCache;
        new Thread(Work) { IsBackground = true, Name = "MusicBridge-FLAC-file" }.Start();
    }
    private bool Cancelled => _cancel.IsCancelled || !_context.Active;
    private void CheckCancellation() { if (Cancelled) throw new OperationCanceledException(); }
    private void Work()
    {
        try
        {
            if (!_context.Register(_cancel)) throw new OperationCanceledException();
            CheckCancellation();
            if (_file == null)
            {
                _file = AudioDiskCache.CreateTemporary();
                var watch = Stopwatch.StartNew();
                TimeSpan budget = _source.IsFlac ? MusicBridgeOptions.Current.Netease.FlacRequestTimeout : MusicBridgeOptions.Current.Netease.AudioRequestTimeout;
                for (int attempt = 0; ; attempt++)
                {
                    CheckCancellation();
                    try { Download(_file.Path, budget - watch.Elapsed); break; }
                    catch (WebException ex) when (attempt == 0 && !Cancelled && budget - watch.Elapsed > TimeSpan.FromSeconds(1) && Transient(ex.Status))
                    {
                        RetryCount = 1;
                        BridgeLog.Warn("短暂音频网络错误 songId=" + _source.SongId + " status=" + ex.Status + "，同地址重试一次，不降级或跳歌。");
                        ex.Response?.Dispose();
                        File.Delete(BridgePaths.ValidateWritePath(_file.Path));
                        Thread.Sleep(250);
                    }
                }
                DownloadSeconds = watch.Elapsed.TotalSeconds;
            }
            CheckCancellation();
            var validation = Stopwatch.StartNew();
            try
            {
                if (_source.IsFlac)
                {
                    if (_file.VerifiedFormat != null)
                    {
                        if (AudioDiskCache.DigestFile(_file.Path) != _file.VerifiedHash) throw new InvalidDataException("Prefetch file changed");
                        Format = _file.VerifiedFormat;
                    }
                    else Format = NativeFlacDecoder.Validate(_file.Path, () => Cancelled);
                    _file.VerifiedFormat = Format;
                    _file.VerifiedHash = AudioDiskCache.DigestFile(_file.Path);
                }
                else if (!_publishCache && _source.Format == "mp3")
                {
                    if (!AudioDiskCache.IsMp3File(_file.Path)) throw new InvalidDataException("MP3 prefetch header mismatch");
                }
                else throw new InvalidDataException("Unsupported prepared format");
            }
            catch (OperationCanceledException) { throw; }
            catch { DecodeFailure = true; throw; }
            ValidationSeconds = validation.Elapsed.TotalSeconds;
            Format?.Apply(_source);
            if (!_fromCache && _publishCache && _source.IsFlac) AudioDiskCache.StoreValidatedFile(_context.UserId, _source, _file, () => !Cancelled);
            CheckCancellation();
        }
        catch (OperationCanceledException) { Error = "操作已取消"; }
        catch (WebException ex)
        {
            NetworkStatus = ex.Status.ToString();
            var response = ex.Response as HttpWebResponse;
            int status = response == null ? 0 : (int)response.StatusCode;
            RefreshUrl = status == 401 || status == 403 || status == 410 || (_source.ExpiresAtUtc.HasValue && DateTime.UtcNow >= _source.ExpiresAtUtc.Value);
            Error = Volatile.Read(ref _timeoutKind) == 2 ? "音频下载停滞，请重试" : Volatile.Read(ref _timeoutKind) == 1 ? "音频下载超时，请重试" :
                status > 0 ? "音频服务器返回错误（HTTP " + status + "），请重试" : "音频连接中断，请检查网络后重试";
            if (!Cancelled) BridgeLog.Warn("音频下载未完成 songId=" + _source.SongId + " status=" + NetworkStatus + " http=" + status +
                " deadline=" + Volatile.Read(ref _timeoutKind) + " retries=" + RetryCount);
            response?.Dispose();
        }
        catch (Exception ex) { Error = (DecodeFailure ? "FLAC解码或完整性校验失败" : "FLAC文件准备失败") + "（" + ex.GetType().Name + "）"; }
        finally
        {
            if (_fromCache && DecodeFailure && !Cancelled) AudioDiskCache.Remove(_context.UserId, _source);
            _context.Release(_cancel);
            lock (_gate)
            {
                if (Cancelled || Error != null) { _file?.Dispose(); _file = null; }
                Volatile.Write(ref _done, 1);
            }
        }
    }
    internal static bool Transient(WebExceptionStatus status) => status == WebExceptionStatus.ConnectFailure ||
        status == WebExceptionStatus.ConnectionClosed || status == WebExceptionStatus.KeepAliveFailure ||
        status == WebExceptionStatus.ReceiveFailure || status == WebExceptionStatus.SendFailure ||
        status == WebExceptionStatus.Timeout || status == WebExceptionStatus.RequestCanceled;
    private void Download(string path, TimeSpan totalTimeout)
    {
        var options = MusicBridgeOptions.Current.Netease;
        long maximumBytes = _source.IsFlac ? options.FlacMaximumDownloadBytes : options.AudioCacheMaximumFileBytes;
        if (totalTimeout <= TimeSpan.Zero) throw new WebException("Audio deadline", WebExceptionStatus.Timeout);
        if (_source.SizeBytes > maximumBytes) throw new IOException("FLAC exceeds download limit");
        var request = (HttpWebRequest)WebRequest.Create(_source.Url);
        request.Method = "GET"; request.Timeout = Math.Max(1, (int)totalTimeout.TotalMilliseconds);
        // Each track is a long-lived operation separated by minutes. Avoid reusing a
        // stale idle CDN connection on the game's Mono HTTP stack.
        request.KeepAlive = false;
        request.ReadWriteTimeout = (int)options.AudioStallTimeout.TotalMilliseconds;
        request.UserAgent = "MusicBridge/1.4";
        _cancel.Attach(request);
        // Cover connect/first-byte stalls too; ReadWriteTimeout only covers stream I/O.
        var watch = Stopwatch.StartNew(); long lastProgress = 0; int timeoutKind = 0;
        using var deadline = new Timer(_ => {
            long elapsed = watch.ElapsedMilliseconds;
            int reason = elapsed >= totalTimeout.TotalMilliseconds ? 1 :
                elapsed - Interlocked.Read(ref lastProgress) >= options.AudioStallTimeout.TotalMilliseconds ? 2 : 0;
            if (reason != 0) { Volatile.Write(ref timeoutKind, reason); try { request.Abort(); } catch { } }
        }, null, 250, 250);
        try
        {
            using var response = (HttpWebResponse)request.GetResponse();
            if (response.ContentLength > maximumBytes) throw new IOException("FLAC exceeds download limit");
            using var input = response.GetResponseStream();
            using var output = new FileStream(BridgePaths.ValidateWritePath(path), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            using var digest = MD5.Create();
            var buffer = new byte[65536]; long total = 0; int n;
            while (true)
            {
                try { n = input.Read(buffer, 0, buffer.Length); }
                catch (IOException ex) { throw new WebException("Audio response read failed", ex, WebExceptionStatus.ReceiveFailure, null); }
                if (n == 0) break;
                CheckCancellation(); Interlocked.Exchange(ref lastProgress, watch.ElapsedMilliseconds); total += n;
                if (total > maximumBytes) throw new IOException("FLAC exceeds download limit");
                output.Write(buffer, 0, n); digest.TransformBlock(buffer, 0, n, buffer, 0);
            }
            digest.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if ((_source.SizeBytes.HasValue && total < _source.SizeBytes) || (response.ContentLength >= 0 && total < response.ContentLength))
                throw new WebException("Incomplete audio response", WebExceptionStatus.ReceiveFailure);
            if (total == 0 || (_source.SizeBytes.HasValue && total != _source.SizeBytes)) throw new InvalidDataException("Audio download size mismatch");
            string md5 = BitConverter.ToString(digest.Hash).Replace("-", "");
            if (!string.IsNullOrEmpty(_source.ServerMd5) && !string.Equals(md5, _source.ServerMd5, StringComparison.OrdinalIgnoreCase))
            { DecodeFailure = true; throw new InvalidDataException("FLAC download digest mismatch"); }
            output.Flush(true);
        }
        catch (WebException) { Volatile.Write(ref _timeoutKind, Volatile.Read(ref timeoutKind)); throw; }
        finally { _cancel.Detach(request); }
    }
    public AudioDiskCache.Lease TakeFile()
    {
        lock (_gate)
        {
            if (!Done || Cancelled || Error != null) return null;
            var file = _file; _file = null; return file;
        }
    }
    public void Dispose()
    {
        _cancel.Cancel();
        lock (_gate) { if (Done) { _file?.Dispose(); _file = null; } }
    }
}
