using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Ports the shape of Yjs 13.6.32's tests/permanentUserData.tests.js against this port's
/// <see cref="PermanentUserData"/> (see the class remarks there for the deviations this port had to
/// make: a <c>Func&lt;Transaction, bool&gt;</c> filter instead of Yjs' two-arg
/// <c>filter(transaction, deleteSet)</c>, and running synchronously instead of deferring bookkeeping
/// with <c>setTimeout</c>).
/// </summary>
[TestClass]
public class PermanentUserDataTests
{
    [TestMethod]
    public void GetUserByClientIdReturnsTheMappedDescription()
    {
        var doc = new YDoc();
        doc.ClientId = 1;

        var pud = new PermanentUserData(doc);
        pud.SetUserMapping(doc.ClientId, "user1");

        Assert.AreEqual("user1", pud.GetUserByClientId(1));
        Assert.IsNull(pud.GetUserByClientId(9999));
    }

    [TestMethod]
    public void GetUserByDeletedIdAttributesADeletionToItsUser()
    {
        var doc = new YDoc();
        doc.ClientId = 1;

        var pud = new PermanentUserData(doc);
        pud.SetUserMapping(doc.ClientId, "user1");

        var array = doc.GetArray("array");

        // 'SetUserMapping' itself already consumed some of client 1's clocks (creating the "users"
        // entry, its "ids"/"ds" sub-arrays, and the initial clientId push) before this array insert,
        // so 'a'/'b'/'c' do not start at clock 0 - ask the store for the clock they actually start at.
        var clockBeforeInsert = doc.Store.GetState(doc.ClientId);
        array.Insert(0, "a", "b", "c");
        var idOfA = new ItemId(doc.ClientId, clockBeforeInsert);
        var idOfB = new ItemId(doc.ClientId, clockBeforeInsert + 1);

        array.RemoveAt(0); // delete 'a' only

        Assert.AreEqual("user1", pud.GetUserByDeletedId(idOfA));
        Assert.IsNull(pud.GetUserByDeletedId(idOfB));
    }

    [TestMethod]
    public void SetUserMappingIsIdempotentPerCall_MultipleClientIdsCanShareAUser()
    {
        var doc = new YDoc();
        doc.ClientId = 1;

        var pud = new PermanentUserData(doc);
        pud.SetUserMapping(1, "user1");
        pud.SetUserMapping(2, "user1");

        Assert.AreEqual("user1", pud.GetUserByClientId(1));
        Assert.AreEqual("user1", pud.GetUserByClientId(2));
    }

    [TestMethod]
    public void FilterCanSuppressRecordingADeletion()
    {
        var doc = new YDoc();
        doc.ClientId = 1;

        var pud = new PermanentUserData(doc);
        pud.SetUserMapping(doc.ClientId, "user1", filter: _ => false);

        var array = doc.GetArray("array");
        var clockBeforeInsert = doc.Store.GetState(doc.ClientId);
        array.Insert(0, "a");
        var idOfA = new ItemId(doc.ClientId, clockBeforeInsert);

        array.RemoveAt(0);

        Assert.IsNull(pud.GetUserByDeletedId(idOfA));
    }

    [TestMethod]
    public void UserMappingAndDeletionsPropagateAcrossASync()
    {
        var doc1 = new YDoc();
        doc1.ClientId = 1;
        var doc2 = new YDoc();
        doc2.ClientId = 2;

        var pud1 = new PermanentUserData(doc1);
        var pud2 = new PermanentUserData(doc2);
        pud1.SetUserMapping(doc1.ClientId, "user1");
        pud2.SetUserMapping(doc2.ClientId, "user2");

        var clockBeforeInsert = doc1.Store.GetState(doc1.ClientId);
        doc1.GetArray("array").Insert(0, "a", "b");
        var idOfA = new ItemId(doc1.ClientId, clockBeforeInsert);
        doc1.GetArray("array").RemoveAt(0); // 'a' deleted by user1

        // Sync both ways: each doc's PermanentUserData should learn about the other user, and doc2
        // should learn that 'a' was deleted by user1 even though it never performed the delete.
        var update1 = doc1.EncodeStateAsUpdate(format: UpdateFormat.V2);
        var update2 = doc2.EncodeStateAsUpdate(format: UpdateFormat.V2);
        doc2.ApplyUpdate(update1, format: UpdateFormat.V2);
        doc1.ApplyUpdate(update2, format: UpdateFormat.V2);

        Assert.AreEqual("user1", pud2.GetUserByClientId(1));
        Assert.AreEqual("user2", pud1.GetUserByClientId(2));
        Assert.AreEqual("user1", pud2.GetUserByDeletedId(idOfA));
    }

    [TestMethod]
    public void ConstructorAdoptsAnAlreadyPopulatedUsersMap()
    {
        // A PermanentUserData created *after* the mapping already exists in the doc (e.g. on a peer
        // that just loaded the document) must pick up the existing data too - Yjs parity: the
        // constructor's initial 'storeType.forEach(initUser)' pass.
        var doc1 = new YDoc();
        doc1.ClientId = 1;
        var pud1 = new PermanentUserData(doc1);
        pud1.SetUserMapping(doc1.ClientId, "user1");

        var doc2 = new YDoc();
        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate(format: UpdateFormat.V2), format: UpdateFormat.V2);
        var pud2 = new PermanentUserData(doc2); // constructed after the sync

        Assert.AreEqual("user1", pud2.GetUserByClientId(1));
    }

    [TestMethod]
    public void CustomStoreTypeIsHonored()
    {
        var doc = new YDoc();
        doc.ClientId = 1;
        var customUsers = doc.GetMap("customUsers");

        var pud = new PermanentUserData(doc, customUsers);
        pud.SetUserMapping(doc.ClientId, "user1");

        Assert.AreSame(customUsers, pud.Users);
        Assert.AreEqual("user1", pud.GetUserByClientId(1));
        Assert.IsTrue(customUsers.ContainsKey("user1"));
    }
}
