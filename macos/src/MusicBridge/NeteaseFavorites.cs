using System;
using System.Collections.Generic;

namespace MusicBridge;

// Mutations and notifications are confined to the owner (Unity main) thread.
internal sealed class NeteaseFavorites
{
    private sealed class Intent { public bool Target; public bool Sent; public bool Busy; public string Error; }
    private readonly INeteaseClient _api;
    private readonly Action<Action> _work, _dispatch;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<long, Intent> _intents = new Dictionary<long, Intent>();
    private HashSet<long> _confirmed = new HashSet<long>();
    private List<long> _ordered = new List<long>();
    private NeteaseAccountContext _context;
    private int _version;
    public bool Known { get; private set; }
    public bool Refreshing { get; private set; }
    public string Error { get; private set; }
    public DateTime LastAttempt { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public event Action Changed;
    public event Action SnapshotChanged;
    public List<long> Ids => new List<long>(_ordered);
    public NeteaseFavorites(INeteaseClient api, Action<Action> work, Action<Action> dispatch, Func<DateTime> now)
    { _api = api; _work = work; _dispatch = dispatch; _now = now; }
    public void Bind(NeteaseAccountContext context)
    {
        _context = context; _version++; _intents.Clear(); _confirmed.Clear(); _ordered.Clear();
        Known = Refreshing = false; Error = null; LastAttempt = DateTime.MinValue; LastSuccess = null;
        Changed?.Invoke();
        if (context != null) Refresh();
    }
    private bool Current(NeteaseAccountContext context) => context != null && context.Active && ReferenceEquals(context, _context);
    public bool IsLiked(long id) => _confirmed.Contains(id);
    public bool Target(long id) => _intents.TryGetValue(id, out var intent) && intent.Busy ? intent.Target : IsLiked(id);
    public bool Pending(long id) => _intents.TryGetValue(id, out var intent) && intent.Busy;
    public string SongError(long id) => _intents.TryGetValue(id, out var intent) ? intent.Error : null;
    public void Tick(bool visible, bool enabled, TimeSpan interval)
    { if (visible && enabled && _now() - LastAttempt >= interval) Refresh(); }
    public void Refresh()
    {
        var context = _context;
        if (!Current(context) || Refreshing) return;
        Refreshing = true; LastAttempt = _now(); int version = _version;
        Changed?.Invoke();
        _work(() => {
            NeteaseResult<List<long>> result;
            try { result = _api.Favorites(context, new NeteaseRequestCancellation()); }
            catch { result = NeteaseResult<List<long>>.Fail(NeteaseFailure.Network, "同步失败，状态可能过期"); }
            _dispatch(() => {
                if (!Current(context)) return;
                Refreshing = false;
                // Never commit snapshots spanning writes, including writes still in flight.
                bool pending = false; foreach (var intent in _intents.Values) pending |= intent.Busy;
                if (version != _version || pending) { Changed?.Invoke(); return; }
                if (result.Ok)
                {
                    _ordered = new List<long>(result.Value); _confirmed = new HashSet<long>(_ordered);
                    Known = true; Error = null; LastSuccess = _now();
                    foreach (var intent in _intents.Values) intent.Error = null;
                    Error = null;
                    foreach (var other in _intents.Values) if (other.Error != null) { Error = other.Error; break; }
                    SnapshotChanged?.Invoke();
                }
                else Error = Known ? "同步失败，状态可能过期 · " + result.Message : "喜欢状态未知 · " + result.Message;
                Changed?.Invoke();
            });
        });
    }
    public void SetLiked(long id, bool target)
    {
        if (!Known || !Current(_context) || id <= 0) return;
        if (!_intents.TryGetValue(id, out var intent)) _intents[id] = intent = new Intent();
        intent.Target = target; intent.Error = null;
        if (!intent.Busy && IsLiked(id) != target) Send(id, intent);
        Changed?.Invoke();
    }
    private void Send(long id, Intent intent)
    {
        var context = _context; bool sent = intent.Target;
        intent.Busy = true; intent.Sent = sent; _version++;
        _work(() => {
            NeteaseResult<bool> result;
            try
            {
                result = _api.SetLiked(id, sent, context, new NeteaseRequestCancellation());
                if (result.Failure == NeteaseFailure.UnknownWrite && context.Active)
                {
                    var check = _api.Favorites(context, new NeteaseRequestCancellation());
                    if (check.Ok)
                    {
                        result = check.Value.Contains(id) == sent ? NeteaseResult<bool>.Success(true) :
                            _api.SetLiked(id, sent, context, new NeteaseRequestCancellation());
                        if (result.Failure == NeteaseFailure.UnknownWrite)
                        {
                            var finalCheck = _api.Favorites(context, new NeteaseRequestCancellation());
                            if (finalCheck.Ok && finalCheck.Value.Contains(id) == sent) result = NeteaseResult<bool>.Success(true);
                        }
                    }
                }
            }
            catch { result = NeteaseResult<bool>.Fail(NeteaseFailure.UnknownWrite, "写入结果待确认，请刷新云端喜欢状态"); }
            _dispatch(() => {
                if (!Current(context)) return;
                _version++; intent.Busy = false;
                if (result.Ok)
                {
                    if (sent) { if (_confirmed.Add(id)) _ordered.Insert(0, id); }
                    else { _confirmed.Remove(id); _ordered.RemoveAll(value => value == id); }
                    intent.Error = null;
                    Error = null;
                    foreach (var other in _intents.Values) if (other.Error != null) { Error = other.Error; break; }
                    SnapshotChanged?.Invoke();
                    if (intent.Target != sent) Send(id, intent);
                }
                else
                {
                    intent.Target = IsLiked(id);
                    intent.Error = result.Failure == NeteaseFailure.UnknownWrite ? "写入结果待确认，请刷新云端喜欢状态" : result.Message;
                    Error = intent.Error;
                }
                Changed?.Invoke();
            });
        });
    }
}
