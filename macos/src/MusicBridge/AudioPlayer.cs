using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace MusicBridge;

internal sealed partial class AudioPlayer : MonoBehaviour
{
	private sealed class UrlLookupResult
	{
		public string Url;
        public NeteasePlaybackSource Source;
        public NeteaseFailure FailureCategory;
        public AudioDiskCache.Lease Cache;
        public readonly object Gate = new object();

		public string Failure;

		public volatile bool Done;
	}

	private static AudioPlayer _instance;

	private AudioSource _source;

	private int _generation;

	private Coroutine _loadRoutine;

	private UnityWebRequest _activeRequest;

	private NeteaseRequestCancellation _urlCancellation;
    private UrlLookupResult _lookup;
    private AudioDiskCache.Lease _cacheLease;
    private NeteaseQuality _loadedQuality;
    public NeteasePlaybackSource PlaybackSource { get; private set; }
    public bool IsFm => Source == QueueSource.PersonalFm && NeteaseRuntime.Fm.Active;

	private List<TrackInfo> _queue = new List<TrackInfo>();

	private int _index = -1;

	public bool RepeatQueue = true;

	public bool RepeatOne;

	public bool Shuffle;

	private readonly System.Random _rng = new System.Random();

	private readonly List<int> _shuffleHistory = new List<int>();

	private bool _wasPlaying;

	private float _trackStartedAt;

	private bool _sawPlaying;

	private float _lastGoodPosition;
    private readonly AudioPlaybackProgress _progress = new AudioPlaybackProgress();

	private int _resumeAttempts;

	private bool _playAfterLoad = true;
    private bool _clipNeedsStart;

	private float _resumePositionAfterLoad;

	private const int MaximumResumeAttempts = 3;

	public static AudioPlayer Instance => _instance;

	public PlaybackState State { get; private set; }

	public TrackInfo CurrentTrack { get; private set; }

	public string LastError { get; private set; }

	public QueueSource Source { get; private set; }

	public string SourceName { get; private set; } = "";

	public int QueueIndex => _index;

	public int QueueCount => _queue.Count;

	public bool IsActive
	{
		get
		{
			if (CurrentTrack != null)
			{
				return State != PlaybackState.Idle;
			}
			return false;
		}
	}

	public float Volume
	{
		get
		{
			if (!(_source != null))
			{
				return 0f;
			}
			return _source.volume;
		}
		set
		{
			if (_source != null)
			{
				_source.volume = Mathf.Clamp01(value);
			}
		}
	}

	public float PositionSeconds
	{
		get
		{
            if (_flacStream != null) return (_clipNeedsStart || IsBuffering || _source.clip == null) ? _lastGoodPosition : _flacClipBase + _source.time;
            if (_source == null || _source.clip == null) return 0f;
            return _clipNeedsStart ? _lastGoodPosition : _source.time;
		}
	}

	public float DurationSeconds
	{
		get
		{
            if (_flacStream?.Format != null) return (float)_flacStream.Format.Duration;
			if (_source != null && _source.clip != null && _source.clip.length > 0f)
			{
				return _source.clip.length;
			}
			if (CurrentTrack != null && CurrentTrack.DurationMs > 0)
			{
				return (float)CurrentTrack.DurationMs / 1000f;
			}
			return 0f;
		}
	}

	public event Action StateChanged;

	public event Action<TrackInfo> TrackChanged;

	public static void Initialize()
	{
		if (!(_instance != null))
		{
			GameObject obj = new GameObject("MusicBridge_AudioPlayer");
			UnityEngine.Object.DontDestroyOnLoad(obj);
			obj.hideFlags = HideFlags.HideAndDontSave;
			_instance = obj.AddComponent<AudioPlayer>();
			BridgeLog.Info("MusicBridge 播放器已创建（独立 AudioSource，不影响游戏音频系统）。");
		}
	}

	private void Awake()
	{
		try
		{
			RepeatQueue = MusicBridgeOptions.Current.Netease.RepeatQueue;
		}
		catch
		{
		}
		_source = base.gameObject.AddComponent<AudioSource>();
		_source.playOnAwake = false;
		_source.loop = false;
		_source.volume = 0.7f;
		_source.spatialBlend = 0f;
		_source.bypassEffects = true;
		_source.bypassListenerEffects = true;
		_source.bypassReverbZones = true;
		_source.ignoreListenerPause = true;
		_source.ignoreListenerVolume = false;
	}

