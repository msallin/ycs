using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The struct store keeps its integration plan for the next update; a plan that was rejected while
/// planning must not leave anything behind.
/// </summary>
[TestClass]
public class IntegrationPlanReuseTests
{
    [TestMethod]
    public void ApplyUpdate_AfterARejectedPlan_IntegratesTheNextUpdate()
    {
        var doc = new YDoc();
        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdateV1(NestedArrays(UpdateLimits.Default.MaxDepth + 1)));

        doc.ApplyUpdateV1(NestedArrays(3));

        Assert.AreEqual(1, doc.GetArray("a").Length);
        Assert.AreEqual(3L, doc.Store.GetState(5));
        Assert.IsFalse(doc.HasPending);
    }

    [TestMethod]
    public void ApplyUpdate_MissingThenArriving_IntegratesBothWithOnePlan()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var text = source.GetText("text");
        text.Insert(0, "ab");
        var first = source.EncodeStateAsUpdateV1();
        var vector = source.EncodeStateVector();
        text.Insert(2, "cd");
        var second = source.EncodeStateAsUpdateV1(vector);
        var doc = new YDoc();

        doc.ApplyUpdateV1(second);
        var pendingAfterSecond = doc.HasPending;
        doc.ApplyUpdateV1(first);

        Assert.IsTrue(pendingAfterSecond);
        Assert.IsFalse(doc.HasPending);
        Assert.AreEqual("abcd", doc.GetText("text").ToString());
    }

    [TestMethod]
    public void ApplyUpdate_LateUpdateLetsPendingStructsIntegrate_OneTransactionWithAllOfThem()
    {
        // As Yjs' readUpdateV2: the pending structs are read again inside the late update's transaction.
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var text = source.GetText("text");
        text.Insert(0, "ab");
        var first = source.EncodeStateAsUpdateV1();
        var vector = source.EncodeStateVector();
        text.Insert(2, "cd");
        var second = source.EncodeStateAsUpdateV1(vector);
        var doc = new YDoc();
        doc.ApplyUpdateV1(second);
        var events = 0;
        var transactions = 0;
        doc.GetText("text").Observe(_ => events++);
        doc.AfterTransaction += (_, _) => transactions++;

        doc.ApplyUpdateV1(first);

        Assert.AreEqual((1, 1, "abcd"), (transactions, events, doc.GetText("text").ToString()));
    }

    [TestMethod]
    public void ApplyUpdate_UpdateThatChangesNothing_RunsATransaction()
    {
        // As Yjs' applyUpdate, which always transacts: three no-op updates raise three afterTransaction events.
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetText("text").Insert(0, "x");
        var update = source.EncodeStateAsUpdateV1();
        var doc = new YDoc();
        doc.ApplyUpdateV1(update);
        var transactions = 0;
        doc.AfterTransaction += (_, _) => transactions++;

        doc.ApplyUpdateV1(update);
        doc.ApplyUpdateV1([0, 0]);

        Assert.AreEqual(2, transactions);
    }

    [TestMethod]
    public void ApplyUpdate_DeletionsOfTheHalvesOfASurrogatePairBeforeTheInsert_KeepThePair()
    {
        // As Yjs, which merges the waiting ranges [1, 1] and [0, 1] into [0, 2] when the second arrives: the pair is
        // deleted as one item, not split (a split would replace both halves with U+FFFD).
        var source = new YDoc(new YDocOptions { ClientId = 1, Gc = false });
        var text = source.GetText("t");
        var updates = new System.Collections.Generic.List<byte[]>();
        source.Updated += (_, e) => updates.Add(e.Update.ToArray());
        text.Insert(0, "\ud83d\udc4d");
        text.ApplyDelta([new Delta { Delete = 1 }]);
        text.ApplyDelta([new Delta { Delete = 1 }]);
        var doc = new YDoc(new YDocOptions { Gc = false });

        doc.ApplyUpdateV1(updates[2]);
        doc.ApplyUpdateV1(updates[1]);
        doc.ApplyUpdateV1(updates[0]);

        // Yjs 13.6.32's receiver (the sender, which split the pair itself, writes "AQEBAAQBAXQG77+977+9AQEBAAI=").
        Assert.AreEqual("AQEBAAQBAXQE8J+RjQEBAQAC", Convert.ToBase64String(doc.EncodeStateAsUpdateV1()));
    }

    [TestMethod]
    public void ApplyUpdate_GcUpToNearTheClockLimitThenWaitingDeletions_EncodesAsYjs()
    {
        // The property engine's huge-clock-gc-encoding: a GC struct up to clock 2^31 - 65, items, a Skip, pending
        // structs and waiting deletions. Expected: Yjs 13.6.32's encodeStateAsUpdate after the same three updates.
        var doc = new YDoc(new YDocOptions { Gc = false, ClientId = 2 });

        doc.ApplyUpdateV1(Convert.FromBase64String("AQL4+v//BwAAv////wfHrAIFrAIFAgILAQMDrAIBAwI="));
        doc.ApplyUpdateV1(Convert.FromBase64String("AQMLAIWsAgYReyJpbWFnZSI6ImkucG5nIn1ErAIGAmJjAQEBYQECrAIBAggLAQAC"));
        doc.ApplyUpdateV1(Convert.FromBase64String("AQSsAgAIAQFhA30BdwFzfoisAgEDdQJ9AXcBeH0BdQJ9AXcBeAQBAXQD5LitBwEBeAYA"));

        Assert.AreEqual(
            "AwL4+v//BwAAv////wfHrAIFrAIFAgasAgAIAQFhAn0BdwFziKwCAQF+iKwCAQJ1An0BdwF4fQGIrAIEAXUCfQF3AXgEAQF0A+S4rQcBAXgGBAsAhawCBhF7ImltYWdlIjoiaS5wbmcifUSsAgYBYsQLAawCBgFjAQEBYQED+Pr//wcBAL////8HrAIBAggLAgACAwM=",
            Convert.ToBase64String(doc.EncodeStateAsUpdateV1()));
    }

    [TestMethod]
    public void EncodeStateAsUpdateV2_ClocksAbove2To30_RoundTrips()
    {
        // Clocks up to 2^31 - 1 are valid (docs/api.md); the v2 clock columns write diff * 2 + flag, as lib0, without a
        // 30-bit limit (a Debug.Assert of one failed here in Debug builds).
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.ApplyUpdateV1(Convert.FromBase64String("AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA="));
        var copy = new YDoc(new YDocOptions { Gc = false });

        copy.ApplyUpdateV2(doc.EncodeStateAsUpdateV2());

        Assert.AreEqual(Convert.ToBase64String(doc.EncodeStateAsUpdateV1()), Convert.ToBase64String(copy.EncodeStateAsUpdateV1()));
    }

    /// <summary>A v1 update of client 5: <paramref name="depth"/> arrays, each nested in the one before, the first in root 'a'.</summary>
    private static byte[] NestedArrays(int depth)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint(1);
        writer.WriteVarUint((ulong)depth);
        writer.WriteVarUint(5);
        writer.WriteVarUint(0);
        for (var i = 0; i < depth; i++)
        {
            writer.WriteByte(7); // ContentType, no origins
            if (i == 0)
            {
                writer.WriteVarUint(1);
                writer.WriteVarString("a");
            }
            else
            {
                writer.WriteVarUint(0);
                writer.WriteVarUint(5);
                writer.WriteVarUint((ulong)(i - 1));
            }

            writer.WriteVarUint(YArray.YArrayRefId);
        }

        writer.WriteVarUint(0);
        return writer.ToArray();
    }
}
