using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class ArenaTests
{
    // These tests state page sizes of their own: the run mode's (YCS_ARENA_PAGE) is off while one runs.
    private static (int First, int Max) _mode;

    [ClassInitialize]
    public static void SaveMode(TestContext context) => _mode = (Arena<char>.FirstPageForTests, Arena<char>.MaxPageForTests);

    [TestInitialize]
    public void NoMode()
    {
        Arena<char>.FirstPageForTests = 0;
        Arena<char>.MaxPageForTests = 0;
    }

    [TestCleanup]
    public void RestoreMode() => (Arena<char>.FirstPageForTests, Arena<char>.MaxPageForTests) = _mode;

    private static char[] Text(int length, char from = 'a') => Enumerable.Range(0, length).Select(i => (char)(from + i % 26)).ToArray();

    private static char[] Fill(int length, char c) => new string(c, length).ToCharArray();

    private static void AssertSound(Arena<char> arena, params (long Offset, int Length)[] live) =>
        Assert.AreEqual(0, arena.Violations(live).Count, string.Join("; ", arena.Violations(live)));

    private static void CompactRuns(Arena<char> arena, IReadOnlyList<(long Offset, int Length)> live, Action<long, long, int> relocate) =>
        arena.Compact(live.Select((r, i) => (r.Offset, r.Length, i)), (i, moved) => relocate(live[i].Offset, moved, live[i].Length));

    [TestMethod]
    public void Append_SmallRuns_PagesDoubleFromTheFirstSizeToTheMaximum()
    {
        var arena = new Arena<char>(2, 8);
        var live = new List<(long, int)>();

        for (var i = 0; i < 8; i++)
        {
            live.Add((arena.Append(Text(2)), 2));
        }

        Assert.AreEqual("2,4,8,8,0", string.Join(",", Enumerable.Range(0, 5).Select(arena.PageCapacity)));
        Assert.AreEqual((3, 0), (Arena<char>.PageOf(live[7].Item1), Arena<char>.IndexOf(live[7].Item1)));
        AssertSound(arena, live.ToArray());
    }

    [TestMethod]
    public void Constructor_LayoutSizes_FirstPageHoldsTheFirstSize()
    {
        using var firstChars = StaticScope.Set(ref Arena<char>.FirstPageForTests, 0);
        using var maxChars = StaticScope.Set(ref Arena<char>.MaxPageForTests, 0);
        using var firstRefs = StaticScope.Set(ref Arena<object>.FirstPageForTests, 0);
        using var maxRefs = StaticScope.Set(ref Arena<object>.MaxPageForTests, 0);
        var chars = new Arena<char>(Arena.CharFirstPage, Arena.CharMaxPage);
        var refs = new Arena<object>(Arena.RefFirstPage, Arena.RefMaxPage);

        chars.Append(Text(1));
        refs.Append(new object[1]);

        Assert.AreEqual((256, 64), (chars.PageCapacity(0), refs.PageCapacity(0)));
    }

    [TestMethod]
    public void Constructor_HooksSet_UsesTinyPages()
    {
        Arena<char>.FirstPageForTests = 1;
        Arena<char>.MaxPageForTests = 4;
        try
        {
            var arena = new Arena<char>(256, 32768);
            for (var i = 0; i < 4; i++)
            {
                arena.Append(Text(1));
            }

            Assert.AreEqual("1,2,4", string.Join(",", Enumerable.Range(0, 3).Select(arena.PageCapacity)));
        }
        finally
        {
            Arena<char>.FirstPageForTests = 0;
            Arena<char>.MaxPageForTests = 0;
        }
    }

    [TestMethod]
    public void Append_RunDoesNotFitTheWritePage_StartsANewPageAndLeavesTheRest()
    {
        var arena = new Arena<char>(4, 8);
        var a = arena.Append(Text(3));

        var b = arena.Append(Text(3, 'x'));

        Assert.AreEqual((0, 1), (Arena<char>.PageOf(a), Arena<char>.PageOf(b)));
        AssertSound(arena, (a, 3), (b, 3));
    }

    [TestMethod]
    public void Append_RunLongerThanAPage_GetsAPageOfItsOwnTwiceItsLength()
    {
        var arena = new Arena<char>(2, 4);
        var small = arena.Append(Text(1));

        var big = arena.Append(Text(10));
        var after = arena.Append(Text(1));

        Assert.AreEqual((20, 0), (arena.PageCapacity(Arena<char>.PageOf(big)), arena.TailCount));
        Assert.AreEqual(0, Arena<char>.PageOf(after) - Arena<char>.PageOf(small)); // the write page is the small one still
        Assert.IsTrue(arena.Slice(big, 10).SequenceEqual(Text(10)));
        AssertSound(arena, (small, 1), (big, 10), (after, 1));
    }

    [TestMethod]
    public void Extend_RunInItsOwnPage_GrowsInPlaceUntilTheSlackIsUsedThenMovesToAPageTwiceAsLong()
    {
        var arena = new Arena<char>(2, 4);
        var big = arena.Append(Text(10));

        var inPlace = arena.Extend(big, 10, Text(10, 'k'));
        var moved = arena.Extend(inPlace, 20, Text(1, 'z'));

        Assert.AreEqual(big, inPlace);
        Assert.AreEqual(42, arena.PageCapacity(Arena<char>.PageOf(moved)));
        Assert.AreEqual((20L, 1L, 0), (arena.Dead, arena.Live - 20, arena.TailCount));
        Assert.IsTrue(arena.Slice(moved, 21).SequenceEqual(Text(10).Concat(Text(10, 'k')).Append('z').ToArray()));
        AssertSound(arena, (moved, 21));
    }

    [TestMethod]
    public void Extend_RunAtTheWriteEnd_AppendsInPlaceWithoutATail()
    {
        var arena = new Arena<char>(16, 64);
        var run = arena.Append(Text(3));

        var same = arena.Extend(run, 3, Text(2, 'x'));

        Assert.AreEqual((run, 0), (same, arena.TailCount));
        AssertSound(arena, (run, 5));
    }

    [TestMethod]
    public void Extend_RunNotAtTheWriteEnd_CopiesWithSlackAndLaterAppendsFillItInPlace()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(2));
        var b = arena.Append(Text(2, 'x'));

        var moved = arena.Extend(a, 2, Text(1, 'q'));
        var again = arena.Extend(moved, 3, Text(3, 'r'));

        Assert.AreNotEqual(a, moved);
        Assert.AreEqual(moved, again);
        Assert.IsTrue(arena.Slice(again, 6).SequenceEqual("abqrst".ToCharArray()));
        Assert.IsTrue(arena.Slice(b, 2).SequenceEqual("xy".ToCharArray()));
        AssertSound(arena, (again, 6), (b, 2));
    }

    [TestMethod]
    public void Extend_SlackUsedUp_MovesAgainAndTheOldRangeAndItsReservationAreDead()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(2));
        arena.Append(Text(2, 'x'));
        var moved = arena.Extend(a, 2, Text(1)); // 3 long, 3 reserved
        var grown = arena.Extend(moved, 3, Text(3));
        arena.Append(Text(1));

        var again = arena.Extend(grown, 6, Text(1));

        Assert.AreEqual((true, 2L + 6, 1, 10L), (again != grown, arena.Dead, arena.TailCount, arena.Live));
    }

    [TestMethod]
    public void Extend_TwoRunsGrowingAlternately_CopyLogarithmicallyOften()
    {
        var arena = new Arena<char>(16, 1 << 16);
        long a = arena.Append(Text(1));
        long b = arena.Append(Text(1, 'x'));
        var copies = 0;
        const int Steps = 5000;

        for (var i = 1; i <= Steps; i++)
        {
            var na = arena.Extend(a, i, Text(1, 'a'));
            var nb = arena.Extend(b, i, Text(1, 'x'));
            copies += (na != a ? 1 : 0) + (nb != b ? 1 : 0);
            (a, b) = (na, nb);
        }

        Assert.IsTrue(copies is > 0 and <= 2 * 16, $"{copies} copies");
        Assert.AreEqual(Steps + 1, arena.Slice(a, Steps + 1).Length);
        AssertSound(arena, (a, Steps + 1), (b, Steps + 1));
    }

    [TestMethod]
    public void Extend_RunLongerThanAPageKeepsGrowing_CopiesLogarithmicallyOften()
    {
        var arena = new Arena<char>(2, 4);
        var run = arena.Append(Text(5));
        var length = 5;
        var copies = 0;

        for (var i = 0; i < 2000; i++)
        {
            var next = arena.Extend(run, length++, Text(1));
            copies += next != run ? 1 : 0;
            run = next;
        }

        Assert.IsTrue(copies is > 0 and <= 10, $"{copies} copies");
        AssertSound(arena, (run, length));
    }

    [TestMethod]
    public void Extend_AfterASplit_OnlyTheRightPartGrowsInPlace()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(4));
        var b = arena.Append(Text(2, 'x'));
        var run = arena.Extend(a, 4, Text(2, 'p')); // abcdpq with a reservation after it
        var right = run + 3;

        var rightGrown = arena.Extend(right, 3, Text(1, 'z'));
        var leftMoved = arena.Extend(run, 3, Text(1, 'y'));

        Assert.AreEqual((right, true), (rightGrown, leftMoved != run));
        Assert.IsTrue(arena.Slice(rightGrown, 4).SequenceEqual("dpqz".ToCharArray()));
        Assert.IsTrue(arena.Slice(leftMoved, 4).SequenceEqual("abcy".ToCharArray()));
        AssertSound(arena, (leftMoved, 4), (rightGrown, 4), (b, 2));
    }

    [TestMethod]
    public void Extend_WholeRunAfterItsHalvesWereSeparateRanges_GrowsInPlaceInTheReservation()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(2));
        arena.Append(Text(2, 'x'));
        var run = arena.Extend(a, 2, Text(2)); // abab, room for 4 more

        var merged = arena.Extend(run, 4, Text(2, 'm')); // the two halves as one range

        Assert.AreEqual(run, merged);
        Assert.IsTrue(arena.Slice(merged, 6).SequenceEqual("ababmn".ToCharArray()));
        Assert.AreEqual(1, arena.TailCount);
    }

    [TestMethod]
    public void MarkDead_RunWithATail_DropsTheTailAndCountsTheReservationDead()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(2));
        arena.Append(Text(2, 'x'));
        var moved = arena.Extend(a, 2, Text(1)); // 3 long, 3 reserved
        var before = arena.Dead;

        arena.MarkDead(moved, 3);

        Assert.AreEqual((0, before + 3 + 3, 2L), (arena.TailCount, arena.Dead, arena.Live));
    }

    [TestMethod]
    public void Compact_PagesBelowHalfLive_MoveTheirRunsAndAreFreed()
    {
        var arena = new Arena<char>(4, 4);
        var runs = new List<(long Offset, int Length)>();
        for (var i = 0; i < 12; i++)
        {
            runs.Add((arena.Append(Text(1, (char)('a' + i))), 1)); // pages 0 to 2, four runs each
        }

        foreach (var i in new[] { 1, 2, 3, 7 })
        {
            arena.MarkDead(runs[i].Offset, 1);
        }

        var live = runs.Where((_, i) => i is not (1 or 2 or 3 or 7)).ToList();
        var moves = new List<(long, long, int)>();

        CompactRuns(arena, live.ToArray(), (o, n, l) => moves.Add((o, n, l)));

        Assert.AreEqual((1, runs[0].Offset, 0, 3), (moves.Count, moves[0].Item1, arena.PageCapacity(0), arena.PageCount));
        Assert.AreEqual((8L, 1L), (arena.Live, arena.Dead));
        Assert.AreEqual("a", new string(arena.Slice(moves[0].Item2, 1)));
        live[0] = (moves[0].Item2, 1);
        AssertSound(arena, live.ToArray());
    }

    [TestMethod]
    public void Compact_MovedRuns_KeepTheirContentAndAreReportedOldToNew()
    {
        var arena = new Arena<char>(4, 8);
        var a = arena.Append(Text(1, 'a'));
        var filler = arena.Append(Text(3, 'b'));
        var b = arena.Append(Text(4, 'c'));
        var c = arena.Append(Text(2, 'd'));
        arena.MarkDead(filler, 3);
        var live = new[] { (a, 1), (b, 4), (c, 2) };
        var map = new Dictionary<long, long>();

        CompactRuns(arena, live, (o, n, l) => map[o] = n);

        Assert.AreEqual((1, true), (map.Count, map.ContainsKey(a)));
        Assert.AreEqual("a", new string(arena.Slice(map[a], 1)));
        Assert.AreEqual(0, arena.Violations(live.Select(r => (map.GetValueOrDefault(r.Item1, r.Item1), r.Item2))).Count);
    }

    [TestMethod]
    public void Compact_Called_ClearsTheTailTable()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(2));
        arena.Append(Text(2, 'x'));
        var moved = arena.Extend(a, 2, Text(1));

        CompactRuns(arena, [(moved, 3)], (_, _, _) => { });

        Assert.AreEqual(0, arena.TailCount);
    }

    [TestMethod]
    public void Extend_AfterACompactionFreedItsPage_NeverUsesAStaleReservation()
    {
        var arena = new Arena<char>(8, 8);
        var a = arena.Append(Text(3, 'a')); // page 0
        var b = arena.Append(Text(3, 'b'));
        var grown = arena.Extend(a, 3, Text(1, 'x')); // page 1: four long, four reserved after it
        var other = arena.Append(Text(1, 'o')); // page 2
        arena.MarkDead(grown, 2); // a split of the run: the left half is garbage
        var kept = grown + 2;
        var live = new List<(long Offset, int Length)> { (b, 3), (kept, 2), (other, 1) };

        CompactRuns(arena, live.ToArray(), (o, n, l) => live[live.FindIndex(r => r.Offset == o)] = (n, l)); // pages 0 and 1 are freed
        var y = arena.Append(Fill(4, 'y')); // takes the number of a freed page
        var z = arena.Append(Fill(2, 'z'));
        var yGrown = arena.Extend(y, 4, Fill(2, '!'));
        live.Add((yGrown, 6));
        live.Add((z, 2));

        Assert.AreEqual(("zz", "yyyy!!"), (new string(arena.Slice(z, 2)), new string(arena.Slice(yGrown, 6))));
        AssertSound(arena, live.ToArray());
    }

    [TestMethod]
    public void Clear_ReferenceRange_DropsTheReferences()
    {
        var arena = new Arena<object>(4, 8);
        var o = new object();
        var run = arena.Append(new[] { o, o, o });

        arena.Clear(run + 1, 2);

        var span = arena.Slice(run, 3);
        Assert.AreEqual((o, null, null), (span[0], span[1], span[2]));
    }

    [TestMethod]
    public void Slice_OffsetOnAFreedPage_Throws()
    {
        var arena = new Arena<char>(2, 2);
        var a = arena.Append(Text(2));
        arena.Append(Text(2));
        arena.MarkDead(a, 2);
        CompactRuns(arena, [], (_, _, _) => { });

        Assert.ThrowsExactly<ArgumentException>(() => arena.Slice(a, 1).ToArray());
    }

    [TestMethod]
    public void Append_Empty_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new Arena<char>(2, 2).Append([]));
    }

    [TestMethod]
    public void Extend_LengthPastTheUsedPart_Throws()
    {
        var arena = new Arena<char>(4, 4);
        var a = arena.Append(Text(2));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => arena.Extend(a, 3, Text(1)));
    }

    [TestMethod]
    public void Violations_OverlappingLiveRanges_AreReported()
    {
        var arena = new Arena<char>(8, 8);
        var a = arena.Append(Text(4));

        Assert.IsTrue(arena.Violations([(a, 3), (a + 2, 2)]).Count > 0);
    }

    [TestMethod]
    public void Violations_RunMissingFromTheLiveRanges_IsReportedAsACountMismatch()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(2));
        arena.Append(Text(2));
        var moved = arena.Extend(a, 2, Text(1));

        Assert.IsTrue(arena.Violations([(moved, 3)]).Any(v => v.Contains("live count")));
    }

    [TestMethod]
    public void Violations_NoLiveRangeEndsAtATail_IsReported()
    {
        var arena = new Arena<char>(16, 64);
        var a = arena.Append(Text(2));
        var b = arena.Append(Text(2));
        var moved = arena.Extend(a, 2, Text(1));

        Assert.IsTrue(arena.Violations([(moved, 2), (b, 2)]).Any(v => v.Contains("tail")));
    }

    [TestMethod]
    public void Violations_RangeOnAFreedPage_IsReported()
    {
        var arena = new Arena<char>(2, 2);
        arena.Append(Text(2));
        var gone = arena.Append(Text(2));
        arena.Append(Text(2));
        arena.MarkDead(gone, 2);
        CompactRuns(arena, [], (_, _, _) => { });

        Assert.IsTrue(arena.Violations([(gone, 2)]).Count > 0);
    }

