using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The <see cref="YChange.User"/> a <see cref="PermanentUserData"/> mapping gives the ranges of a snapshot delta.</summary>
[TestClass]
public class YChangeUserTests
{
    private static readonly Dictionary<string, long> UserIds = new() { ["alice"] = 100, ["bob"] = 200 };

    private static void Sync(YDoc from, YDoc to) => to.ApplyUpdate(from.EncodeStateAsUpdate(to.EncodeStateVector()), "remote");

    // Alice writes "abc"; Bob removes "b" and inserts "X" at the start; Alice appends "Y". The delta is read from Alice's side.
    private static (YText Text, Snapshot Before, Snapshot After, PermanentUserData Pud) Scenario(bool mapUsers)
    {
        var alice = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var bob = new YDoc(new YDocOptions { ClientId = 2, GarbageCollection = false });
        var pudAlice = new PermanentUserData(alice);
        var pudBob = new PermanentUserData(bob);
        if (mapUsers)
        {
            pudAlice.SetUserMapping(1, "alice");
            pudBob.SetUserMapping(2, "bob");
        }

        var text = alice.GetText("t");
        text.Insert(0, "abc");
        Sync(alice, bob);
        Sync(bob, alice);
        var before = alice.CreateSnapshot();

        var bobText = bob.GetText("t");
        bobText.Remove(1, 1);
        bobText.Insert(0, "X");
        Sync(bob, alice);
        text.Insert(3, "Y");
        return (text, before, alice.CreateSnapshot(), pudAlice);
    }

    private static YChange UserOf(PermanentUserData pud, YChangeKind kind, ItemId id)
    {
        var name = kind == YChangeKind.Added ? pud.GetUserByClientId(id.Client) : pud.GetUserByDeletedId(id);
        return new YChange(kind, name == null ? null : UserIds[name]);
    }

    [TestMethod]
    public void ToDelta_UsersMappedByPermanentUserData_NamesTheUserOfEachAddedAndRemovedRange()
    {
        var (text, before, after, pud) = Scenario(mapUsers: true);

        var delta = text.ToDelta(after, before, (kind, id) => UserOf(pud, kind, id));

        Assert.AreEqual(
            """[{"attributes":{"ychange":{"type":"added","user":200}},"insert":"X"},{"insert":"a"},{"attributes":{"ychange":{"type":"removed","user":200}},"insert":"b"},{"insert":"c"},{"attributes":{"ychange":{"type":"added","user":100}},"insert":"Y"}]""",
            CanonicalJson.Write(delta));
    }

    [TestMethod]
    public void ToDelta_NoUserMapping_LeavesTheUserOutOfTheChange()
    {
        var (text, before, after, pud) = Scenario(mapUsers: false);

        var delta = text.ToDelta(after, before, (kind, id) => UserOf(pud, kind, id));

        Assert.AreEqual(
            """[{"attributes":{"ychange":{"type":"added"}},"insert":"X"},{"insert":"a"},{"attributes":{"ychange":{"type":"removed"}},"insert":"b"},{"insert":"c"},{"attributes":{"ychange":{"type":"added"}},"insert":"Y"}]""",
            CanonicalJson.Write(delta));
    }

    [TestMethod]
    public void User_ChangeBuiltWithoutAUser_IsNull()
    {
        Assert.IsNull(new YChange(YChangeKind.Added, null).User);
    }
}
