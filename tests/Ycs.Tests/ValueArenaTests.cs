using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The range of an Any slot in the slab's value arena, the only holder of the values, as each write path keeps it (the store's
/// checks, which every transaction of every suite runs, find a range outside a held page, a range that overlaps another and
/// a value that an exited range left behind; these tests state the counters and offsets, and that a value is collectible
/// before any compaction). They run with the default page sizes and merge rule and without forced compaction whatever the
/// run mode.
/// </summary>
[TestClass]
public class ValueArenaTests
{
    private static (int First, int Max, bool Copy, TestHooks.CompactionMode Compaction) _mode;

    [ClassInitialize]
    public static void SaveMode(TestContext context) => _mode = (TestHooks.ArenaPages<object?>.First, TestHooks.ArenaPages<object?>.Max, TestHooks.CopyOnMerge, TestHooks.Compaction);

    [TestInitialize]
    public void NoMode()
    {
        TestHooks.ArenaPages<object?>.First = 0;
        TestHooks.ArenaPages<object?>.Max = 0;
        TestHooks.CopyOnMerge = false;
        TestHooks.Compaction = TestHooks.CompactionMode.None;
    }

    [TestCleanup]
    public void RestoreMode() => (TestHooks.ArenaPages<object?>.First, TestHooks.ArenaPages<object?>.Max, TestHooks.CopyOnMerge, TestHooks.Compaction) = _mode;

    private static YDoc NewDoc(long client = 1) => new(new YDocOptions { ClientId = client }) { RunExtensionsDisabledForTests = true };

    private static ItemRef At(YDoc doc, long client, long clock) => doc.Store.Find(new ItemId(client, clock));

    private static long Payload(YDoc doc, ItemRef item) => doc.Store.Slab.Read(item.Handle).Payload;

    private static string Range(YDoc doc, ItemRef item) => string.Join(",", doc.Store.Slab.Values.Slice(Payload(doc, item), item.Length).ToArray());

    private static (long Live, long Dead, int Tails) Counts(YDoc doc)
    {
        var values = doc.Store.Slab.Values;
        return (values.Live, values.Dead, values.TailCount);
    }

    private static void AddRange(YArray array, int from, int count)
    {
        for (var i = 0; i < count; i++)
        {
            array.Add(from + i);
        }
    }

    [TestMethod]
    public void Insert_Values_RangeHoldsTheValues()
    {
        var doc = NewDoc();
        doc.GetArray("a").Add(1, 2, 3);

        Assert.AreEqual(("1,2,3", (3L, 0L, 0)), (Range(doc, At(doc, 1, 0)), Counts(doc)));
    }

    [TestMethod]
    public void Pushing_ExtendingARun_KeepsTheOffsetAndCountsNothingDead()
    {
        using var first64 = StaticScope.Set(ref TestHooks.ArenaPages<object>.First, 64);
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        array.Add(0);
        var first = Payload(doc, At(doc, 1, 0));

        AddRange(array, 1, 59);

        var item = At(doc, 1, 0);
        Assert.AreEqual((60, first, 60L, 0L), (item.Length, Payload(doc, item), doc.Store.Slab.Values.Live, doc.Store.Slab.Values.Dead));
    }

    [TestMethod]
    public void Pushing_RunOutgrowsItsRoom_CopiesWithSlackAndTheOldRangeIsDeadAndCleared()
    {
        using var first64 = StaticScope.Set(ref TestHooks.ArenaPages<object>.First, 64);
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        AddRange(array, 0, 63);
        var first = Payload(doc, At(doc, 1, 0));

        AddRange(array, 63, 2);

        var item = At(doc, 1, 0);
        var values = doc.Store.Slab.Values;
        Assert.AreEqual((65, true, 65L, 64L, 0), (item.Length, Payload(doc, item) != first, values.Live, values.Dead, values.UnclearedOutside([(Payload(doc, item), item.Length)])));
    }

