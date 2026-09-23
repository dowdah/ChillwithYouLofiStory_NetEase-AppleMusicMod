using System;
using System.Collections;
using System.Diagnostics;
using UnityEngine;

namespace MusicBridge;

internal sealed partial class AudioPlayer
{
    private AudioFilePreparation _flacPreparation;
    private FlacPcmStream _flacStream;
    private ProgressiveFlacSession _progressiveSession;
    private bool _progressiveRefreshedUrl;
    private bool _flacAwaitingValidation, _flacFirstPcmLogged;
    private bool _flacHasDeferredSeek;
    private long _flacDeferredSeekFrame;
    private long _flacLoadStartedTicks;
    private bool _flacWantsPlay;
    private float _flacBufferStarted, _flacClipBase;
    public bool IsBuffering { get; private set; }

    private IEnumerator LoadFlac(TrackInfo track, int gen, NeteaseAccountContext context,
        NeteasePlaybackSource source, AudioDiskCache.Lease lease, float resume,
        bool fromCache, bool bypassCache, bool refreshedUrl, NeteaseQuality quality, float loadStarted,
        bool forceComplete = false, TimeSpan? downloadBudget = null, bool allowDownloadRetry = true)
    {
        if (!forceComplete && !fromCache && lease == null && !bypassCache && resume <= 0.05f &&
            MusicBridgeOptions.Current.Netease.StreamFlacDuringDownload)
        {
            yield return LoadProgressiveFlac(track, gen, context, source, quality, refreshedUrl, loadStarted);
            yield break;
        }
        float started = Time.realtimeSinceStartup;
        var preparation = new AudioFilePreparation(context, source, lease,
            publishCache: true, budgetOverride: downloadBudget, allowRetry: allowDownloadRetry);
        _flacPreparation = preparation;
        while (!preparation.Done)
        {
            if (gen != _generation || !context.Active) { preparation.Dispose(); yield break; }
            yield return null;
        }
        if (gen != _generation || !context.Active) { preparation.Dispose(); yield break; }
        _flacPreparation = null;
        if (preparation.Error != null)
        {
            preparation.Dispose();
            if (fromCache)
            {
                _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resume, _playAfterLoad, true, refreshedUrl, quality,
                    forceComplete, downloadBudget, allowDownloadRetry));
            }
            else if (preparation.RefreshUrl && !refreshedUrl)
                _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resume, _playAfterLoad, bypassCache, true, quality,
                    forceComplete, downloadBudget, allowDownloadRetry));
            else if (preparation.DecodeFailure && NeteaseQualityPolicy.Lower(quality) is NeteaseQuality lower)
                _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resume, _playAfterLoad, false, false, lower));
            else FailFlac(gen, preparation.Error, preparation.DecodeFailure);
            yield break;
        }
        var file = preparation.TakeFile(); preparation.Dispose();
        if (file == null) { FailFlac(gen, "FLAC加载已取消", false); yield break; }
        var format = preparation.Format;
        float position = Mathf.Clamp(resume, 0, (float)Math.Max(0, format.Duration - 0.05));
        float prefillStarted = Time.realtimeSinceStartup;
        var stream = new FlacPcmStream(file.Path, file, (long)(position * format.SampleRate));
        _flacStream = stream;
        while (!stream.Ready && stream.Error == null && !stream.Released)
        {
            if (gen != _generation || !context.Active) { stream.Dispose(); yield break; }
            yield return null;
        }
        if (gen != _generation || !context.Active) { stream.Dispose(); yield break; }
        string error = stream.Error;
        AudioClip clip = null;
        if (error == null)
        {
            try { clip = CreateFlacClip(position); }
            catch (Exception ex) { error = "Unity音频创建失败（" + ex.GetType().Name + "）"; }
        }
        if (error != null)
        {
            if (clip != null) Destroy(clip);
            CloseFlac();
            if (NeteaseQualityPolicy.Lower(quality) is NeteaseQuality lower)
                _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, position, _playAfterLoad, false, false, lower));
            else FailFlac(gen, error, true);
            yield break;
        }
        _source.clip = clip;
        _lastGoodPosition = position;
        _progress.Reset(position, Time.realtimeSinceStartup);
        _sawPlaying = false; _resumeAttempts = 0; _clipNeedsStart = true;
        _flacWantsPlay = _playAfterLoad && context.Active && PlaybackCoordinator.Active == MusicProvider.Netease;
        State = _flacWantsPlay ? PlaybackState.Playing : PlaybackState.Paused;
        if (_flacWantsPlay) StartFlacAt(position);
        if (source.IsTrial != true && Math.Abs(track.DurationMs - format.Duration * 1000) > 1000)
        {
            track.DurationMs = (int)(format.Duration * 1000);
            NeteasePanelUi.RefreshTrackDuration(track.Id, track.DurationText);
        }
        if (IsFm) NeteaseRuntime.Fm.PlaybackSucceeded();
        BridgeLog.Info("FLAC就绪 songId=" + track.Id + " rate=" + format.SampleRate + " bits=" + format.BitsPerSample +
            " channels=" + format.Channels + " prefetched=" + source.WasPrefetched + " cache=" + fromCache + " download_s=" + preparation.DownloadSeconds.ToString("F3") +
            " validate_s=" + preparation.ValidationSeconds.ToString("F3") + " total_s=" + (Time.realtimeSinceStartup - started).ToString("F3") +
            " lookup_s=" + source.UrlLookupSeconds.ToString("F3") + " prefill_s=" + (Time.realtimeSinceStartup - prefillStarted).ToString("F3") +
            " load_s=" + (Time.realtimeSinceStartup - loadStarted).ToString("F3") + " pcm_bytes=" + stream.BufferedBytes);
        _loadRoutine = null; Notify();
    }
    private IEnumerator LoadProgressiveFlac(TrackInfo track, int gen, NeteaseAccountContext context,
        NeteasePlaybackSource source, NeteaseQuality quality, bool refreshedUrl, float loadStarted)
    {
        ProgressiveFlacSession session;
        try { session = new ProgressiveFlacSession(context, source, 0); }
        catch (Exception ex) { FailFlac(gen, "FLAC流式文件创建失败（" + ex.GetType().Name + "）", false); yield break; }
        _progressiveSession = session; _progressiveRefreshedUrl = refreshedUrl;
        _flacStream = session.Pcm;
        var stream = session.Pcm;
        while (!stream.Ready && stream.Error == null && session.Download.Error == null)
        {
            if (gen != _generation || !context.Active) { session.Dispose(); yield break; }
            yield return null;
        }
        if (gen != _generation || !context.Active) { session.Dispose(); yield break; }
        if (!stream.Ready || session.Download.Error != null || stream.Error != null)
        {
            RestartAfterProgressiveFailure(gen, session.Download.Error ?? stream.Error ?? "FLAC流式预填失败");
            yield break;
        }
        var format = stream.Format;
        format.Apply(source);
        AudioClip clip = null;
        try { clip = CreateFlacClip(0); }
        catch (Exception ex)
        {
            if (clip != null) Destroy(clip);
            RestartAfterProgressiveFailure(gen, "Unity音频创建失败（" + ex.GetType().Name + "）");
            yield break;
        }
        _source.clip = clip; _lastGoodPosition = 0;
        _progress.Reset(0, Time.realtimeSinceStartup);
        _sawPlaying = false; _resumeAttempts = 0; _clipNeedsStart = true;
        _flacWantsPlay = _playAfterLoad && context.Active && PlaybackCoordinator.Active == MusicProvider.Netease;
        State = _flacWantsPlay ? PlaybackState.Playing : PlaybackState.Paused;
        if (_flacWantsPlay) StartFlacAt(0);
        if (source.IsTrial != true && Math.Abs(track.DurationMs - format.Duration * 1000) > 1000)
        {
            track.DurationMs = (int)(format.Duration * 1000);
            NeteasePanelUi.RefreshTrackDuration(track.Id, track.DurationText);
        }
        if (IsFm) NeteaseRuntime.Fm.PlaybackSucceeded();
        BridgeLog.Info("FLAC流式就绪 songId=" + track.Id + " rate=" + format.SampleRate +
            " bits=" + format.BitsPerSample + " channels=" + format.Channels +
            " published_bytes=" + session.Growth.Published + " total_bytes=" + (source.SizeBytes?.ToString() ?? "unknown") +
            " download_done=" + session.Download.Downloaded + " load_s=" +
            (Time.realtimeSinceStartup - loadStarted).ToString("F3") + " pcm_bytes=" + stream.BufferedBytes);
        _loadRoutine = null; Notify();
    }
    private void RestartAfterProgressiveFailure(int gen, string error)
    {
        var session = _progressiveSession;
        if (session == null || gen != _generation || CurrentTrack == null || PlaybackSource == null) return;
        var download = session.Download;
        var track = CurrentTrack; var source = PlaybackSource;
        var context = NeteaseRuntime.Context;
        var quality = source.AttemptedQuality;
        float resume = _lastGoodPosition;
        bool play = State == PlaybackState.Loading ? _playAfterLoad : State == PlaybackState.Playing && _flacWantsPlay;
        bool network = download.NetworkFailure;
        bool decode = download.DecodeFailure || (download.Error == null && _flacStream?.Error != null);
        bool retry = download.Retryable, refresh = download.RefreshUrl;
        TimeSpan remaining = MusicBridgeOptions.Current.Netease.FlacRequestTimeout - TimeSpan.FromSeconds(download.ElapsedSeconds);
        bool alreadyRefreshed = _progressiveRefreshedUrl;
        _source.Stop(); ReleaseClip();
        if (context == null || !context.Active) return;
        State = PlaybackState.Loading; IsBuffering = false; Notify();
        if (network && remaining > TimeSpan.FromSeconds(1) && refresh && !alreadyRefreshed)
            _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resume, play, false, true, quality,
                true, remaining, false));
        else if (network && retry && remaining > TimeSpan.FromSeconds(1))
            _loadRoutine = StartCoroutine(LoadFlac(track, gen, context, source, null, resume,
                false, false, true, quality, Time.realtimeSinceStartup, true, remaining, false));
        else if (decode && NeteaseQualityPolicy.Lower(quality) is NeteaseQuality lower)
            _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resume, play, false, false, lower, true));
        else FailFlac(gen, error, decode);
    }
    private AudioClip CreateFlacClip(float position)
    {
        var stream = _flacStream;
        long frame = Math.Min(stream.Format.Frames - 1, Math.Max(0, (long)(position * stream.Format.SampleRate)));
        _flacClipBase = (float)((double)frame / stream.Format.SampleRate);
        // A clip represents only the remaining segment. Unity may prefetch during Create,
        // so feed valid PCM immediately and never reset its time after Play(). Every clip
        // reader is bound to a seek epoch; callbacks from a destroyed clip are silence.
        stream.Enable(true);
        var reader = stream.CreateClipReader();
        var clip = AudioClip.Create("MB_FLAC_" + CurrentTrack.Id, checked((int)(stream.Format.Frames - frame)),
            stream.Format.Channels, stream.Format.SampleRate, true, reader.Read);
        if (clip == null) throw new InvalidOperationException("Unity AudioClip creation failed");
        return clip;
    }
    private void StartFlacAt(float position)
    {
        if (_flacStream == null || !_flacStream.Ready || !_flacWantsPlay || PlaybackCoordinator.Active != MusicProvider.Netease) return;
        _flacStream.ClearUnderflow(); _flacStream.Enable(true);
        _source.Play();
        _clipNeedsStart = false; IsBuffering = false;
        _lastGoodPosition = position; _sawPlaying = false;
        _progress.Reset(position, Time.realtimeSinceStartup);
        _trackStartedAt = Time.realtimeSinceStartup;
    }
    private void SeekFlac(float seconds)
    {
        var stream = _flacStream;
        if (stream?.Format == null) return;
        float target = Mathf.Clamp(seconds, 0, (float)Math.Max(0, stream.Format.Duration - 0.05));
        _source.Stop(); stream.Enable(false);
        _flacWantsPlay = State == PlaybackState.Playing && PlaybackCoordinator.Active == MusicProvider.Netease;
        long targetFrame = (long)(target * stream.Format.SampleRate);
        if (_progressiveSession != null && !_progressiveSession.Download.Downloaded &&
            targetFrame > stream.MaximumDecodedFrame)
        {
            // The compressed offset of a future PCM frame is not known safely yet.
            // Invalidate old callbacks now, then apply only the latest target at EOF.
            if (!_flacHasDeferredSeek) stream.Seek(stream.PositionFrames);
            _flacDeferredSeekFrame = targetFrame; _flacHasDeferredSeek = true;
        }
        else { _flacHasDeferredSeek = false; stream.Seek(targetFrame); }
        if (_source.clip != null) { var oldClip = _source.clip; _source.clip = null; Destroy(oldClip); }
        _lastGoodPosition = target; _resumePositionAfterLoad = target;
        _clipNeedsStart = true; _sawPlaying = false; IsBuffering = true;
        _flacBufferStarted = Time.realtimeSinceStartup;
        _progress.Reset(target, Time.realtimeSinceStartup); Notify();
    }
    private void ResumeFlac()
    {
        if (PlaybackCoordinator.Active != MusicProvider.Netease) return;
        _flacWantsPlay = true;
        if (!_flacStream.Ready || IsBuffering) { IsBuffering = true; return; }
        if (_clipNeedsStart) StartFlacAt(_lastGoodPosition);
        else { _source.UnPause(); _flacStream.Enable(true); _progress.Reset(_lastGoodPosition, Time.realtimeSinceStartup); }
    }
    private void UpdateFlac()
    {
        var stream = _flacStream;
        if (NeteaseRuntime.Context == null || !NeteaseRuntime.Context.Active) { Stop(); return; }
        var progressive = _progressiveSession;
        if (progressive != null && progressive.Download.Done && progressive.Download.Error != null)
        { RestartAfterProgressiveFailure(_generation, progressive.Download.Error); return; }
        if (progressive != null && stream.Error != null)
        { RestartAfterProgressiveFailure(_generation, stream.Error); return; }
        if (stream.Error != null) { FailFlac(_generation, stream.Error, false); return; }
        if (State == PlaybackState.Loading) return; // Preparation coroutine owns this phase.
        if (State != PlaybackState.Playing && State != PlaybackState.Paused) return;
        if (PlaybackCoordinator.Active != MusicProvider.Netease && State == PlaybackState.Playing) PauseIfPlaying();
        if (!_flacFirstPcmLogged && stream.FirstPcmTicks != 0)
        {
            _flacFirstPcmLogged = true;
            BridgeLog.Info("FLAC首次有效PCM songId=" + CurrentTrack?.Id + " elapsed_s=" +
                ((stream.FirstPcmTicks - _flacLoadStartedTicks) / (double)Stopwatch.Frequency).ToString("F3") +
                " progressive=" + (progressive != null) + " download_done=" +
                (progressive?.Download.Downloaded ?? true));
        }
        if (_flacAwaitingValidation)
        {
            if (progressive != null && !progressive.Download.Validated) return;
            _flacAwaitingValidation = false; IsBuffering = false; Notify();
        }
        if (_flacHasDeferredSeek)
        {
            if (progressive == null || !progressive.Download.Downloaded) return;
            _flacHasDeferredSeek = false;
            stream.Seek(_flacDeferredSeekFrame);
            _flacBufferStarted = Time.realtimeSinceStartup;
            return;
        }
        if (IsBuffering)
        {
            if (stream.CanResume)
            {
                try { _source.clip = CreateFlacClip(_lastGoodPosition); }
                catch (Exception ex) { FailFlac(_generation, "FLAC定位后创建音频失败（" + ex.GetType().Name + "）", false); return; }
                IsBuffering = false;
                if (_flacWantsPlay && State == PlaybackState.Playing) StartFlacAt(_lastGoodPosition);
                Notify();
            }
            else if ((progressive == null || progressive.Download.Downloaded) &&
                Time.realtimeSinceStartup - _flacBufferStarted > 15)
                FailFlac(_generation, "FLAC缓冲超时，请重试", false);
            return;
        }
        if (State == PlaybackState.Paused || _source.clip == null) return;
        if (stream.Underflow)
        {
            // Unity may already have requested samples ahead of audible playback. Re-seek
            // to the last trusted playback time, never to the decoder/read-ahead cursor.
            SeekFlac(_lastGoodPosition); return;
        }
        if (_source.isPlaying)
        {
            if (!_progress.TryObserve(_flacClipBase + _source.time, Time.realtimeSinceStartup))
            { AudioOutputRecovery.ReportProgressDiscontinuity(); return; }
            _lastGoodPosition = (float)_progress.Position; _sawPlaying = true;
        }
        else if (_sawPlaying || (!_clipNeedsStart && Time.realtimeSinceStartup - _trackStartedAt >= _source.clip.length))
        {
            BridgeLog.Info("FLAC停止检查 position=" + _lastGoodPosition.ToString("F3") + " duration=" + stream.Format.Duration.ToString("F3") +
                " drained=" + stream.Drained + " consumed=" + stream.PositionFrames + " frames=" + stream.Format.Frames + " underruns=" + stream.Underruns);
            if (stream.Drained && _lastGoodPosition >= stream.Format.Duration - 1f)
            {
                if (progressive != null && !progressive.Download.Validated)
                { _flacAwaitingValidation = true; IsBuffering = true; Notify(); return; }
                BridgeLog.Info("FLAC有效PCM播放结束，推进队列。consumed=" + stream.PositionFrames + " frames=" + stream.Format.Frames + " underruns=" + stream.Underruns);
                if (RepeatOne && !IsFm) SeekFlac(0); else Next();
            }
            else if (++_resumeAttempts <= MaximumResumeAttempts) SeekFlac(_lastGoodPosition);
            else FailFlac(_generation, "FLAC播放中断，恢复失败", false);
        }
    }
    private void FailFlac(int gen, string error, bool trackFailure)
    {
        BridgeLog.Warn("FLAC播放失败 songId=" + CurrentTrack?.Id + " trackFailure=" + trackFailure + " reason=" + error);
        _source.Stop(); ReleaseClip(); LastError = error; State = PlaybackState.Failed;
        _loadRoutine = null; Notify(); ReportFmFailure(gen, trackFailure);
    }
    private void CloseFlac()
    {
        _flacPreparation?.Dispose(); _flacPreparation = null;
        _progressiveSession?.Dispose(); _progressiveSession = null;
        _flacStream?.Dispose(); _flacStream = null;
        IsBuffering = false; _flacWantsPlay = false; _flacAwaitingValidation = false;
        _flacHasDeferredSeek = false;
        _progressiveRefreshedUrl = false;
    }
}