	public void PlayQueue(IList<TrackInfo> tracks, int startIndex, QueueSource source, string sourceName)
	{
		if (tracks != null && tracks.Count != 0)
		{
            NeteaseRuntime.Fm.End();
			_queue = new List<TrackInfo>(tracks);
			_shuffleHistory.Clear();
			_index = Mathf.Clamp(startIndex, 0, _queue.Count - 1);
			Source = source;
			SourceName = sourceName ?? "";
			BridgeLog.History("建立播放队列：来源=" + source.ToString() + "『" + SourceName + "』共 " + _queue.Count + " 首，从第 " + (_index + 1) + " 首开始。");
			PlayIndex(_index);
		}
	}

	private bool IsPlayableAt(int i)
	{
		return _queue[i]?.Playable ?? false;
	}

	private int FirstPlayableFrom(int start, bool wrap, out int skipped)
	{
		return PlaylistAssembly.FirstPlayable(_queue.Count, start, wrap, forward: true, IsPlayableAt, out skipped);
	}

	private int FirstPlayableBefore(int start, bool wrap, out int skipped)
	{
		return PlaylistAssembly.FirstPlayable(_queue.Count, start, wrap, forward: false, IsPlayableAt, out skipped);
	}

	private void StopNoPlayable(int skipped)
	{
		AbortActiveRequest();
		CancelUrlLookup();
		_generation++;
		if (_source != null)
		{
			_source.Stop();
		}
		ReleaseClip();
		_sawPlaying = false;
		_lastGoodPosition = 0f;
		LastError = "队列里接下来没有可播放的曲目（已跳过 " + skipped + " 首不可播放）";
		State = PlaybackState.Failed;
		BridgeLog.Info("连续 " + skipped + " 首不可播放，队列中已无可播放曲目，停止。");
		Notify();
	}

	public void Next()
	{
        if (IsFm) { NeteaseRuntime.Fm.Next(); return; }
		if (_queue.Count == 0)
		{
			return;
		}
		if (Shuffle && _queue.Count > 1)
		{
			int num = _index;
			for (int i = 0; i < 8; i++)
			{
				if (num != _index)
				{
					break;
				}
				num = _rng.Next(_queue.Count);
			}
			if (num == _index)
			{
				num = (_index + 1) % _queue.Count;
			}
			int skipped;
			int num2 = FirstPlayableFrom(num, wrap: true, out skipped);
			if (num2 < 0)
			{
				StopNoPlayable(skipped);
				return;
			}
			if (skipped > 0)
			{
				BridgeLog.Info("随机播放：跳过 " + skipped + " 首不可播放曲目。");
			}
			BridgeLog.History("随机播放：跳到队列第 " + (num2 + 1) + " 首。");
			if (_index >= 0)
			{
				_shuffleHistory.Add(_index);
				if (_shuffleHistory.Count > 256)
				{
					_shuffleHistory.RemoveRange(0, 128);
				}
			}
			PlayIndex(num2);
			return;
		}
		int num3 = _index + 1;
		if (num3 >= _queue.Count)
		{
			if (!RepeatQueue)
			{
				Stop();
				BridgeLog.Info("已到队列末尾，停止播放。");
				return;
			}
			num3 = 0;
		}
		int skipped2;
		int num4 = FirstPlayableFrom(num3, RepeatQueue, out skipped2);
		if (num4 < 0)
		{
			if (skipped2 > 0)
			{
				StopNoPlayable(skipped2);
				return;
			}
			Stop();
			BridgeLog.Info("已到队列末尾，停止播放。");
		}
		else
		{
			if (skipped2 > 0)
			{
				BridgeLog.Info("自动续播：跳过 " + skipped2 + " 首不可播放曲目（无版权 / 需 VIP 等）。");
			}
			PlayIndex(num4);
		}
	}

