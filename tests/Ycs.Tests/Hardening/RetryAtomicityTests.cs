using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// An update that lets pending structs integrate retries them in its transaction, after it was carried out; what
/// the retry's plan rejects (nesting, garbage collection) is dropped and the update stands, and the conflict budget
/// running out in the retry keeps what was integrated. Either way the document stays a complete Yjs document: its
/// state applies to a fresh document, which has the same content and state vector.
/// </summary>
[TestClass]
public class RetryAtomicityTests
{
    private static readonly UpdateLimits Shallow = new() { MaxDepth = 3 };

    [TestMethod]
    public void Retry_PendingTypesNestedTooDeep_AreDroppedAndTheUpdateStands()
    {
        var (trigger, deep) = DeepAfterTrigger();
        var doc = new YDoc(new YDocOptions { ClientId = 9, Limits = Shallow });
        doc.ApplyUpdateV1(deep);
        var pendingBefore = doc.HasPending;

        doc.ApplyUpdateV1(trigger);

        Assert.IsTrue(pendingBefore);
        Assert.AreEqual("[\"x\"]", Json(doc.GetArray("a")));
        Assert.IsFalse(doc.HasPending);
        AssertComplete(doc);
    }

    [TestMethod]
    public void Retry_PendingTypesNestedTooDeep_WithStructsOfTheUpdate_IsAppliedInPart()
    {
        var (trigger, deep) = DeepAfterTrigger();
        var doc = new YDoc(new YDocOptions { ClientId = 9, Limits = Shallow });
        doc.ApplyUpdateV1(deep);

        // The trigger and a struct of client 3 that waits for the deep structs: it joins the retry.
        var withOwnRest = UpdateOperations.MergeUpdates([trigger, IntoTheDeepArray()]);
        var e = Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdateV1(withOwnRest));