    [TestMethod]
    public void Split_InTheMiddle_RightPartRangeStartsAtPayloadPlusDiff()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1, 2, 3, 4);

        array.Insert(2, 9);

        var left = At(doc, 1, 0);
        var right = At(doc, 1, 2);
        Assert.AreEqual((Payload(doc, left) + 2, "1,2", "3,4", (5L, 0L, 0)), (Payload(doc, right), Range(doc, left), Range(doc, right), Counts(doc)));
    }

    [TestMethod]
    public void Merge_RangesAdjacent_TheRangeExtendsAndNothingIsDead()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        doc.Transact(_ =>
        {
            array.Add(1, 2);
            array.Add(3, 4);
        });

        var item = At(doc, 1, 0);
        Assert.AreEqual((1, "1,2,3,4", (4L, 0L, 0)), (doc.Store.Clients[1].Count, Range(doc, item), Counts(doc)));
    }

    [TestMethod]
    public void Merge_RangesNotAdjacent_CopiesWithSlackAndBothOldRangesAreDeadAndCleared()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1, 2);
        var peer = NewDoc(2);
        peer.GetArray("o").Add(8, 9);
        doc.ApplyUpdate(peer.EncodeStateAsUpdate());

        array.Add(3, 4);

        var item = At(doc, 1, 0);
        Assert.AreEqual((1, "1,2,3,4", (6L, 4L, 1), 0), (doc.Store.Clients[1].Count, Range(doc, item), Counts(doc), doc.Store.Slab.Values.UnclearedOutside(Ranges(doc))));
    }

    [TestMethod]
    public void Merge_CopyOnMergeSwitch_CopiesAdjacentRanges()
    {
        TestHooks.CopyOnMerge = true;
        var doc = NewDoc();
        var array = doc.GetArray("a");
        doc.Transact(_ =>
        {
            array.Add(1, 2);
            array.Add(3, 4);
        });

        Assert.AreEqual((1, "1,2,3,4", (4L, 4L), 0), (doc.Store.Clients[1].Count, Range(doc, At(doc, 1, 0)), (doc.Store.Slab.Values.Live, doc.Store.Slab.Values.Dead), doc.Store.Slab.Values.UnclearedOutside(Ranges(doc))));
    }

    [TestMethod]
    public void Split_RunWithAReservation_TheRightPartKeepsItAndNothingIsDeadBut64()
    {
        using var first64 = StaticScope.Set(ref TestHooks.ArenaPages<object>.First, 64);
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        AddRange(array, 0, 63);
        AddRange(array, 63, 2);

        array.Insert(30, 99);

        Assert.AreEqual((66L, 64L, 1), Counts(doc));
    }

    [TestMethod]
    public void Gc_DeletedItem_RangeDiesIsClearedAndTheSlotHoldsNoOffset()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1, 2, 3);

        array.RemoveRange(0, 3);

        var item = At(doc, 1, 0);
        Assert.AreEqual((SlotKind.Deleted, 0L, (0L, 3L, 0), 0), (item.Kind, Payload(doc, item), Counts(doc), doc.Store.Slab.Values.UnclearedOutside(Ranges(doc))));
    }

    [TestMethod]
    public void Gc_Disabled_DeletedItemKeepsItsRange()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false }) { RunExtensionsDisabledForTests = true };
        var array = doc.GetArray("a");
        array.Add(1, 2, 3);

        array.RemoveRange(0, 3);

        Assert.AreEqual(("1,2,3", (3L, 0L, 0)), (Range(doc, At(doc, 1, 0)), Counts(doc)));
    }

    [TestMethod]
    public void Gc_ParentCollected_RangeOfANestedArrayDies()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        var inner = new YArray();
        map["t"] = inner;
        inner.Add(1, 2, 3);

        map.Remove("t");

        Assert.AreEqual((0L, 3L, 0), (doc.Store.Slab.Values.Live, doc.Store.Slab.Values.Dead, doc.Store.Slab.Values.UnclearedOutside([])));
    }

    [TestMethod]
    public void Undo_RedoneValues_GetARangeOfTheirOwn()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var undo = new UndoManager(array);
        array.Add(1, 2, 3);
        undo.StopCapturing();
        array.RemoveRange(0, 3);
        undo.StopCapturing();

        undo.Undo();

        var ranges = doc.Store.Clients[1].Where(s => s.Kind == SlotKind.Any).Select(s => (Payload(doc, s), Range(doc, s))).ToList();
        Assert.AreEqual((true, 2), (ranges.All(r => r.Item2 == "1,2,3"), ranges.Select(r => r.Item1).Distinct().Count()));
    }

    [TestMethod]
    public void Apply_ItemIntegratedFromAnOffset_CopiesOnlyTheRestAndLeavesNothingDead()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        a.GetArray("a").Add(1, 2);
        var b = NewDoc(2);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        a.GetArray("a").Add(3, 4);

        b.ApplyUpdate(a.EncodeStateAsUpdate());

        var values = b.Store.Slab.Values;
        Assert.AreEqual((4, 4L, 0L, 0), (b.GetArray("a").Length, values.Live, values.Dead, values.UnclearedOutside(Ranges(b))));
    }

    [TestMethod]
    public void Push_RunLongerThanAPage_GetsAPageOfItsOwnAndExtendsInIt()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        doc.Transact(_ => AddRange(array, 0, 10_000));
        var first = Payload(doc, At(doc, 1, 0));

        AddRange(array, 10_000, 2);

        var item = At(doc, 1, 0);
        Assert.AreEqual((10_002, first, 10_002L, 0L), (item.Length, Payload(doc, item), doc.Store.Slab.Values.Live, doc.Store.Slab.Values.Dead));
    }

    [TestMethod]
    public void Read_ByteArrayValue_IsACopyOfTheStoredOne()
    {
        var slab = new Slab();
        var item = ItemRef.New(slab, new ItemId(1, 0), default, default, null, null, new object[] { new byte[] { 1, 2 } }.AsSpan());
        var stored = slab.Values.Slice(slab.Read(item.Handle).Payload, 1)[0];

        var read = item.ValueAt(0);
        var viewed = item.Values[0];

        Assert.AreEqual((false, false, true), (ReferenceEquals(stored, read), ReferenceEquals(stored, viewed), ((byte[])read).AsSpan().SequenceEqual((byte[])stored)));
    }

    [TestMethod]
    public void Values_Snapshot_IsReadOnlyAndKeepsItsValuesAfterTheItemChanges()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1, 2);
        var item = At(doc, 1, 0);

        var snapshot = item.Values;
        array.RemoveRange(0, 2);

        Assert.AreEqual((false, 2), (snapshot is IList<object>, snapshot.Count));
    }

    [TestMethod]
    public void Stored_AfterTheItemMergedAway_StillReadsItsValues()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        array.Add(1, 2);
        var stored = At(doc, 1, 0).Stored;

        array.RemoveRange(0, 2);
        array.Add(7, 8, 9);

        Assert.AreEqual((1, 2), (stored.ValueAt(0), stored.ValueAt(1)));
    }

    [TestMethod]
    public void Stored_ByteArrayValue_IsACopyOnEveryRead()
    {
        var doc = NewDoc();
        doc.GetArray("a").Add(new byte[] { 1, 2 });
        var stored = At(doc, 1, 0).Stored;

        var first = (byte[])stored.ValueAt(0);
        first[0] = 9;

        Assert.AreEqual(1, ((byte[])stored.ValueAt(0))[0]);
    }

    [TestMethod]
    public void ValueAt_NotAnAny_Throws()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => At(doc, 1, 0).ValueAt(0));
    }

    [TestMethod]
    public void Compaction_ManyDeletedRuns_MovesTheLiveRangesAndTheEncodingStays()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = true });
        var array = doc.GetArray("a");
        for (var i = 0; i < 300; i++)
        {
            array.Add(i);
            array.Add("x" + i);
            doc.GetMap("m")["k" + (i % 3)] = i;
        }

        array.RemoveRange(0, 400);
        var before = doc.EncodeStateAsUpdate();
        var pages = doc.Store.Slab.Values.PageCount;

        doc.Store.Slab.CompactValues();

        var values = doc.Store.Slab.Values;
        Assert.AreEqual((true, 0, true), (values.PageCount <= pages, values.UnclearedOutside(Ranges(doc)), before.AsSpan().SequenceEqual(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void Compact_MovedRun_OldValuesAreClearedInThePageItLeft()
    {
        var arena = new Arena<object?>(4, 4);
        var kept = arena.Append(["a"]);
        var other = arena.Append(["b", "c", "d"]);
        var onlyPage = arena.PageDataForTests(Arena<object?>.PageOf(kept))!;
        arena.Clear(other, 3);
        arena.MarkDead(other, 3);
        var moved = kept;

        arena.Compact([(kept, 1, 0)], (_, offset) => moved = offset);

        Assert.AreEqual((true, "a", 1L), (moved != kept, arena.Slice(moved, 1)[0], arena.Live));
        Assert.IsTrue(onlyPage.All(v => v == null), "the page the run left still holds a value");
    }

    [TestMethod]
    public void UnclearedOutside_ADeadRangeThatKeepsValues_IsCounted()
    {
        var arena = new Arena<object?>(4, 4);
        var run = arena.Append(["a", "b"]);
        arena.MarkDead(run, 2);

        Assert.AreEqual(2, arena.UnclearedOutside([]));
    }

    private static List<(long, int)> Ranges(YDoc doc) =>
        [.. doc.Store.Clients.Values.SelectMany(list => list).Where(s => s.IsItem && s.Kind == SlotKind.Any).Select(s => (Payload(doc, s), s.Length))];

    // A value as the arena holds it, a string nothing else references.
    private static string Fresh(int length) => new string('v', length);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] DeleteAndCollect(YDoc doc)
    {
        var array = doc.GetArray("a");
        var one = Fresh(11);
        var two = Fresh(12);
        array.Add(one, two);
        var item = At(doc, 1, 0);
        var weak = new[] { new WeakReference(doc.Store.Slab.Values.Slice(Payload(doc, item), 2)[0]), new WeakReference(doc.Store.Slab.Values.Slice(Payload(doc, item), 2)[1]) };
        array.RemoveRange(0, 2);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CopyingMergeThenDelete(YDoc doc)
    {
        var array = doc.GetArray("a");
        array.Add(Fresh(11));
        var peer = NewDoc(2);
        peer.GetArray("o").Add(Fresh(5));
        doc.ApplyUpdate(peer.EncodeStateAsUpdate());
        array.Add(Fresh(12));
        var item = At(doc, 1, 0);
        var range = doc.Store.Slab.Values.Slice(Payload(doc, item), item.Length);
        var weak = new[] { new WeakReference(range[0]), new WeakReference(range[1]) };
        array.RemoveRange(0, 2);
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] NestedTypeCollected(YDoc doc)
    {
        var map = doc.GetMap("m");
        var inner = new YArray();
        map["t"] = inner;
        inner.Add(Fresh(11), Fresh(12));
        var item = doc.Store.Clients[1].First(s => s.Kind == SlotKind.Any);
        var range = doc.Store.Slab.Values.Slice(Payload(doc, item), item.Length);
        var weak = new[] { new WeakReference(range[0]), new WeakReference(range[1]) };
        map.Remove("t");
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] FailedAdoption(YDoc doc)
    {
        doc.Store.AddGcForTests(new ItemId(1, 0), 1);
        doc.GetArray("a");
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var i = table.Add(StagedKind.Any, 2, null, null);
        table.SetParentRoot(i, "a");
        table.Values(i)[0] = Fresh(11);
        table.Values(i)[1] = Fresh(12);
        var weak = new[] { new WeakReference(table.Values(i)[0]), new WeakReference(table.Values(i)[1]) };

        Assert.ThrowsExactly<YUpdateException>(() => doc.Transact(tr => ItemRef.Adopt(table, i, tr, 1, true)));
        table.Reset();
        return weak;
    }

    private static bool AnyAlive(WeakReference[] weak)
    {
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
        return weak.Any(w => w.IsAlive);
    }

    [TestMethod]
    public void Gc_DeletedValues_AreCollectibleBeforeAnyCompaction()
    {
        var doc = NewDoc();
        var weak = DeleteAndCollect(doc);

        Assert.AreEqual((false, 0), (AnyAlive(weak), doc.Store.Slab.Values.CompactionCount));
    }

    [TestMethod]
    public void Merge_CopyingMergeThenDeleteAndGc_ValuesAreCollectibleBeforeAnyCompaction()
    {
        var doc = NewDoc();
        var weak = CopyingMergeThenDelete(doc);

        Assert.AreEqual((false, 0, 1), (AnyAlive(weak), doc.Store.Slab.Values.CompactionCount, doc.Store.Clients[1].Count));
    }

    [TestMethod]
    public void Merge_CopyOnMergeSwitch_ValuesAreCollectibleBeforeAnyCompaction()
    {
        TestHooks.CopyOnMerge = true;
        var doc = NewDoc();
        var array = doc.GetArray("a");
        WeakReference[] weak = null;
        doc.Transact(_ =>
        {
            weak = Pair(array);
        });
        array.RemoveRange(0, 4);

        Assert.AreEqual((false, 0), (AnyAlive(weak), doc.Store.Slab.Values.CompactionCount));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] Pair(YArray array)
    {
        var values = new[] { Fresh(11), Fresh(12), Fresh(13), Fresh(14) };
        array.Add(values[0], values[1]);
        array.Add(values[2], values[3]);
        return [.. values.Select(v => new WeakReference(v))];
    }

    [TestMethod]
    public void Gc_NestedTypeCollected_ValuesAreCollectibleBeforeAnyCompaction()
    {
        var doc = NewDoc();
        var weak = NestedTypeCollected(doc);

        Assert.AreEqual((false, 0), (AnyAlive(weak), doc.Store.Slab.Values.CompactionCount));
    }

    [TestMethod]
    public void Adopt_IntegrationFails_ValuesAreCollectibleAndTheRangeIsDead()
    {
        var doc = NewDoc();
        var weak = FailedAdoption(doc);

        Assert.AreEqual((false, 0L, 1L, 0), (AnyAlive(weak), doc.Store.Slab.Values.Live, doc.Store.Slab.Values.Dead, doc.Store.Slab.Values.UnclearedOutside([])));
    }
}