	public void Previous()
	{
        if (IsFm) { NeteaseRuntime.Fm.Previous(); return; }
		if (_queue.Count == 0)
		{
			return;
		}
		if (State == PlaybackState.Playing && PositionSeconds > 3f)
		{
			Seek(0f);
			return;
		}
		int index;
		if (Shuffle && _shuffleHistory.Count > 0)
		{
			index = _shuffleHistory[_shuffleHistory.Count - 1];
			_shuffleHistory.RemoveAt(_shuffleHistory.Count - 1);
			PlayIndex(index);
			return;
		}
		index = _index - 1;
		if (index < 0)
		{
			if (!RepeatQueue)
			{
				Seek(0f);
				return;
			}
			index = _queue.Count - 1;
		}
		int skipped;
		int num = FirstPlayableBefore(index, RepeatQueue, out skipped);
		if (num < 0)
		{
			if (skipped > 0)
			{
				BridgeLog.Info("上一首：往前 " + skipped + " 首都不可播放，停留在当前曲目。");
			}
			Seek(0f);
		}
		else
		{
			if (skipped > 0)
			{
				BridgeLog.Info("上一首：跳过 " + skipped + " 首不可播放曲目。");
			}
			PlayIndex(num);
		}
	}

	private void PlayIndex(int index)
	{
		if (index >= 0 && index < _queue.Count)
		{
			_index = index;
			PlayTrack(_queue[index]);
		}
	}

    public void PlayFmTrack(TrackInfo track)
    {
        Source = QueueSource.PersonalFm; SourceName = "私人FM";
        _queue.Clear(); _index = -1;
        if (CurrentTrack != null && CurrentTrack.Id == track.Id && State == PlaybackState.Loading)
            _playAfterLoad = true;
        PlayTrack(track);
    }
    public void WaitForFm()
    {
        AbortActiveRequest(); CancelUrlLookup(); _generation++;
        if (_loadRoutine != null) { StopCoroutine(_loadRoutine); _loadRoutine = null; }
        _source.Stop(); ReleaseClip(); _sawPlaying = false; CurrentTrack = null; PlaybackSource = null;
        Source = QueueSource.PersonalFm; SourceName = "私人FM"; State = PlaybackState.Loading; Notify();
    }

	public void PlayTrack(TrackInfo track)
	{
		if (track == null)
		{
			return;
		}
		PlaybackCoordinator.Claim(MusicProvider.Netease);
		if (CurrentTrack != null && CurrentTrack.Id == track.Id && (State == PlaybackState.Loading || State == PlaybackState.Playing))
		{
			BridgeLog.Info("忽略重复播放请求：同一首歌已在 " + State.ToString() + "。");
			return;
		}
		if (CurrentTrack != null && CurrentTrack.Id == track.Id && State == PlaybackState.Paused && _source != null && (_source.clip != null || _flacStream != null))
		{
			ResumeClip();
			State = PlaybackState.Playing;
			BridgeLog.History("同一首暂停曲目直接恢复，不重新下载：" + track.Name);
			Notify();
			return;
		}
		int gen = ++_generation;
		AbortActiveRequest();
		CancelUrlLookup();
        _source.Stop(); ReleaseClip(); _sawPlaying = false; _resumeAttempts = 0;
		CurrentTrack = track;
        PlaybackSource = null;
        _loadedQuality = MusicBridgeOptions.Current.Netease.PreferredQuality;
		LastError = null;
		State = PlaybackState.Loading;
		Notify();
		if (this.TrackChanged != null)
		{
			this.TrackChanged(track);
		}
		if (!track.Playable)
		{
			LastError = (string.IsNullOrEmpty(track.UnplayableReason) ? "该歌曲不可播放" : track.UnplayableReason);
			State = PlaybackState.Failed;
			BridgeLog.Info("该曲目不可播放 " + track.Id);
			Notify();
            ReportFmFailure(gen, true);
		}
		else
		{
			if (_loadRoutine != null)
			{
				StopCoroutine(_loadRoutine);
				_loadRoutine = null;
			}
			_loadRoutine = StartCoroutine(LoadAndPlay(track, gen));
		}
	}

    private void ReportFmFailure(int generation, bool trackFailure)
    {
        Plugin.RunOnMainThread(() => { if (generation == _generation && !AudioOutputRecovery.OutputUnavailable) {
            BridgeLog.Warn("网易云播放未完成 songId=" + CurrentTrack?.Id + " trackFailure=" + trackFailure + " reason=" + LastError);
            if (IsFm) NeteaseRuntime.Fm.PlaybackFailed(LastError ?? "播放失败，请重试", trackFailure);
        } });
    }

	internal bool Owns(AudioSource source) => source == _source;

