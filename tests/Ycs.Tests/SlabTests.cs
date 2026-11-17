using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class SlabTests
{
    private static void AssertSound(Slab slab) => Assert.AreEqual(0, slab.Violations().Count, string.Join("; ", slab.Violations()));

    [TestMethod]
    public void Allocate_PageZeroFromOneSlot_DoublesToTheFullPageThenOpensPageOne()
    {
        var slab = new Slab(1);
        var capacities = new List<int>();
        var handles = new List<int>();
        for (var i = 0; i < Slab.PageSlots + 1; i++)
        {
            handles.Add(slab.Allocate());
            if (capacities.Count == 0 || capacities[^1] != slab.FirstPageCapacity)
            {
                capacities.Add(slab.FirstPageCapacity);
            }
        }

        Assert.IsTrue(capacities.SequenceEqual(new[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024 }));
        Assert.AreEqual(2, slab.PageCount);
        Assert.AreEqual(1, Slab.PageOf(handles[^1]));
        AssertSound(slab);
    }

    [TestMethod]
    public void Constructor_NoSizeGiven_StartsPageZeroAtSixteen()
    {
        var slab = new Slab();
        slab.Allocate();

        // A run mode (YCS_SLAB_FIRST) sets the hook for the whole run.
        Assert.AreEqual(Slab.FirstPageSlotsForTests != 0 ? Slab.FirstPageSlotsForTests : 1, slab.FirstPageCapacity);
    }

    [TestMethod]
    public void Constructor_HookSet_StartsPageZeroThere()
    {
        using var _ = StaticScope.Set(ref Slab.FirstPageSlotsForTests, 1);
        var slab = new Slab();
        slab.Allocate();

        Assert.AreEqual(1, slab.FirstPageCapacity);
    }

    [TestMethod]
    public void Allocate_NewSlot_IsEmpty()
    {
        var slab = new Slab(1);

        var handle = slab.Allocate();

        var slot = slab.At(handle);
        Assert.AreEqual((0L, 0, -1, -1, -1, -1, -1, 0), (slot.Clock, slot.Length, slot.OriginClient, slot.ROriginClient, slot.Left, slot.Right, slot.Parent, slot.Info));
    }

    [TestMethod]
    public void At_AfterPageZeroGrew_KeepsEveryHandleAndItsFields()
    {
        var slab = new Slab(1);
        var handles = new List<int>();
        for (var i = 0; i < 300; i++)
        {
            var h = slab.Allocate();
            slab.At(h).Clock = i * 7;
            slab.SetRef(h, "r" + i);
            slab.SetKey(h, "k" + i);
            handles.Add(h);
        }

        var wrong = handles.Where((h, i) => slab.At(h).Clock != i * 7 || (string)slab.GetRef(h)! != "r" + i || (string)slab.GetKey(h)! != "k" + i);

        Assert.AreEqual(0, wrong.Count());
    }

    [TestMethod]
    public void At_RefKeptAcrossGrowth_ReadsPoisonInDebugAndTheOldValueInRelease()
    {
        var slab = new Slab(1);
        var first = slab.Allocate();
        slab.At(first).Clock = 42;
        ref var kept = ref slab.At(first);

        slab.Allocate();

#if DEBUG
        Assert.AreNotEqual(42L, kept.Clock);
#else
        Assert.AreEqual(42L, kept.Clock);
#endif
        Assert.AreEqual(42L, slab.At(first).Clock);
    }

    [TestMethod]
    public void Allocate_AfterFreeBeforeAllowReuse_TakesAFreshSlot()
    {
        var slab = new Slab();
        var a = slab.Allocate();

        slab.Free(a);
        var b = slab.Allocate();

        Assert.AreNotEqual(a, b);
        AssertSound(slab);
    }

    [TestMethod]
    public void Allocate_AfterAllowReuse_TakesTheFreedSlotLastFirst()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        var b = slab.Allocate();
        slab.Free(a);
        slab.Free(b);

        slab.AllowReuse();

        Assert.IsTrue(new[] { slab.Allocate(), slab.Allocate() }.OrderBy(x => x).SequenceEqual(new[] { a, b }));
        AssertSound(slab);
    }

    [TestMethod]
    public void Free_Slot_KeepsItsFieldsReadableAndMarksItFree()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        slab.At(a).Clock = 9;
        slab.At(a).Left = 3;
        slab.At(a).Info = 5 | (2 << 8);

        slab.Free(a);

        var slot = slab.At(a);
        Assert.AreEqual((9L, 3, true, 5), (slot.Clock, slot.Left, slot.IsFree, slot.Info & 0xFF));
    }

    [TestMethod]
    public void Free_SlotWithObjects_DropsThem()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        slab.SetRef(a, new object());
        slab.SetKey(a, "key");

        slab.Free(a);

        Assert.IsNull(slab.GetRef(a));
        Assert.IsNull(slab.GetKey(a));
        AssertSound(slab);
    }

    [TestMethod]
    public void Free_ObjectReleased_IsCollectable()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        var weak = Hold(slab, a);

        slab.Free(a);
        System.GC.Collect();

        Assert.IsFalse(weak.TryGetTarget(out _));
    }

    private static WeakReference<object> Hold(Slab slab, int handle)
    {
        var o = new object();
        slab.SetRef(handle, o);
        return new WeakReference<object>(o);
    }

    [TestMethod]
    public void Free_TwiceOrWriteAfterFree_Throws()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        slab.Free(a);

        Assert.AreEqual(2, Count(() => slab.Free(a)) + Count(() => slab.SetRef(a, "x")));
    }

    private static int Count(Action act)
    {
        try
        {
            act();
            return 0;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    [TestMethod]
    public void At_HandleNeverAllocatedOrNegative_Throws()
    {
        var slab = new Slab();
        slab.Allocate();

        Assert.AreEqual(3, Thrown(() => slab.At(5)) + Thrown(() => slab.At(-1)) + Thrown(() => slab.At(7 << Slab.PageBits)));
    }

    private static int Thrown(Action act)
    {
        try
        {
            act();
            return 0;
        }
        catch (ArgumentException)
        {
            return 1;
        }
    }

    [TestMethod]
    public void SetRef_Null_AllocatesNoPage()
    {
        var slab = new Slab();
        var a = slab.Allocate();

        slab.SetRef(a, null);
        slab.SetKey(a, null);

        Assert.IsNull(slab.GetRef(a));
        AssertSound(slab);
    }

    [TestMethod]
    public void SetRef_AfterObjectPagesExist_ClearsWithNull()
    {
        var slab = new Slab(1);
        var a = slab.Allocate();
        slab.SetRef(a, "x");
        slab.SetKey(a, "k");

        slab.SetRef(a, null);
        slab.SetKey(a, null);

        Assert.IsNull(slab.GetRef(a) ?? slab.GetKey(a));
    }

    [TestMethod]
    public void AllowReuse_PageOneAllFree_ReleasesItAndReacquiresItsNumber()
    {
        var slab = new Slab(1);
        var handles = Enumerable.Range(0, Slab.PageSlots + 5).Select(_ => slab.Allocate()).ToList();
        foreach (var h in handles.Where(h => Slab.PageOf(h) == 1))
        {
            slab.SetRef(h, "x");
            slab.Free(h);
        }

        Assert.AreEqual(2, slab.PageCount);
        slab.AllowReuse();
        Assert.AreEqual(1, slab.PageCount);
        AssertSound(slab);

        // Page 0 is full of live slots: the next allocation reacquires page number 1.
        var again = slab.Allocate();
        Assert.AreEqual(1, Slab.PageOf(again));
        Assert.AreEqual(2, slab.PageCount);
        Assert.IsNull(slab.GetRef(again));
        AssertSound(slab);
    }

    [TestMethod]
    public void AllowReuse_ReleasedPage_RefusesItsOldHandles()
    {
        var slab = new Slab(1);
        var handles = Enumerable.Range(0, Slab.PageSlots + 1).Select(_ => slab.Allocate()).ToList();
        slab.Free(handles[^1]);
        slab.AllowReuse();

        Assert.AreEqual(1, Thrown(() => slab.At(handles[^1])));
    }

    [TestMethod]
    public void AllowReuse_PageWithLiveSlot_KeepsThePage()
    {
        var slab = new Slab(1);
        var handles = Enumerable.Range(0, Slab.PageSlots + 2).Select(_ => slab.Allocate()).ToList();
        slab.Free(handles[^1]);

        slab.AllowReuse();

        Assert.AreEqual(2, slab.PageCount);
        AssertSound(slab);
    }

    [TestMethod]
    public void AllowReuse_PageZeroAllFree_KeepsIt()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        slab.Free(a);

        slab.AllowReuse();

        Assert.AreEqual((1, 0), (slab.PageCount, slab.LiveCount));
        Assert.AreEqual(a, slab.Allocate());
    }

    [TestMethod]
    public void AllowReuse_NothingFreed_IsANoOp()
    {
        var slab = new Slab();
        slab.Allocate();

        slab.AllowReuse();

        AssertSound(slab);
    }

    [TestMethod]
    public void Allocate_OnlyPageOneHasFreeSlots_FillsItBeforeOpeningAnother()
    {
        var slab = new Slab(1);
        var handles = Enumerable.Range(0, Slab.PageSlots * 2 + 1).Select(_ => slab.Allocate()).ToList();
        var victim = handles[Slab.PageSlots + 3];
        slab.Free(victim);
        slab.AllowReuse();

        var again = slab.Allocate();

        Assert.AreEqual(victim, again);
        Assert.AreEqual(3, slab.PageCount);
    }

    [TestMethod]
    public void Violations_CorruptedFreeList_ReportsIt()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        var b = slab.Allocate();
        slab.Free(a);
        slab.AllowReuse();
        slab.At(a).Info = 0; // a live slot on the free list

        var found = slab.Violations();

        Assert.IsTrue(found.Any(v => v.Contains("live slot")));
        slab.At(a).Info = Slot.FreeKind << 8;
        slab.At(a).Payload = a.Equals(b) ? 0 : a; // a loop
        Assert.IsTrue(slab.Violations().Any(v => v.Contains("twice")));
    }

    [TestMethod]
    public void Violations_FreeSlotOffEveryList_AndBeyondUsed_AndObjects_AreReported()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        var b = slab.Allocate();
        slab.At(b).Info = Slot.FreeKind << 8;
        Assert.IsTrue(slab.Violations().Any(v => v.Contains("on no list")));

        slab.At(b).Info = 0;
        slab.Free(a);
        slab.AllowReuse();
        slab.At(a).Payload = 14;
        Assert.IsTrue(slab.Violations().Any(v => v.Contains("beyond")));

        slab.At(a).Payload = -1;
        slab.SetRef(b, "x");
        Assert.AreEqual(0, slab.Violations().Count);
    }

    [TestMethod]
    public void Violations_WrongCounts_AreReported()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        var b = slab.Allocate();
        slab.Free(a);
        slab.Free(b);
        slab.AllowReuse();
        slab.At(b).Payload = -1; // cuts the chain: one slot is lost

        var found = slab.Violations();

        Assert.IsTrue(found.Any(v => v.Contains("counted")));
    }

    [TestMethod]
    public void Violations_FreeSlotHoldingAnObject_IsReported()
    {
        var slab = new Slab();
        var a = slab.Allocate();
        slab.Free(a);
        slab.AllowReuse();
        slab.At(a).Info = 0;
        slab.SetRef(a, "x");
        slab.At(a).Info = Slot.FreeKind << 8;

        Assert.IsTrue(slab.Violations().Any(v => v.Contains("holds an object")));
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(1, 2)]
    [DataRow(1, 3)]
    [DataRow(16, 4)]
    [DataRow(16, 5)]
    public void Slab_RandomAllocateFreeAndAllowReuse_MatchesAModel(int firstSlots, int seed)
    {
        var random = new Random(seed);
        var slab = new Slab(firstSlots);
        var live = new Dictionary<int, long>();
        var freedSinceReuse = new HashSet<int>();
        var stamp = 0L;
        for (var step = 0; step < 6000; step++)
        {
            var pick = random.Next(10);
            if (pick < 5 || live.Count == 0)
            {
                var h = slab.Allocate();
                Assert.IsFalse(live.ContainsKey(h));
                Assert.IsFalse(freedSinceReuse.Contains(h), "a slot freed in this batch was reused");
                slab.At(h).Clock = ++stamp;
                if (random.Next(2) == 0)
                {
                    slab.SetRef(h, stamp);
                }

                live[h] = stamp;
            }
            else if (pick < 9)
            {
                var h = live.Keys.ElementAt(random.Next(live.Count));
                slab.Free(h);
                live.Remove(h);
                freedSinceReuse.Add(h);
            }
            else
            {
                slab.AllowReuse();
                freedSinceReuse.Clear();
            }

            if (step % 97 == 0)
            {
                AssertSound(slab);
            }
        }

        Assert.AreEqual(live.Count, slab.LiveCount);
        Assert.AreEqual(0, live.Count(kv => slab.At(kv.Key).Clock != kv.Value));
        AssertSound(slab);
        foreach (var h in live.Keys.ToList())
        {
            slab.Free(h);
        }

        slab.AllowReuse();
        Assert.AreEqual((0, 1), (slab.LiveCount, slab.PageCount));
        AssertSound(slab);
    }
}
