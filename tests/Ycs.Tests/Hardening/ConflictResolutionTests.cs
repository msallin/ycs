using System;
using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The conflict loop of <see cref="ItemRef.Integrate"/>: n items without origins of one client after
/// n of another in the same text take n^2 steps (Yjs is quadratic too). It must not allocate per
/// step, and <see cref="UpdateLimits.MaxIntegrationSteps"/> bounds it.
/// </summary>
[TestClass]
public class ConflictResolutionTests
{
    /// <summary>A v1 update of <paramref name="count"/> deleted items of <paramref name="client"/> in the root text "t", without origins: 4 bytes each.</summary>
    private static byte[] ItemsWithoutOrigins(long client, int count)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1);
        writer.WriteVarUint((uint)count);
        writer.WriteVarUint((ulong)client);
        writer.WriteVarUint(0);
        for (var i = 0; i < count; i++)
        {
            writer.WriteByte(1); // deleted content, no origins
            writer.WriteVarUint(1); // the parent is a root type
            writer.WriteVarString("t");
            writer.WriteVarUint(1); // length
        }

        writer.WriteVarUint(0);
        return writer.ToArray();
    }

    private static YDoc WithItemsOfClient5(int count)
    {
        var doc = new YDoc();
        doc.ApplyUpdate(ItemsWithoutOrigins(5, count));
        return doc;
    }

    [TestMethod]
    public void ApplyUpdate_ManyConflictingItems_AllocatesLittle()
    {
        // 2,000 items each pass 2,000 others: 4 million steps.
        var doc = WithItemsOfClient5(2_000);
        var update = ItemsWithoutOrigins(9, 2_000);

        var before = System.GC.GetAllocatedBytesForCurrentThread();
        doc.ApplyUpdate(update);
        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.IsLessThan(4L << 20, allocated);
    }

    [TestMethod]
    public void ApplyUpdate_ConflictsBeyondTheBudget_ThrowsPartlyApplied()
    {
        var doc = WithItemsOfClient5(2_000);
        var limits = new UpdateLimits { MaxIntegrationSteps = 1_000_000 };

        var e = Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(ItemsWithoutOrigins(9, 2_000), null, limits: limits));

        Assert.IsTrue(e.PartlyApplied);
    }

    [TestMethod]
    public void ApplyUpdate_ConflictsBeyondTheBudget_LeaveAValidPrefixOfTheUpdate()
    {
        var doc = WithItemsOfClient5(2_000);
        var limits = new UpdateLimits { MaxIntegrationSteps = 1_000_000 };
        var integrated = 0L;
        doc.Updated += (_, _) => integrated = doc.Store.GetState(9);
        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(ItemsWithoutOrigins(9, 2_000), null, limits: limits));

        // A peer that received only that part has the same document, and the document still works.
        var peer = WithItemsOfClient5(2_000);
        peer.ApplyUpdate(ItemsWithoutOrigins(9, (int)integrated));
        doc.GetText("t").Insert(0, "x");
        peer.ApplyUpdate(doc.EncodeStateAsUpdate(peer.EncodeStateVector()));

        Assert.AreEqual(
            (integrated, Convert.ToHexString(peer.EncodeStateAsUpdate())),
            (doc.Store.GetState(9), Convert.ToHexString(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void ApplyUpdate_ConflictsBeyondTheDefaultBudget_AreRejectedQuickly()
    {
        // 10,000 after 10,000: 100 million steps, beyond the default of 2^24.
        var doc = WithItemsOfClient5(10_000);
        var update = ItemsWithoutOrigins(9, 10_000);

        var stopwatch = Stopwatch.StartNew();
        Assert.IsFalse(doc.TryApplyUpdate(update));

        // The walk is 16.7 million steps; 100 million would take far longer. Debug checks every step's slot owner, too slow
        // to bound by the clock on a loaded CI: there only the rejection is asserted.
#if !DEBUG
        Assert.IsLessThan(2_000, stopwatch.ElapsedMilliseconds);
#endif
    }

    [TestMethod]
    public void ApplyUpdate_ConflictsWithinTheBudget_ApplyAsYjs()
    {
        // Each item goes before the earlier ones of its client, and the higher client after the lower (Yjs' order).
        var doc = WithItemsOfClient5(3);
        doc.ApplyUpdate(ItemsWithoutOrigins(9, 3));

        var order = string.Empty;
        for (var item = doc.GetText("t")._start; item.IsItem; item = item.Right)
        {
            order += item.Id.Client + ":" + item.Id.Clock + " ";
        }

        Assert.AreEqual("5:2 5:1 5:0 9:2 9:1 9:0 ", order);
    }

    [TestMethod]
    public void UpdateLimits_DefaultIntegrationSteps_Is2Pow24()
    {
        Assert.AreEqual(1L << 24, UpdateLimits.Default.MaxIntegrationSteps);
    }
}
