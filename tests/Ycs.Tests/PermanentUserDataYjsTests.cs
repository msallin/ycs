using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>PermanentUserData with client ids of Yjs peers, entries written by hostile peers, and Yjs' deferred writes.</summary>
[TestClass]
public class PermanentUserDataYjsTests
{
    private static void Sync(YDoc from, YDoc to) => to.ApplyUpdate(from.EncodeStateAsUpdate(to.EncodeStateVector()), "remote");

    [TestMethod]
    public void GetUserByClientId_IdAbove2Pow31_ReturnsTheUser()
    {
        var peer = new YDoc(new YDocOptions { ClientId = 1 });
        new PermanentUserData(peer).SetUserMapping(peer, 3_000_000_000L, "bob");
        var doc = new YDoc(new YDocOptions { ClientId = 2 });
        var pud = new PermanentUserData(doc);

        Sync(peer, doc);

        Assert.AreEqual("bob", pud.GetUserByClientId(3_000_000_000L));
    }

    [TestMethod]
    public void UsersEntryOfAnotherShape_IsIgnoredAndReplacedBySetUserMapping()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var pud = new PermanentUserData(doc);
        pud.SetUserMapping(doc, 1, "alice");
        var peer = new YDoc(new YDocOptions { ClientId = 2 });
        Sync(doc, peer);
        peer.GetMap("users").Set("alice", "oops");
        peer.GetMap("users").Set("eve", new YMap());

        Sync(peer, doc);
        text.Delete(0, 1);
        pud.SetUserMapping(doc, 1, "alice");
        text.Delete(0, 1);

        Assert.AreEqual(("alice", "alice"), (pud.GetUserByClientId(1), pud.GetUserByDeletedId(new ID(1, 1))));
    }

    [TestMethod]
    public void UsersEntryWithNullValues_IsIgnored()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var pud = new PermanentUserData(doc);
        var peer = new YDoc(new YDocOptions { ClientId = 2 });
        var users = peer.GetMap("users");
        var nulls = new YMap();
        users.Set("mallory", null);
        users.Set("trudy", nulls);
        nulls.Set("ids", null);
        nulls.Set("ds", null);

        Sync(peer, doc);
        var second = new PermanentUserData(doc);

        Assert.AreEqual((null, null), (pud.GetUserByClientId(2), second.GetUserByClientId(2)));
    }

    [TestMethod]
    public void LocalDeletion_RecordedAfterTheDeletingTransactionsUpdate()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var pud = new PermanentUserData(doc);
        pud.SetUserMapping(doc, 1, "alice");
        var ds = (YArray)((YMap)doc.GetMap("users").Get("alice")).Get("ds");
        var dsLengthAtUpdates = new List<int>();
        doc.Update += (_, _) => dsLengthAtUpdates.Add(ds.Length);

        text.Delete(0, 1);

        CollectionAssert.AreEqual(new[] { 0, 1 }, dsLengthAtUpdates);
    }
}