#if DEBUG
    [TestMethod]
    public void Compact_SpanKeptAcrossIt_ShowsPoisonInDebug()
    {
        var arena = new Arena<char>(2, 2);
        var a = arena.Append(Text(2));
        arena.Append(Text(2));
        var kept = arena.Slice(a, 2);

        arena.MarkDead(a, 2);
        CompactRuns(arena, [], (_, _, _) => { });

        Assert.AreEqual('婚', kept[0]);
    }
#endif

    [TestMethod]
    public void RandomOperations_AgainstAModel_ContentAndCountersAgree()
    {
        for (var seed = 1; seed <= 60; seed++)
        {
            var (first, max) = (seed % 3) switch { 0 => (1, 4), 1 => (2, 16), _ => (8, 64) };
            RunRandom(seed, first, max, 400, false);
            RunRandom(seed, first, max, 400, true);
        }
    }

    private sealed class Run
    {
        public long Offset;
        public int Length;
        public List<char> Expected = [];
    }

    private static void RunRandom(int seed, int first, int max, int steps, bool forceCompact)
    {
        var random = new Random(seed);
        var arena = new Arena<char>(first, max);
        var runs = new List<Run>();

        IEnumerable<(long, int)> Live()
        {
            foreach (var run in runs.ToArray())
            {
                yield return (run.Offset, run.Length);
            }
        }

        void Check()
        {
            Assert.AreEqual(0, arena.Violations(Live()).Count, $"seed {seed}: {string.Join("; ", arena.Violations(Live()))}");
            foreach (var run in runs)
            {
                Assert.IsTrue(arena.Slice(run.Offset, run.Length).ToArray().SequenceEqual(run.Expected), $"seed {seed}: content of a run");
            }
        }

        for (var step = 0; step < steps; step++)
        {
            var text = Text(random.Next(1, random.Next(8) == 0 ? max * 3 : 6), (char)('A' + random.Next(26)));
            var pick = runs.Count == 0 ? -1 : random.Next(runs.Count);
            switch (runs.Count == 0 ? 0 : random.Next(forceCompact ? 12 : 11))
            {
                case 0 or 1 or 2:
                    runs.Add(new Run { Offset = arena.Append(text), Length = text.Length, Expected = [.. text] });
                    break;
                case 3 or 4 or 5 or 6:
                    var run = runs[pick];
                    run.Offset = arena.Extend(run.Offset, run.Length, text);
                    run.Length += text.Length;
                    run.Expected.AddRange(text);
                    break;
                case 7 when runs[pick].Length > 1: // split: two ranges
                    var whole = runs[pick];
                    var cut = random.Next(1, whole.Length);
                    var right = new Run { Offset = whole.Offset + cut, Length = whole.Length - cut, Expected = whole.Expected.GetRange(cut, whole.Length - cut) };
                    whole.Expected.RemoveRange(cut, whole.Length - cut);
                    whole.Length = cut;
                    runs.Insert(pick + 1, right);
                    break;
                case 8: // merge with the next range when adjacent
                    if (pick + 1 < runs.Count && runs[pick].Offset + runs[pick].Length == runs[pick + 1].Offset)
                    {
                        runs[pick].Length += runs[pick + 1].Length;
                        runs[pick].Expected.AddRange(runs[pick + 1].Expected);
                        runs.RemoveAt(pick + 1);
                    }

                    break;
                case 9 or 10:
                    arena.MarkDead(runs[pick].Offset, runs[pick].Length);
                    runs.RemoveAt(pick);
                    break;
                default:
                    Compact();
                    break;
            }

            if (random.Next(25) == 0)
            {
                Compact();
            }

            Check();
        }

        void Compact()
        {
            var snapshot = runs.ToArray();
            arena.Compact(snapshot.Select((r, i) => (r.Offset, r.Length, i)), (i, moved) => snapshot[i].Offset = moved);
        }
    }

    [TestMethod]
    public void Overwrite_WithinTheUsedPart_ReplacesTheElementsAndChangesNoCount()
    {
        var arena = new Arena<char>(8, 8);
        var offset = arena.Append(Text(4));

        arena.Overwrite(offset + 1, ['X', 'Y']);

        Assert.AreEqual(("aXYd", 4L, 0L), (arena.Slice(offset, 4).ToString(), arena.Live, arena.Dead));
    }

    [TestMethod]
    public void Overwrite_PastTheUsedPart_Throws()
    {
        var arena = new Arena<char>(8, 8);
        var offset = arena.Append(Text(4));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => arena.Overwrite(offset + 3, ['X', 'Y']));
    }
}
