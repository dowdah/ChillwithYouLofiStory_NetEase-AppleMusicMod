using System;
using System.Collections;
using UnityEngine;

namespace MusicBridge;

internal sealed partial class AudioPlayer
{
    private AudioFilePreparation _flacPreparation;
    private FlacPcmStream _flacStream;
    private bool _flacWantsPlay;
    private float _flacBufferStarted, _flacClipBase;
    public bool IsBuffering { get; private set; }

    private IEnumerator LoadFlac(TrackInfo track, int gen, NeteaseAccountContext context,
        NeteasePlaybackSource source, AudioDiskCache.Lease lease, float resume,
        bool fromCache, bool bypassCache, bool refreshedUrl, NeteaseQuality quality, float loadStarted)
    {
        float started = Time.realtimeSinceStartup;
        var preparation = new AudioFilePreparation(context, source, lease);
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
                _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resume, _playAfterLoad, true, refreshedUrl, quality));
            }
            else if (preparation.RefreshUrl && !refreshedUrl)
                _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resume, _playAfterLoad, bypassCache, true, quality));
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
        stream.Seek((long)(target * stream.Format.SampleRate));
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
        if (stream.Error != null) { FailFlac(_generation, stream.Error, false); return; }
        if (State == PlaybackState.Loading) return; // Preparation coroutine owns this phase.
        if (State != PlaybackState.Playing && State != PlaybackState.Paused) return;
        if (PlaybackCoordinator.Active != MusicProvider.Netease && State == PlaybackState.Playing) PauseIfPlaying();
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
            else if (Time.realtimeSinceStartup - _flacBufferStarted > 15) FailFlac(_generation, "FLAC缓冲超时，请重试", false);
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
        _flacStream?.Dispose(); _flacStream = null;
        IsBuffering = false; _flacWantsPlay = false;
    }
}
