using System;
using System.Collections.Generic;
using MusicBridge;

internal static class ShuffleNavigatorTests
{
    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    private static void Expect(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); }

    public static void Run()
    {
        for (int size = 1; size <= 25; size++)
        for (int seed = 0; seed < 20; seed++)
        {
            var queue = new List<ShuffleNavigator.Candidate>();
            for (int i = 0; i < size; i++) queue.Add(new ShuffleNavigator.Candidate(i + 1, i));
            queue.Add(new ShuffleNavigator.Candidate(1, size)); // duplicate list row is one song
            var nav = new ShuffleNavigator(seed);
            nav.Begin(queue, 1);
            nav.ConfirmPlaying(1);
            var seen = new HashSet<long> { 1 };
            long lastPlayed = 1;
            for (int i = 1; i < size; i++)
            {
                var first = nav.PeekNext(false);
                var again = nav.PeekNext(false);
                Expect(first.HasValue && again.HasValue && first.Value.Id == again.Value.Id,
                    "peek stable size=" + size + " seed=" + seed + " step=" + i);
                Expect(!nav.Consume(first.Value.Id + 1), "stale plan rejected");
                Expect(nav.Consume(first.Value.Id), "plan consumed once");
                Expect(!nav.Consume(first.Value.Id), "plan not consumed twice");
                Expect(seen.Add(first.Value.SongId), "round has no repeated SongId");
                lastPlayed = first.Value.SongId;
                nav.ConfirmPlaying(first.Value.SongId);
            }
            Expect(!nav.PeekNext(false).HasValue && seen.Count == size, "round exhaustion");
            var nextRound = nav.PeekNext(true);
            Expect(nextRound.HasValue && (size == 1 || nextRound.Value.SongId != lastPlayed), "repeat boundary");
        }
        Check(true, "shuffle permutations, deduplication, stable plan and round boundary (500 cases)");
        var large = new List<ShuffleNavigator.Candidate>();
        for (int i = 0; i < 1000; i++) large.Add(new ShuffleNavigator.Candidate(i + 1, i));
        var big = new ShuffleNavigator(19);
        big.Begin(large, 501); big.ConfirmPlaying(501);
        var peek = big.PeekNext(false).Value;
        for (int i = 0; i < 1000; i++)
            Expect(big.PeekNext(false).Value.Id == peek.Id, "1000 peeks retain one plan");
        var unique = new HashSet<long> { 501 };
        for (int i = 1; i < 1000; i++)
        {
            var plan = big.PeekNext(false).Value;
            Expect(unique.Add(plan.SongId) && big.Consume(plan.Id), "1000-song permutation");
            big.ConfirmPlaying(plan.SongId);
        }
        Check(unique.Count == 1000 && !big.PeekNext(false).HasValue,
            "1000-song round and 1000 read-only peeks are complete");
        var empty = new ShuffleNavigator(1);
        empty.Begin(Array.Empty<ShuffleNavigator.Candidate>(), 0);
        Check(!empty.PeekNext(true).HasValue, "empty queue has no plan");

        var history = new ShuffleNavigator(4);
        history.Begin(new[] { new ShuffleNavigator.Candidate(1, 0), new ShuffleNavigator.Candidate(2, 1),
            new ShuffleNavigator.Candidate(3, 2) }, 1);
        history.ConfirmPlaying(1);
        var p = history.PeekNext(false).Value; history.Consume(p.Id); history.ConfirmPlaying(p.SongId);
        int previous = history.Previous().Value;
        Check(previous == 0, "previous returns confirmed history");
        history.ConfirmPlaying(1);
        Check(history.PeekNext(false).Value.SongId == p.SongId, "next walks forward history");

        var duplicate = new ShuffleNavigator(7);
        duplicate.Begin(new[] { new ShuffleNavigator.Candidate(11, 0),
            new ShuffleNavigator.Candidate(12, 1), new ShuffleNavigator.Candidate(11, 2) }, 11);
        duplicate.ConfirmPlaying(11);
        Check(duplicate.PeekNext(false).Value.SongId == 12, "duplicate current row is excluded by SongId");

        var rapid = new ShuffleNavigator(2);
        rapid.Begin(new[] { new ShuffleNavigator.Candidate(1, 0), new ShuffleNavigator.Candidate(2, 1),
            new ShuffleNavigator.Candidate(3, 2), new ShuffleNavigator.Candidate(4, 3) }, 1);
        rapid.ConfirmPlaying(1);
        var rapidFirst = rapid.PeekNext(false).Value; rapid.Consume(rapidFirst.Id);
        var rapidSecond = rapid.PeekNext(false).Value; rapid.Consume(rapidSecond.Id);
        Check(rapidFirst.SongId != rapidSecond.SongId, "rapid next advances unconfirmed candidates");
        Check(rapid.CanPrevious, "loading candidate exposes cancel-to-history previous capability");
        Check(rapid.Previous() == 0, "previous during loading returns last confirmed track");
        rapid.ConfirmPlaying(1);
        Check(rapid.HistoryCursor == 0, "cancelled loading attempts never enter history");

        var old = rapid.PeekNext(false).Value;
        rapid.Begin(new[] { new ShuffleNavigator.Candidate(7, 0), new ShuffleNavigator.Candidate(8, 1) }, 7);
        Check(!rapid.Consume(old.Id), "new queue invalidates old plan id");
    }
}
