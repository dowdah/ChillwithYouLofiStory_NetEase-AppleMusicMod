using System;
using System.Collections.Generic;

namespace MusicBridge;

// One ordinary NetEase queue owns one navigator. Peeking never consumes a plan.
internal sealed class ShuffleNavigator
{
    internal readonly struct Candidate
    {
        public readonly long SongId;
        public readonly int QueueIndex;
        public Candidate(long songId, int queueIndex) { SongId = songId; QueueIndex = queueIndex; }
    }

    internal readonly struct Plan
    {
        public readonly long Id;
        public readonly int QueueIndex;
        public readonly long SongId;
        public readonly bool FromHistory;
        public Plan(long id, Candidate target, bool fromHistory)
        { Id = id; QueueIndex = target.QueueIndex; SongId = target.SongId; FromHistory = fromHistory; }
    }

    private readonly Random _random;
    private readonly List<Candidate> _eligible = new List<Candidate>();
    private readonly List<Candidate> _round = new List<Candidate>();
    private readonly List<Candidate> _history = new List<Candidate>();
    private int _roundCursor;
    private int _historyCursor = -1;
    private long _nextPlanId;
    private Plan? _prepared;
    private Candidate? _pending;
    private int? _pendingHistoryCursor;
    private long _lastSongId;
    public long QueueEpoch { get; private set; }
    public long RoundId { get; private set; }
    public int HistoryCursor => _historyCursor;
    public long PreparedPlanId => _prepared?.Id ?? 0;
    public bool CanPrevious => _historyCursor > 0 || (_pending.HasValue && _historyCursor >= 0);

    public ShuffleNavigator(int? seed = null) { _random = seed.HasValue ? new Random(seed.Value) : new Random(); }

    public void Begin(IList<Candidate> queue, long currentSongId)
    {
        QueueEpoch++;
        RoundId = 0;
        _eligible.Clear(); _round.Clear(); _history.Clear();
        _historyCursor = -1; _roundCursor = 0; _prepared = null; _pending = null;
        _pendingHistoryCursor = null; _lastSongId = 0;
        var seen = new HashSet<long>();
        if (queue != null)
            foreach (var candidate in queue)
                if (candidate.SongId > 0 && seen.Add(candidate.SongId)) _eligible.Add(candidate);
        Candidate? current = null;
        foreach (var candidate in _eligible)
            if (candidate.SongId == currentSongId) { current = candidate; break; }
        if (current.HasValue) { _pending = current; _lastSongId = current.Value.SongId; }
        StartRound(current?.SongId ?? 0);
    }

    private void StartRound(long exclude)
    {
        RoundId++;
        _round.Clear();
        foreach (var candidate in _eligible)
            if (candidate.SongId != exclude) _round.Add(candidate);
        for (int i = _round.Count - 1; i > 0; i--)
        { int j = _random.Next(i + 1); var tmp = _round[i]; _round[i] = _round[j]; _round[j] = tmp; }
        _roundCursor = 0;
    }

    public Plan? PeekNext(bool repeatQueue)
    {
        if (_prepared.HasValue) return _prepared;
        int forwardCursor = _pendingHistoryCursor ?? _historyCursor;
        if (forwardCursor >= 0 && forwardCursor + 1 < _history.Count)
        {
            var target = _history[forwardCursor + 1];
            return _prepared = new Plan(++_nextPlanId, target, true);
        }
        if (_roundCursor >= _round.Count)
        {
            if (!repeatQueue || _eligible.Count == 0) return null;
            if (_eligible.Count == 1)
            {
                _round.Clear(); _round.Add(_eligible[0]); _roundCursor = 0; RoundId++;
            }
            else
            {
                // The next round is prepared once. Its first song must differ from the last.
                StartRound(0);
                if (_round[0].SongId == _lastSongId)
                { var tmp = _round[0]; _round[0] = _round[1]; _round[1] = tmp; }
            }
        }
        return _prepared = new Plan(++_nextPlanId, _round[_roundCursor], false);
    }

    public bool Consume(long planId)
    {
        if (!_prepared.HasValue || _prepared.Value.Id != planId) return false;
        var plan = _prepared.Value;
        _prepared = null;
        _pending = new Candidate(plan.SongId, plan.QueueIndex);
        _pendingHistoryCursor = plan.FromHistory ? (_pendingHistoryCursor ?? _historyCursor) + 1 : (int?)null;
        if (!plan.FromHistory) _roundCursor++;
        return true;
    }

    public int? Previous()
    {
        _prepared = null;
        if (_pending.HasValue && _historyCursor >= 0)
        {
            _pending = _history[_historyCursor]; _pendingHistoryCursor = _historyCursor;
            return _pending.Value.QueueIndex;
        }
        if (_historyCursor <= 0) return null;
        _pendingHistoryCursor = _historyCursor - 1;
        _pending = _history[_pendingHistoryCursor.Value];
        return _pending.Value.QueueIndex;
    }

    public void ConfirmPlaying(long songId)
    {
        if (!_pending.HasValue || _pending.Value.SongId != songId) return;
        if (_pendingHistoryCursor.HasValue)
            _historyCursor = _pendingHistoryCursor.Value;
        else if (_historyCursor < 0 || _history[_historyCursor].SongId != songId)
        {
            if (_historyCursor + 1 < _history.Count) _history.RemoveRange(_historyCursor + 1, _history.Count - _historyCursor - 1);
            _history.Add(_pending.Value);
            if (_history.Count > 512) _history.RemoveAt(0);
            _historyCursor = _history.Count - 1;
        }
        _lastSongId = songId;
        _pending = null; _pendingHistoryCursor = null;
    }

    public void InvalidatePlan() { _prepared = null; }
}
