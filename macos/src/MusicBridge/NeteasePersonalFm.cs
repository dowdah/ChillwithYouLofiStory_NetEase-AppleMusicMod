using System;
using System.Collections.Generic;

namespace MusicBridge;

internal sealed class NeteasePersonalFm
{
    private readonly INeteaseClient _api;
    private readonly Action<Action> _work, _dispatch;
    private readonly Func<DateTime> _now;
    private NeteaseAccountContext _context;
    private readonly List<TrackInfo> _history = new List<TrackInfo>(), _pending = new List<TrackInfo>();
    private readonly Queue<long> _recentOrder = new Queue<long>();
    private readonly HashSet<long> _recent = new HashSet<long>();
    private readonly HashSet<long> _trashed = new HashSet<long>();
    private NeteaseRequestCancellation _batch, _trash;
    private int _generation, _historyIndex = -1, _attempt, _unplayable;
    private DateTime? _retryAt;
    private bool _needNext;
    private bool _outputAvailable = true;
    public bool Active { get; private set; }
    public bool Suspended { get; private set; }
    public bool Fetching => _batch != null;
    public bool TrashPending => _trash != null;
    public bool Waiting => Active && _needNext;
    public bool CanPrevious => Active && _historyIndex > 0;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public string Error { get; private set; }
    public string TrashError { get; private set; }
    public TrackInfo Current { get; private set; }
    public int BatchCount { get; private set; }
    public event Action<int, int> BatchAccepted;
    public int PendingCount => _pending.Count;
    public int HistoryCount => _history.Count;
    // Read-only prediction; never consumes a recommendation or requests another batch.
    public TrackInfo PeekNext => _historyIndex + 1 < _history.Count ? _history[_historyIndex + 1] : _pending.Count > 0 ? _pending[0] : null;
    public event Action Changed;
    public event Action<TrackInfo> Play;
    public event Action Wait;
    public NeteasePersonalFm(INeteaseClient api, Action<Action> work, Action<Action> dispatch, Func<DateTime> now)
    { _api = api; _work = work; _dispatch = dispatch; _now = now; }
    public void Start(NeteaseAccountContext context)
    {
        End(); if (context == null || !context.Active) { Error = "请先登录网易云"; Changed?.Invoke(); return; }
        _context = context; Active = true; Next();
    }
    public void End()
    {
        _generation++; _batch?.Cancel(); _trash?.Cancel(); _batch = _trash = null;
        Active = Suspended = _needNext = false; _retryAt = null; _attempt = _unplayable = 0;
        _history.Clear(); _pending.Clear(); _recent.Clear(); _recentOrder.Clear(); _trashed.Clear();
        _historyIndex = -1; BatchCount = 0; Current = null; Error = TrashError = null; Changed?.Invoke();
    }
    public void Suspend()
    {
        if (!Active) return;
        Suspended = true; _generation++; _batch?.Cancel(); _batch = null; _retryAt = null;
        // Trash completion remains tied to the same session; no auto-advance while suspended.
        Changed?.Invoke();
    }
    public void Resume()
    {
        if (!Active || _context == null || !_context.Active) return;
        Suspended = false; _attempt = 0;
        if (_needNext) Advance(); else if (Current != null) Play?.Invoke(Current);
        EnsureBatch(); Changed?.Invoke();
    }
    public void Next()
    {
        if (!Active || Suspended || _needNext || !_outputAvailable) return;
        Error = null; _needNext = true; Advance();
    }
    private void Advance()
    {
        if (!Active || Suspended || !_needNext || !_outputAvailable) return;
        TrackInfo track = null;
        if (_historyIndex + 1 < _history.Count) track = _history[++_historyIndex];
        else if (_pending.Count > 0)
        {
            track = _pending[0]; _pending.RemoveAt(0); _history.Add(track);
            if (_history.Count > 20) _history.RemoveAt(0);
            _historyIndex = _history.Count - 1;
        }
        if (track == null) { Current = null; Wait?.Invoke(); EnsureBatch(); Changed?.Invoke(); return; }
        Current = track; _needNext = false;
        Play?.Invoke(track); EnsureBatch(); Changed?.Invoke();
    }
    public void Previous()
    {
        if (!CanPrevious || Suspended) return;
        _needNext = false; Error = null; Current = _history[--_historyIndex]; Play?.Invoke(Current); Changed?.Invoke();
    }
    public void PlaybackSucceeded() { _unplayable = 0; }
    public void PlaybackFailed(string error, bool trackFailure)
    {
        if (!Active || Suspended) return;
        if (trackFailure && ++_unplayable < 5) { Next(); return; }
        Error = trackFailure ? "连续5首无法播放，请重试" : error;
        Changed?.Invoke();
    }
    public void Retry()
    {
        if (!Active || Suspended) return;
        Error = null; _attempt = _unplayable = 0; _retryAt = null;
        if (!_needNext && Current != null) Play?.Invoke(Current);
        EnsureBatch();
        Changed?.Invoke();
    }
    public void Tick(bool outputAvailable)
    {
        _outputAvailable = outputAvailable;
        if (!outputAvailable || !Active || Suspended) return;
        if (_needNext && _pending.Count > 0) Advance();
        if (_retryAt.HasValue && _now() >= _retryAt.Value) { _retryAt = null; EnsureBatch(); }
    }
    private void EnsureBatch()
    {
        if (!Active || Suspended || Fetching || _retryAt.HasValue || HasError || _pending.Count > 2 || !_context.Active) return;
        var context = _context; int generation = _generation;
        var cancellation = _batch = new NeteaseRequestCancellation();
        _work(() => {
            NeteaseResult<List<TrackInfo>> result;
            try { result = _api.Fm(context, cancellation); }
            catch { result = NeteaseResult<List<TrackInfo>>.Fail(NeteaseFailure.Network, "私人FM请求失败"); }
            _dispatch(() => {
                if (!Active || Suspended || !context.Active || generation != _generation || _batch != cancellation) return;
                _batch = null; int added = 0;
                if (result.Ok)
                {
                    foreach (var track in result.Value)
                    {
                        if (track == null || track.Id <= 0 || _recent.Contains(track.Id) || _trashed.Contains(track.Id) || _history.Exists(t => t.Id == track.Id) || _pending.Exists(t => t.Id == track.Id)) continue;
                        if (_pending.Count >= 30) break;
                        _pending.Add(track); _recent.Add(track.Id); _recentOrder.Enqueue(track.Id); added++;
                        while (_recentOrder.Count > 100) _recent.Remove(_recentOrder.Dequeue());
                    }
                }
                if (added == 0)
                {
                    string message = result.Ok ? "私人FM暂未返回新的歌曲" : result.Message;
                    if ((result.Ok || result.Failure == NeteaseFailure.Network) && _attempt < 2)
                    { _retryAt = _now().AddSeconds(_attempt++ == 0 ? 2 : 5); }
                    else Error = message + "，请重试";
                }
                else { _attempt = 0; Error = null; BatchAccepted?.Invoke(++BatchCount, added); if (_needNext) Advance(); }
                Changed?.Invoke();
            });
        });
    }
    public void TrashCurrent(long expectedId)
    {
        if (!Active || Suspended || Current == null || Current.Id != expectedId || TrashPending) return;
        var context = _context; var cancellation = _trash = new NeteaseRequestCancellation();
        TrashError = null; Changed?.Invoke();
        _work(() => {
            NeteaseResult<bool> result;
            try { result = _api.Trash(expectedId, context, cancellation); }
            catch { result = NeteaseResult<bool>.Fail(NeteaseFailure.UnknownWrite, "不再推荐结果待确认"); }
            _dispatch(() => {
                if (!Active || !context.Active || _context != context || _trash != cancellation) return;
                _trash = null;
                if (result.Ok)
                {
                    // Bounded negative feedback memory, independently of the listening dedup window.
                    if (_trashed.Count >= 100) _trashed.Clear();
                    _trashed.Add(expectedId); _pending.RemoveAll(t => t.Id == expectedId);
                    bool wasCurrent = Current != null && Current.Id == expectedId;
                    for (int i = _history.Count - 1; i >= 0; i--) if (_history[i].Id == expectedId) { _history.RemoveAt(i); if (i <= _historyIndex) _historyIndex--; }
                    if (wasCurrent) { Current = null; _needNext = true; if (!Suspended) Advance(); }
                }
                else TrashError = result.Failure == NeteaseFailure.UnknownWrite ? "不再推荐结果待确认；不会自动重发，可普通跳过" : "不再推荐失败 · " + result.Message;
                Changed?.Invoke();
            });
        });
    }
}
