using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace MusicBridge;

// Writes a FLAC from the beginning and publishes only flushed bytes. The caller owns
// the temporary lease; it may be released only after this worker and its PCM reader end.
internal sealed class ProgressiveFlacDownload : IDisposable
{
    private readonly NeteaseAccountContext _context;
    private readonly NeteasePlaybackSource _source;
    private readonly AudioDiskCache.Lease _file;
    private readonly GrowingFlacFile _growth;
    private readonly NeteaseRequestCancellation _cancel = new NeteaseRequestCancellation();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _done, _validated, _downloaded, _timeoutKind;
    public bool Done => Volatile.Read(ref _done) != 0;
    public bool Validated => Volatile.Read(ref _validated) != 0;
    public bool Downloaded => Volatile.Read(ref _downloaded) != 0;
    public string Error { get; private set; }
    public bool DecodeFailure { get; private set; }
    public bool NetworkFailure { get; private set; }
    public bool Retryable { get; private set; }
    public bool RefreshUrl { get; private set; }
    public double ElapsedSeconds => _watch.Elapsed.TotalSeconds;
    public double DownloadSeconds { get; private set; }
    public double ValidationSeconds { get; private set; }
    public PcmFormat Format { get; private set; }
    public ProgressiveFlacDownload(NeteaseAccountContext context, NeteasePlaybackSource source,
        AudioDiskCache.Lease file, GrowingFlacFile growth)
    {
        _context = context; _source = source; _file = file; _growth = growth;
        // Opening an empty file first lets the decoder attach without racing path creation.
        using (new FileStream(BridgePaths.ValidateWritePath(file.Path), FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite)) { }
        new Thread(Work) { IsBackground = true, Name = "MusicBridge-FLAC-progressive-download" }.Start();
    }
    private bool Cancelled => _cancel.IsCancelled || !_context.Active;
    private void CheckCancellation() { if (Cancelled) throw new OperationCanceledException(); }
    private void Work()
    {
        try
        {
            if (!_context.Register(_cancel)) throw new OperationCanceledException();
            Download();
            DownloadSeconds = _watch.Elapsed.TotalSeconds;
            Volatile.Write(ref _downloaded, 1);
            _growth.Finish();
            CheckCancellation();
            var validation = Stopwatch.StartNew();
            Format = NativeFlacDecoder.Validate(_file.Path, () => Cancelled);
            ValidationSeconds = validation.Elapsed.TotalSeconds;
            Format.Apply(_source);
            CheckCancellation();
            // Only a complete and decoded file may acquire a persistent cache index.
            AudioDiskCache.StoreValidatedFile(_context.UserId, _source, _file, () => !Cancelled);
            CheckCancellation();
            Volatile.Write(ref _validated, 1);
        }
        catch (OperationCanceledException) { Error = "操作已取消"; }
        catch (WebException ex)
        {
            NetworkFailure = true; Retryable = AudioFilePreparation.Transient(ex.Status);
            var response = ex.Response as HttpWebResponse;
            int status = response == null ? 0 : (int)response.StatusCode;
            RefreshUrl = status == 401 || status == 403 || status == 410 ||
                (_source.ExpiresAtUtc.HasValue && DateTime.UtcNow >= _source.ExpiresAtUtc.Value);
            Error = Volatile.Read(ref _timeoutKind) == 2 ? "音频下载停滞，请重试" :
                Volatile.Read(ref _timeoutKind) == 1 ? "音频下载超时，请重试" :
                status > 0 ? "音频服务器返回错误（HTTP " + status + "），请重试" : "音频连接中断，请检查网络后重试";
            if (!Cancelled) BridgeLog.Warn("FLAC流式下载未完成 songId=" + _source.SongId +
                " status=" + ex.Status + " http=" + status + " deadline=" + Volatile.Read(ref _timeoutKind));
            response?.Dispose();
        }
        catch (InvalidDataException ex) { DecodeFailure = true; Error = "FLAC完整性校验失败（" + ex.GetType().Name + "）"; }
        catch (Exception ex) { Error = "FLAC流式文件准备失败（" + ex.GetType().Name + "）"; }
        finally
        {
            if (Error != null) _growth.Fail(Error);
            _context.Release(_cancel);
            Volatile.Write(ref _done, 1);
        }
    }
    private void Download()
    {
        var options = MusicBridgeOptions.Current.Netease;
        long limit = options.FlacMaximumDownloadBytes;
        if (_source.SizeBytes > limit) throw new InvalidDataException("FLAC超出下载上限");
        var request = (HttpWebRequest)WebRequest.Create(_source.Url);
        request.Method = "GET"; request.KeepAlive = false;
        request.Timeout = Math.Max(1, (int)options.FlacRequestTimeout.TotalMilliseconds);
        request.ReadWriteTimeout = (int)options.AudioStallTimeout.TotalMilliseconds;
        request.UserAgent = "MusicBridge/1.3";
        _cancel.Attach(request);
        long lastProgress = 0; int timeoutKind = 0;
        using var deadline = new Timer(_ => {
            long elapsed = _watch.ElapsedMilliseconds;
            int reason = elapsed >= options.FlacRequestTimeout.TotalMilliseconds ? 1 :
                elapsed - Interlocked.Read(ref lastProgress) >= options.AudioStallTimeout.TotalMilliseconds ? 2 : 0;
            if (reason != 0) { Volatile.Write(ref timeoutKind, reason); try { request.Abort(); } catch { } }
        }, null, 250, 250);
        try
        {
            using var response = (HttpWebResponse)request.GetResponse();
            if (response.ContentLength > limit) throw new InvalidDataException("FLAC超出下载上限");
            using var input = response.GetResponseStream();
            using var output = new FileStream(BridgePaths.ValidateWritePath(_file.Path), FileMode.Open,
                FileAccess.Write, FileShare.ReadWrite, 65536);
            using var digest = MD5.Create();
            var buffer = new byte[65536]; long total = 0; int count;
            while (true)
            {
                try { count = input.Read(buffer, 0, buffer.Length); }
                catch (IOException ex) { throw new WebException("Audio response read failed", ex, WebExceptionStatus.ReceiveFailure, null); }
                if (count == 0) break;
                CheckCancellation();
                total += count;
                if (total > limit) throw new InvalidDataException("FLAC超出下载上限");
                output.Write(buffer, 0, count);
                digest.TransformBlock(buffer, 0, count, buffer, 0);
                output.Flush();
                _growth.Publish(total);
                Interlocked.Exchange(ref lastProgress, _watch.ElapsedMilliseconds);
            }
            digest.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            if ((_source.SizeBytes.HasValue && total < _source.SizeBytes) ||
                (response.ContentLength >= 0 && total < response.ContentLength))
                throw new WebException("Incomplete audio response", WebExceptionStatus.ReceiveFailure);
            if (total == 0 || (_source.SizeBytes.HasValue && total != _source.SizeBytes))
                throw new InvalidDataException("FLAC下载大小不符");
            string md5 = BitConverter.ToString(digest.Hash).Replace("-", "");
            if (!string.IsNullOrEmpty(_source.ServerMd5) &&
                !string.Equals(md5, _source.ServerMd5, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FLAC压缩文件摘要不符");
            output.Flush(true);
        }
        catch (WebException) { Volatile.Write(ref _timeoutKind, Volatile.Read(ref timeoutKind)); throw; }
        finally { _cancel.Detach(request); }
    }
    public void Dispose() { _cancel.Cancel(); _growth.Close(); }
}