	internal Action CaptureOutputRecovery(bool preferCachedPosition = false)
	{
		if (CurrentTrack == null || (State != PlaybackState.Playing && State != PlaybackState.Paused && State != PlaybackState.Loading)) return null;
		int generation = _generation;
		TrackInfo track = CurrentTrack;
		float position = State == PlaybackState.Loading ? _resumePositionAfterLoad : preferCachedPosition ? _lastGoodPosition : PositionSeconds;
		return () =>
		{
			if (_generation != generation || CurrentTrack != track) return;
			bool play = (State == PlaybackState.Loading ? _playAfterLoad : State != PlaybackState.Paused) && PlaybackCoordinator.Active == MusicProvider.Netease;
			AbortActiveRequest();
			CancelUrlLookup();
			if (_loadRoutine != null) StopCoroutine(_loadRoutine);
			_source.Stop();
			ReleaseClip();
			_sawPlaying = false;
			_lastGoodPosition = position;
			State = PlaybackState.Loading;
			int gen = ++_generation;
			_loadRoutine = StartCoroutine(LoadAndPlay(track, gen, position, play));
			Notify();
		};
	}

	private IEnumerator LoadAndPlay(TrackInfo track, int gen, float resumePosition = 0f, bool playAfterLoad = true,
        bool bypassCache = false, bool refreshedUrl = false, NeteaseQuality? attemptQuality = null,
        bool forceCompleteFlac = false, TimeSpan? downloadBudget = null, bool allowDownloadRetry = true)
	{
        float loadStarted = Time.realtimeSinceStartup;
        _flacLoadStartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _flacFirstPcmLogged = false;
		_playAfterLoad = playAfterLoad;
		_resumePositionAfterLoad = resumePosition;
        var context = NeteaseRuntime.Context;
        var preferred = _loadedQuality;
        var quality = attemptQuality ?? preferred;
        UrlLookupResult lookup = new UrlLookupResult();
        _lookup = lookup;
        NeteaseRequestCancellation cancellation = new NeteaseRequestCancellation();
        _urlCancellation = cancellation;
		Thread thread = new Thread((ThreadStart)delegate
		{
			try
			{
                var lookupWatch = System.Diagnostics.Stopwatch.StartNew();
				var result = NeteaseApi.GetPlaybackSource(track.Id, quality, context, cancellation);
                if (result.Ok) {
                    result.Value.UrlLookupSeconds = lookupWatch.Elapsed.TotalSeconds;
                    result.Value.AttemptedQuality = quality;
                    result.Value.RequestedQuality = preferred;
                    if (quality != preferred) result.Value.FallbackReason = "高档音源不可用或解码失败";
                }
                AudioDiskCache.Lease cache = null;
                if (result.Ok && !cancellation.IsCancelled && context.Active && !bypassCache)
                    cache = _prefetch?.Take(context, result.Value) ?? AudioDiskCache.TryGet(context.UserId, result.Value);
                lock (lookup.Gate)
                {
                    lookup.Source = result.Value; lookup.Failure = result.Message; lookup.FailureCategory = result.Failure;
                    if (result.Ok && !cancellation.IsCancelled && context.Active)
                    {
                        lookup.Cache = cache; cache = null;
                        lookup.Url = lookup.Cache?.Uri ?? result.Value.Url;
                    }
                    cache?.Dispose();
                }
			}
			catch (Exception ex4)
			{
				lookup.Failure = "取播放地址异常：" + ex4.GetType().Name;
			}
			finally
			{
				lock (lookup.Gate) { if (cancellation.IsCancelled) { lookup.Cache?.Dispose(); lookup.Cache = null; } lookup.Done = true; }
			}
		});
		thread.IsBackground = true;
		thread.Start();
		while (!lookup.Done)
		{
			if (gen != _generation || context == null || !context.Active)
			{
				cancellation.Cancel();
				yield break;
			}
			yield return null;
		}
		if (_urlCancellation == cancellation)
		{
			_urlCancellation = null;
		}
        if (gen != _generation || context == null || !context.Active)
        { lock (lookup.Gate) { lookup.Cache?.Dispose(); lookup.Cache = null; } yield break; }
        string uri = lookup.Url;
        bool fromCache;
        lock (lookup.Gate) { _cacheLease = lookup.Cache; lookup.Cache = null; fromCache = _cacheLease != null; }
        _lookup = null;
        PlaybackSource = lookup.Source;
        bool needsCacheCommit = !fromCache || (_cacheLease?.DeleteOnDispose ?? false);
		if (string.IsNullOrEmpty(uri))
		{
			if (NeteaseQualityPolicy.CanFallback(lookup.FailureCategory) && NeteaseQualityPolicy.Lower(quality) is NeteaseQuality lower)
            { _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resumePosition, _playAfterLoad, false, false, lower)); yield break; }
            LastError = lookup.Failure ?? "无法获取播放地址";
            State = PlaybackState.Failed; Notify();
            ReportFmFailure(gen, lookup.FailureCategory == NeteaseFailure.Copyright || lookup.FailureCategory == NeteaseFailure.Subscription || lookup.FailureCategory == NeteaseFailure.Rejected || lookup.FailureCategory == NeteaseFailure.UnsupportedFormat);
			yield break;
		}
        if (lookup.Source.IsFlac)
        {
            var lease = _cacheLease; _cacheLease = null;
            yield return LoadFlac(track, gen, context, lookup.Source, lease, resumePosition, fromCache,
                bypassCache, refreshedUrl, quality, loadStarted, forceCompleteFlac, downloadBudget, allowDownloadRetry);
            yield break;
        }
		BridgeLog.History("准备下载音频 songId=" + track.Id + "（协程存活，世代 " + gen + "）");
		UnityWebRequest req = null;
		UnityWebRequestAsyncOperation op = null;
		string text = null;
		try
		{
			req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG);
			if (req.downloadHandler is DownloadHandlerAudioClip downloadHandlerAudioClip)
			{
				downloadHandlerAudioClip.streamAudio = false;
			}
			else
			{
				text = "downloadHandler 不是 DownloadHandlerAudioClip（音频模块可能被裁剪）";
			}
			req.timeout = (int)Math.Ceiling(MusicBridgeOptions.Current.Netease.AudioRequestTimeout.TotalSeconds);
			_activeRequest = req;
			op = req.SendWebRequest();
		}
		catch (Exception ex)
		{
			text = ex.GetType().Name;
		}
		if (text != null || req == null || op == null)
		{
			LastError = "无法创建音频请求：" + (text ?? "未知原因");
			State = PlaybackState.Failed;
            _cacheLease?.Dispose(); _cacheLease = null; ReportFmFailure(gen, false);
			BridgeLog.Error("创建音频请求失败 songId=" + track.Id + " -> " + LastError);
			if (req != null)
			{
				try
				{
					req.Dispose();
				}
				catch
				{
				}
			}
			_activeRequest = null;
			Notify();
			yield break;
		}
		using (req)
        using (var cacheLease = _cacheLease)
		{
			BridgeLog.History("下载请求已发出 songId=" + track.Id);
			float realtimeSinceStartup = Time.realtimeSinceStartup;
			float lastLog = realtimeSinceStartup;
			ulong lastBytes = 0uL;
			float lastProgressAt = realtimeSinceStartup;
			while (!op.isDone)
			{
				if (gen != _generation || context == null || !context.Active)
				{
					req.Abort();
					_activeRequest = null;
					yield break;
				}
				float realtimeSinceStartup2 = Time.realtimeSinceStartup;
				ulong downloadedBytes = req.downloadedBytes;
				if (downloadedBytes != lastBytes)
				{
					lastBytes = downloadedBytes;
					lastProgressAt = realtimeSinceStartup2;
				}
				if (realtimeSinceStartup2 - lastLog >= 2f)
				{
					lastLog = realtimeSinceStartup2;
					BridgeLog.History("音频下载中 songId=" + track.Id + "：已接收 " + downloadedBytes + " 字节，进度 " + (req.downloadProgress * 100f).ToString("0") + "%");
				}
				if (realtimeSinceStartup2 - lastProgressAt > (float)MusicBridgeOptions.Current.Netease.AudioStallTimeout.TotalSeconds)
				{
					req.Abort();
					_activeRequest = null;
					LastError = "音频下载停滞（" + MusicBridgeOptions.Current.Netease.AudioStallTimeout.TotalSeconds + " 秒无数据）";
					State = PlaybackState.Failed;
                    ReportFmFailure(gen, false);
					BridgeLog.Warn("音频下载停滞，已中止。songId=" + track.Id + "，已接收 " + downloadedBytes + " 字节。");
					Notify();
					yield break;
				}
				yield return null;
			}
			_activeRequest = null;
			if (gen != _generation || context == null || !context.Active)
			{
				yield break;
			}
			if (req.result != UnityWebRequest.Result.Success)
			{
				if (fromCache)
				{
					_cacheLease?.Dispose(); _cacheLease = null; AudioDiskCache.Remove(context.UserId, lookup.Source);
					BridgeLog.Warn("音频缓存已损坏，改从网络重取。songId=" + track.Id);
					_activeRequest = null;
					if (gen == _generation)
					{
						_loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resumePosition, _playAfterLoad, true, refreshedUrl, quality));
					}
					yield break;
				}
				if (!refreshedUrl && (req.responseCode == 401 || req.responseCode == 403 || req.responseCode == 410 || (lookup.Source.ExpiresAtUtc.HasValue && DateTime.UtcNow >= lookup.Source.ExpiresAtUtc.Value)))
                {
                    _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resumePosition, _playAfterLoad, bypassCache, true, quality));
                    yield break;
                }
                LastError = "音频下载失败（HTTP " + req.responseCode + "）";
				State = PlaybackState.Failed;
                ReportFmFailure(gen, false);
				BridgeLog.Warn("音频下载失败 songId=" + track.Id + "  result=" + req.result.ToString() + "  httpCode=" + req.responseCode + "（详情不记录，避免泄露临时地址）");
				Notify();
				yield break;
			}
			BridgeLog.History("音频下载完成 songId=" + track.Id + "，HTTP " + req.responseCode + "，已接收 " + req.downloadedBytes + " 字节。");
			AudioClip audioClip = null;
			try
			{
				audioClip = DownloadHandlerAudioClip.GetContent(req);
			}
			catch (Exception ex3)
			{
				BridgeLog.Warn("解码失败：" + ex3.GetType().Name);
			}
			if (gen != _generation || context == null || !context.Active)
			{
				if (audioClip != null)
				{
					UnityEngine.Object.Destroy(audioClip);
				}
				yield break;
			}
			if (audioClip == null || audioClip.length <= 0f)
            {
                if (audioClip != null) UnityEngine.Object.Destroy(audioClip);
                if (fromCache)
                {
                    _cacheLease?.Dispose(); _cacheLease = null; AudioDiskCache.Remove(context.UserId, lookup.Source);
                    _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resumePosition, _playAfterLoad, true, refreshedUrl, quality));
                    yield break;
                }
				if (NeteaseQualityPolicy.Lower(quality) is NeteaseQuality lower)
                { _loadRoutine = StartCoroutine(LoadAndPlay(track, gen, resumePosition, _playAfterLoad, false, false, lower)); yield break; }
                LastError = "音频解码失败";
				State = PlaybackState.Failed;
                ReportFmFailure(gen, false);
				Notify();
				yield break;
			}
            _cacheLease?.Dispose(); _cacheLease = null;
            if (needsCacheCommit)
            {
                byte[] bytes = null;
                try { bytes = req.downloadHandler.data; } catch { }
                if (bytes != null)
                {
                    var data = bytes;
                    ThreadPool.QueueUserWorkItem(_ => AudioDiskCache.Store(context.UserId, lookup.Source, data, () => context.Active && gen == _generation));
                }
            }
            ReleaseClip();
            audioClip.name = "MB_" + track.Id;
			_source.clip = audioClip;
            float restoredPosition = Mathf.Clamp(resumePosition, 0f, Mathf.Max(0f, audioClip.length - 0.05f));
            bool shouldPlay = _playAfterLoad && context.Active && PlaybackCoordinator.Active == MusicProvider.Netease;
            _clipNeedsStart = !shouldPlay;
            if (shouldPlay) AudioPlaybackProgress.ResumeAt(restoredPosition, () => _source.Play(), value => _source.time = value);
            else _source.time = restoredPosition;
			_trackStartedAt = Time.realtimeSinceStartup;
			_sawPlaying = false;
            _lastGoodPosition = restoredPosition;
            _progress.Reset(restoredPosition, Time.realtimeSinceStartup);
			State = shouldPlay ? PlaybackState.Playing : PlaybackState.Paused;
            if (IsFm) NeteaseRuntime.Fm.PlaybackSucceeded();
            BridgeLog.Info("MP3就绪 songId=" + track.Id + " prefetched=" + lookup.Source.WasPrefetched + " cache=" + fromCache);
			int num = Mathf.RoundToInt(audioClip.length * 1000f);
			if (lookup.Source.IsTrial != true && num > 0 && Mathf.Abs(num - track.DurationMs) > 1000)
			{
				BridgeLog.Info("时长以实际解码为准：元数据 " + track.DurationMs / 1000 + "s -> 实际 " + num / 1000 + "s（" + BridgeLog.Redact(track.Name) + "）");
				track.DurationMs = num;
				NeteasePanelUi.RefreshTrackDuration(track.Id, track.DurationText);
			}
			BridgeLog.History("开始播放：" + track.Name + " · " + track.Artists + "（时长 " + audioClip.length.ToString("0.0") + "s）");
			Notify();
		}
		_loadRoutine = null;
	}

	public void TogglePlayPause()
    {
        if (IsFm && NeteaseRuntime.Fm.Suspended) { NeteaseRuntime.ResumeFm(); return; }
        if (IsFm) { NeteaseRuntime.Fm.Suspend(); PauseIfPlaying(); return; }
		if (State == PlaybackState.Loading)
		{
			_playAfterLoad = !_playAfterLoad;
			BridgeLog.Info(_playAfterLoad ? "音频加载完成后播放。" : "音频加载完成后保持暂停。");
			Notify();
			return;
		}
        if (_flacStream != null && IsBuffering)
        {
            _flacWantsPlay = State != PlaybackState.Playing;
            State = _flacWantsPlay ? PlaybackState.Playing : PlaybackState.Paused;
            Notify(); return;
        }
		if (!(_source == null) && !(_source.clip == null))
		{
			if (State == PlaybackState.Playing)
			{
				_source.Pause();
                _flacWantsPlay = false;
				State = PlaybackState.Paused;
			}
			else if (State == PlaybackState.Paused)
			{
				ResumeClip();
				State = PlaybackState.Playing;
			}
			BridgeLog.Info("播放状态 -> " + State);
			Notify();
		}
	}

	public void PauseIfPlaying()
	{
        CancelPrefetch();
        if (IsFm && !NeteaseRuntime.Fm.Suspended) NeteaseRuntime.Fm.Suspend();
		if (State == PlaybackState.Loading) _playAfterLoad = false;
        if (_flacStream != null && IsBuffering && State == PlaybackState.Playing)
        { _flacWantsPlay = false; State = PlaybackState.Paused; Notify(); return; }
		if (!(_source == null) && !(_source.clip == null) && State == PlaybackState.Playing)
		{
			_source.Pause();
            _flacWantsPlay = false;
			State = PlaybackState.Paused;
			BridgeLog.Info("网易云让位：已暂停（保留进度）。");
			Notify();
		}
	}

	public void Seek(float seconds)
    {
        if (_flacStream != null) { SeekFlac(seconds); return; }
		if (!(_source == null) && !(_source.clip == null))
		{
			float time = Mathf.Clamp(seconds, 0f, Mathf.Max(0f, _source.clip.length - 0.05f));
			_source.time = time;
            _lastGoodPosition = time; _progress.Reset(time, Time.realtimeSinceStartup);
			BridgeLog.Info("跳转到 " + time.ToString("0.0") + "s");
			Notify();
		}
	}

	public void Stop()
	{
        CancelPrefetch();
        NeteaseRuntime.Fm.End(); PlaybackSource = null;
        if (_loadRoutine != null) { StopCoroutine(_loadRoutine); _loadRoutine = null; }
		AbortActiveRequest();
		CancelUrlLookup();
		_generation++;
		if (_source != null)
		{
			_source.Stop();
		}
		ReleaseClip();
		State = PlaybackState.Idle;
		CurrentTrack = null;
		Notify();
	}

	private void AbortActiveRequest()
	{
		if (_activeRequest != null)
		{
			try
			{
				_activeRequest.Abort();
                _activeRequest.Dispose();
			}
			catch
			{
			}
			_activeRequest = null;
		}
	}

	private void CancelUrlLookup()
	{
		NeteaseRequestCancellation urlCancellation = _urlCancellation;
		_urlCancellation = null;
		urlCancellation?.Cancel();
        var lookup = _lookup; _lookup = null;
        if (lookup != null) lock (lookup.Gate) { lookup.Cache?.Dispose(); lookup.Cache = null; }
        _cacheLease?.Dispose(); _cacheLease = null;
	}

    private void ResumeClip()
    {
        if (_flacStream != null) { ResumeFlac(); return; }
        if (PlaybackCoordinator.Active != MusicProvider.Netease) return;
        float position = Mathf.Clamp(_lastGoodPosition, 0f, Mathf.Max(0f, _source.clip.length - 0.05f));
        bool start = _clipNeedsStart;
        _clipNeedsStart = false;
        AudioPlaybackProgress.ResumeAt(position, () => { if (start) _source.Play(); else _source.UnPause(); }, value => _source.time = value);
        _lastGoodPosition = position;
        _progress.Reset(position, Time.realtimeSinceStartup);
    }

	private void ReleaseClip()
    {
        CloseFlac();
        _clipNeedsStart = false;
		if (_source != null && _source.clip != null)
		{
			AudioClip clip = _source.clip;
			_source.clip = null;
			UnityEngine.Object.Destroy(clip);
		}
	}

	private void Update()
	{
        TickPrefetch();
		// A null output can keep advancing time. Do not skip through the queue while recovering.
		if (AudioOutputRecovery.OutputUnavailable) return;
        if (_flacStream != null) { UpdateFlac(); return; }
		if (State == PlaybackState.Playing && _source != null && _source.clip != null)
		{
			if (_source.isPlaying)
			{
                if (!_progress.TryObserve(_source.time, Time.realtimeSinceStartup))
                {
                    BridgeLog.Warn("音频进度出现非用户跳变，保留最后可信进度并恢复当前曲目。");
                    AudioOutputRecovery.ReportProgressDiscontinuity();
                    return;
                }
                _sawPlaying = true;
                _lastGoodPosition = (float)_progress.Position;
				_resumeAttempts = 0;
			}
			else if (_sawPlaying)
			{
				float length = _source.clip.length;
				if (!(_lastGoodPosition >= length - 1f))
				{
					if (_resumeAttempts >= 3)
					{
						if (IsFm)
                        {
                            LastError = "音频恢复失败，请修复声音后重试"; State = PlaybackState.Failed;
                            NeteaseRuntime.Fm.PlaybackFailed(LastError, false); Notify(); return;
                        }
                        BridgeLog.Error("音频被外部中止且连续 " + 3 + " 次恢复失败，放弃本曲并尝试下一首。");
						_sawPlaying = false;
						_lastGoodPosition = 0f;
						_resumeAttempts = 0;
						Next();
					}
					else
					{
						_resumeAttempts++;
						BridgeLog.Warn("音频被外部中止，正在恢复播放：位置=" + _lastGoodPosition.ToString("0.00") + " 曲长=" + length.ToString("0.00") + " 已播=" + (Time.realtimeSinceStartup - _trackStartedAt).ToString("0.0") + "s，第 " + _resumeAttempts + " 次。");
						_source.time = Mathf.Clamp(_lastGoodPosition, 0f, Mathf.Max(0f, length - 0.05f));
                        _progress.Reset(_source.time, Time.realtimeSinceStartup);
						_source.Play();
						Notify();
					}
				}
				else if (RepeatOne && !IsFm)
				{
					BridgeLog.Info("单曲循环：当前曲目重新播放。");
					_sawPlaying = false;
					_lastGoodPosition = 0f;
                    _source.time = 0f;
                    _progress.Reset(0, Time.realtimeSinceStartup);
                    _source.Play();
					Notify();
				}
				else
				{
					BridgeLog.Info("当前曲目播放结束，自动进入下一首。");
					_sawPlaying = false;
					_lastGoodPosition = 0f;
					Next();
				}
			}
		}
		bool flag = State == PlaybackState.Playing;
		if (flag != _wasPlaying)
		{
			_wasPlaying = flag;
			Notify();
		}
	}

	private void Notify()
	{
		try
		{
			if (this.StateChanged != null)
			{
				this.StateChanged();
			}
		}
		catch (Exception ex)
		{
			BridgeLog.Error("播放状态回调异常：" + ex.Message);
		}
	}

	private void OnApplicationQuit()
    {
        Stop();
		BridgeLog.Info("游戏退出：停止 MusicBridge 音频与下载。");
		AbortActiveRequest();
		CancelUrlLookup();
		_generation++;
		if (_source != null)
		{
			_source.Stop();
		}
	}

	private void OnDestroy()
	{
        CancelPrefetch();
		AbortActiveRequest();
		CancelUrlLookup();
		_generation++;
		ReleaseClip();
	}
}