        Assert.IsTrue(e.PartlyApplied);
        Assert.AreEqual("[\"x\"]", Json(doc.GetArray("a")));
        Assert.IsFalse(doc.HasPending);
        AssertComplete(doc);
    }

    [TestMethod]
    public void Retry_Dropped_TheSameStructsAgainAreRejectedAndTheDocumentStaysUsable()
    {
        var (trigger, deep) = DeepAfterTrigger();
        var doc = new YDoc(new YDocOptions { ClientId = 9, Limits = Shallow });
        doc.ApplyUpdateV1(deep);
        doc.ApplyUpdateV1(trigger);
        var state = doc.EncodeStateAsUpdateV1();

        // Now nothing waits: the same structs are planned with the update and rejected before anything changes.
        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdateV1(deep));
        var unchanged = Convert.ToHexString(state) == Convert.ToHexString(doc.EncodeStateAsUpdateV1());
        doc.GetArray("a").Insert(1, ["y"]);

        Assert.IsTrue(unchanged);
        Assert.AreEqual("[\"x\",\"y\"]", Json(doc.GetArray("a")));
        AssertComplete(doc);
    }

    [TestMethod]
    public void Retry_PendingValueSplitInACollectedType_IsDroppedAndTheUpdateStands()
    {
        // Client 5: "x" (5:0), then a map N (5:1) after it; client 6: one value of two elements under N's key "k" (6:0,
        // which Yjs never writes); the pending update deletes N and the value's second element. With N, its key holds
        // two live parts, and collecting N would fail (Yjs: "Unexpected case" from Item.gc). The garbage collection
        // check of the retry rejects it, after "x" was integrated.
        var doc = new YDoc(new YDocOptions { ClientId = 9 });
        doc.ApplyUpdateV1(SplitValueInDeletedMap());
        var pendingBefore = doc.HasPending;

        doc.ApplyUpdateV1(XOfClient5());

        Assert.IsTrue(pendingBefore);
        Assert.AreEqual("[\"x\"]", Json(doc.GetArray("a")));
        // The structs are dropped; their deletions keep waiting (for structs that may be sent again).
        Assert.IsNull(doc.Store.Pending);
        AssertComplete(doc);
    }

    [TestMethod]
    public void Retry_PendingValueSplitInACollectedType_IntegratesWithoutGarbageCollection()
    {
        // Without garbage collection nothing is collected, so the same structs integrate (as in Yjs, unseen).
        var doc = new YDoc(new YDocOptions { ClientId = 9, Gc = false });
        doc.ApplyUpdateV1(SplitValueInDeletedMap());

        doc.ApplyUpdateV1(XOfClient5());

        Assert.AreEqual("[\"x\"]", Json(doc.GetArray("a")));
        Assert.IsFalse(doc.HasPending);
        AssertComplete(doc);
    }

    [TestMethod]
    public void Retry_ConflictBudgetRunsOut_KeepsACompletePrefix()
    {
        // 2,000 items of client 9 (clocks 1 to 2,000) wait for clock 0; then they conflict with 2,000 of client 5.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        doc.ApplyUpdateV1(ItemsWithoutOrigins(5, 0, 2_000));
        doc.ApplyUpdateV1(ItemsWithoutOrigins(9, 1, 2_000));
        var limits = new UpdateLimits { MaxIntegrationSteps = 1_000_000 };

        var e = Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdateV1(ItemsWithoutOrigins(9, 0, 1), null, limits));

        Assert.IsTrue(e.PartlyApplied);
        var integrated = doc.Store.GetState(9);
        Assert.IsGreaterThan(1L, integrated);
        Assert.IsLessThan(2_001L, integrated);
        Assert.IsFalse(doc.HasPending);
        AssertComplete(doc);
    }

    /// <summary>Its state applies to a fresh document, with the same content and state vector, and back.</summary>
    private static void AssertComplete(YDoc doc)
    {
        foreach (var v2 in new[] { false, true })
        {
            var fresh = new YDoc(new YDocOptions { ClientId = 2, Gc = doc.Gc, Limits = UpdateLimits.Unlimited });
            if (v2)
            {
                fresh.ApplyUpdateV2(doc.EncodeStateAsUpdateV2());
            }
            else
            {
                fresh.ApplyUpdateV1(doc.EncodeStateAsUpdateV1());
            }

            Assert.AreEqual(Convert.ToHexString(doc.EncodeStateVector()), Convert.ToHexString(fresh.EncodeStateVector()));
            Assert.AreEqual(Json(doc.GetArray("a")), Json(fresh.GetArray("a")));
            Assert.AreEqual(doc.GetText("t").ToString(), fresh.GetText("t").ToString());
            Assert.AreEqual(doc.HasPending, fresh.HasPending);
        }
    }

    private static string Json(YArray array) => YJson.Stringify(array.ToJson());

    /// <summary>
    /// The trigger (client 1's "x" in root array 'a') and an update of client 2 that waits for it: arrays nested four
    /// deep, the first right after "x".
    /// </summary>
    private static (byte[] Trigger, byte[] Deep) DeepAfterTrigger()
    {
        var one = new YDoc(new YDocOptions { ClientId = 1 });
        one.GetArray("a").Insert(0, ["x"]);
        var trigger = one.EncodeStateAsUpdateV1();
        var two = new YDoc(new YDocOptions { ClientId = 2 });
        two.ApplyUpdateV1(trigger);
        var vector = two.EncodeStateVector();
        two.Transact(_ =>
        {
            var array = two.GetArray("a");
            for (var i = 0; i < 4; i++)
            {
                var inner = new YArray();
                array.Insert(array == two.GetArray("a") ? 1 : 0, [inner]);
                array = inner;
            }
        });

        return (trigger, two.EncodeStateAsUpdateV1(vector));
    }

    /// <summary>Client 3's insert into the outermost array of <see cref="DeepAfterTrigger"/>: it waits for client 2.</summary>
    private static byte[] IntoTheDeepArray()
    {
        var (trigger, deep) = DeepAfterTrigger();
        var three = new YDoc(new YDocOptions { ClientId = 3 });
        three.ApplyUpdateV1(trigger);
        three.ApplyUpdateV1(deep);
        var vector = three.EncodeStateVector();
        ((YArray)three.GetArray("a").Get(1)).Insert(0, ["z"]);
        return three.EncodeStateAsUpdateV1(vector);
    }

    private static byte[] XOfClient5()
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1); // one client
        writer.WriteVarUint(1); // one struct
        writer.WriteVarUint(5);
        writer.WriteVarUint(0);
        writer.WriteByte(8); // ContentAny, no origins
        writer.WriteVarUint(1); // the parent is a root
        writer.WriteVarString("a");
        writer.WriteVarUint(1);
        writer.WriteAny("x");
        writer.WriteVarUint(0); // no deletions
        return writer.ToArray();
    }

    private static byte[] SplitValueInDeletedMap()
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(2); // two clients, the higher first
        writer.WriteVarUint(1);
        writer.WriteVarUint(6);
        writer.WriteVarUint(0);
        writer.WriteByte(8 | 0x20); // ContentAny with a parentSub, no origins
        writer.WriteVarUint(0); // the parent is an item
        writer.WriteVarUint(5);
        writer.WriteVarUint(1);
        writer.WriteVarString("k");
        writer.WriteVarUint(2);
        writer.WriteAny("a");
        writer.WriteAny("b");

        writer.WriteVarUint(1);
        writer.WriteVarUint(5);
        writer.WriteVarUint(1);
        writer.WriteByte(7 | 0x80); // ContentType with a left origin
        writer.WriteVarUint(5);
        writer.WriteVarUint(0);
        writer.WriteVarUint(YMap.YMapRefId);

        writer.WriteVarUint(2); // deletions: 6:1 and 5:1
        writer.WriteVarUint(6);
        writer.WriteVarUint(1);
        writer.WriteVarUint(1);
        writer.WriteVarUint(1);
        writer.WriteVarUint(5);
        writer.WriteVarUint(1);
        writer.WriteVarUint(1);
        writer.WriteVarUint(1);
        return writer.ToArray();
    }

    /// <summary>A v1 update of <paramref name="count"/> deleted items of <paramref name="client"/> from <paramref name="clock"/> in root text "t", without origins.</summary>
    private static byte[] ItemsWithoutOrigins(long client, long clock, int count)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1);
        writer.WriteVarUint((uint)count);
        writer.WriteVarUint((ulong)client);
        writer.WriteVarUint((ulong)clock);
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
}
