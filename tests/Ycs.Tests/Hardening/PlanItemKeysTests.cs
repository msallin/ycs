using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The integration plan's garbage collection check where its keys matter: a peer's item lands next to an item the local
/// document inserted into a type it holds after collecting it (the plan meets a collected parent), and a resend that
/// overlaps the store has the id of the store's item. Both apply to the bytes Yjs 13.6.32 produces.
/// </summary>
[TestClass]
public class PlanItemKeysTests
{
    [TestCleanup]
    public void ClearPoison() => AbstractType.PoisonCollectedHandleForTests = Environment.GetEnvironmentVariable("YCS_POISON") == "1";

    // client 9: root map "r9", key "s", a string value of two items' length ("ab"); then a range that deletes its
    // second half, which splits the value: the store's MaySplitMapValues is set, so the plan runs every pre-check.
    private static byte[] SplitValueSetup()
    {
        var w = new Lib0Writer();
        w.WriteVarUint(1);
        w.WriteVarUint(1);
        w.WriteVarUint(9);
        w.WriteVarUint(0);
        w.WriteByte(4 | 0x20); // string, no origins, with a key
        w.WriteVarUint(1);
        w.WriteVarString("r9");
        w.WriteVarString("s");
        w.WriteVarString("ab");
        w.WriteVarUint(1);
        w.WriteVarUint(9);
        w.WriteVarUint(1);
        w.WriteVarUint(1);
        w.WriteVarUint(1);
        return w.ToArray();
    }

    // client 2: X (2,0) "yz" right of L, Y (2,2) "w" right of X's first half (splitting X); the range deletes m's item.
    private static byte[] PeerUpdate(ItemId l, ItemId mItem)
    {
        var w = new Lib0Writer();
        w.WriteVarUint(1);
        w.WriteVarUint(2);
        w.WriteVarUint(2);
        w.WriteVarUint(0);
        w.WriteByte(4 | 0x80);
        w.WriteVarUint((ulong)l.Client);
        w.WriteVarUint((ulong)l.Clock);
        w.WriteVarString("yz");
        w.WriteByte(4 | 0x80);
        w.WriteVarUint(2);
        w.WriteVarUint(0);
        w.WriteVarString("w");
        w.WriteVarUint(1);
        w.WriteVarUint((ulong)mItem.Client);
        w.WriteVarUint(1);
        w.WriteVarUint((ulong)mItem.Clock);
        w.WriteVarUint(1);
        return w.ToArray();
    }

    private static (YDoc Doc, byte[] Update) Scenario()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, Gc = true });
        doc.ApplyUpdate(SplitValueSetup());
        var r = doc.GetMap("r");
        var m = new YMap();
        r.Set("m", m);
        var t = new YMap();
        m.Set("t", t);
        m.Delete("t");
        Assert.IsTrue(t.IsCollected, "t is collected");
        t.Set("k", "x");
        var l = new ItemId(1, doc.Store.GetState(1) - 1);
        return (doc, PeerUpdate(l, m.ItemId));
    }

    private const string CollectedParentYjs = "AwIJACQBAnI5AXMBYaEJAAEDAgChAQIBoQIAAaECAAEDAQAhAQFyAW0BAAEhAAEBAWsBAwkBAQECAQADAQEAAw==";

    [TestMethod]
    public void ApplyUpdate_PeerItemNextToItemInCollectedType_AppliesAsYjs()
    {
        var (doc, update) = Scenario();

        doc.ApplyUpdate(update);

        Assert.AreEqual((false, CollectedParentYjs), (doc.HasPending, Convert.ToBase64String(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void ApplyUpdate_PeerItemNextToItemInCollectedType_AppliesWithPoisonedHandles()
    {
        AbstractType.PoisonCollectedHandleForTests = true;
        var (doc, update) = Scenario();

        doc.ApplyUpdate(update);

        Assert.AreEqual((false, CollectedParentYjs), (doc.HasPending, Convert.ToBase64String(doc.EncodeStateAsUpdate())));
    }

    // Store S (2,0) "ab" under map N key "k", an empty map P; a resend D (2,0) "abcd" with the same parent and key; ranges
    // cut S at 1 and D's tail at 3 and delete P. Yjs 13.6.32 applies it to exactly these bytes.
    private const string U1 = "AgIDACcBAXIBbgEnAQFyAXABAQIAJAADAAFrAmFiAA==";
    private const string U2 = "AQECACQAAwABawRhYmNkAgICAAECAQMBAQE=";
    private const string Yjs = "AgIDACcBAXIBbgEhAQFyAXABAgIAIQADAAFrA6QCAgFkAgMBAQECAQAD";

    [TestMethod]
    public void ApplyUpdate_StoreItemAndOverlappingResendBothCut_AppliesAsYjs()
    {
        var doc = new YDoc(new YDocOptions { Gc = true });
        doc.ApplyUpdate(Convert.FromBase64String(U1));

        doc.ApplyUpdate(Convert.FromBase64String(U2));

        Assert.AreEqual(Yjs, Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    // Store S (2,0) "ab" under map N (3,0) key "k"; a resend D (2,0) "abcd" (same parent and key, no origins) whose tail
    // a range (2,3,1) splits, then N deleted. Integrated from the store's state on, D's tail follows S, which leaves two
    // live parts of the key's value in a deleted map: Yjs 13.6.32 throws "Unexpected case" in the cleanup.
    private const string OverlapStore = "AgEDACcBAXIBbgEBAgAkAAMAAWsCYWIA";
    private const string OverlapResend = "AQECACQAAwABawRhYmNkAgIBAwEDAQAB";

    [TestMethod]
    public void ApplyUpdate_OverlappingResendSplitInADeletedMap_RejectsAndLeavesTheDocumentUnchanged()
    {
        var doc = new YDoc(new YDocOptions { Gc = true });
        doc.ApplyUpdate(Convert.FromBase64String(OverlapStore));
        var before = Convert.ToBase64String(doc.EncodeStateAsUpdate());

        Assert.ThrowsExactly<YUpdateException>(() => doc.ApplyUpdate(Convert.FromBase64String(OverlapResend)));

        Assert.AreEqual(before, Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }
}
