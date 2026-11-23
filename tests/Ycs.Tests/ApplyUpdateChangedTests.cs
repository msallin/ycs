using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class ApplyUpdateChangedTests
{
    [TestMethod]
    public void ApplyUpdate_NewContent_ReportsChanged()
    {
        var updates = Updates(text => text.Insert(0, "a"));

        Assert.IsTrue(new YDoc().ApplyUpdate(updates[0]));
    }

    [TestMethod]
    public void ApplyUpdate_SameUpdateAgain_ReportsUnchanged()
    {
        var updates = Updates(text => text.Insert(0, "abc"));
        var doc = new YDoc();
        doc.ApplyUpdate(updates[0]);

        Assert.IsFalse(doc.ApplyUpdate(updates[0]));
    }

    [TestMethod]
    public void ApplyUpdate_WholeStateOfAnEqualDocument_ReportsUnchanged()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        source.GetText("t").Insert(0, "abc");
        source.GetText("t").Remove(1, 1);
        source.GetMap("m").Add("k", 1);
        var doc = new YDoc();
        doc.ApplyUpdate(source.EncodeStateAsUpdate());

        Assert.IsFalse(doc.ApplyUpdate(source.EncodeStateAsUpdate(format: UpdateFormat.V2), UpdateFormat.V2));
    }

    [TestMethod]
    public void ApplyUpdate_DeletionOfAKnownItem_ReportsChanged()
    {
        var updates = Updates(text =>
        {
            text.Insert(0, "ab");
            text.Remove(0, 1);
        });
        var doc = new YDoc();
        doc.ApplyUpdate(updates[0]);

        Assert.AreEqual((true, "b"), (doc.ApplyUpdate(updates[1]), doc.GetText("text").ToString()));
    }

    [TestMethod]
    public void ApplyUpdate_DeletionAlreadyApplied_ReportsUnchanged()
    {
        var updates = Updates(text =>
        {
            text.Insert(0, "ab");
            text.Remove(0, 1);
        });
        var doc = new YDoc();
        doc.ApplyUpdate(updates[0]);
        doc.ApplyUpdate(updates[1]);

        Assert.IsFalse(doc.ApplyUpdate(updates[1]));
    }

    [TestMethod]
    public void ApplyUpdate_StructWithoutItsPredecessor_ReportsChangedThoughItWaits()
    {
        var updates = Updates(text =>
        {
            text.Insert(0, "a");
            text.Insert(1, "b");
        });
        var doc = new YDoc();

        Assert.AreEqual((true, true), (doc.ApplyUpdate(updates[1]), doc.HasPending));
    }

    [TestMethod]
    public void ApplyUpdate_DeletionOfAStructNotReceived_ReportsChangedThoughItWaits()
    {
        var updates = Updates(text =>
        {
            text.Insert(0, "ab");
            text.Remove(0, 1);
        });
        var doc = new YDoc();

        Assert.AreEqual((true, true), (doc.ApplyUpdate(updates[1]), doc.HasPending));
    }

    [TestMethod]
    public void ApplyUpdate_PredecessorOfAWaitingStruct_ReportsChanged()
    {
        var updates = Updates(text =>
        {
            text.Insert(0, "a");
            text.Insert(1, "b");
        });
        var doc = new YDoc();
        doc.ApplyUpdate(updates[1]);

        Assert.AreEqual((true, "ab"), (doc.ApplyUpdate(updates[0]), doc.GetText("text").ToString()));
    }

    [TestMethod]
    public void ApplyUpdate_EmptyUpdate_ReportsUnchanged()
    {
        Assert.IsFalse(new YDoc().ApplyUpdate(new YDoc().EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ApplyUpdate_InTheCallersTransaction_ReportsOnlyThisUpdate()
    {
        var updates = Updates(text => text.Insert(0, "a"));
        var doc = new YDoc();
        doc.ApplyUpdate(updates[0]);
        var changed = true;

        doc.Transact(_ =>
        {
            doc.GetText("text").Insert(1, "local");
            changed = doc.ApplyUpdate(updates[0]);
        });

        Assert.IsFalse(changed);
    }

    [TestMethod]
    public void ApplyUpdate_AnObserverAppliesAnotherUpdate_EachCallReportsItsOwn()
    {
        var known = Updates(text => text.Insert(0, "a"));
        var fresh = Updates(text => text.Insert(0, "x"));
        var doc = new YDoc();
        doc.ApplyUpdate(known[0]);
        var inner = false;
        var applied = false;
        doc.Updated += (_, _) =>
        {
            if (!applied)
            {
                applied = true;
                inner = doc.ApplyUpdate(known[0]);
            }
        };

        var outer = doc.ApplyUpdate(fresh[0]);

        Assert.AreEqual((true, false), (outer, inner));
    }

    private static List<byte[]> Updates(Action<YText> edit)
    {
        var doc = new YDoc();
        var updates = new List<byte[]>();
        doc.Updated += (_, e) => updates.Add(e.Update.ToArray());
        edit(doc.GetText("text"));
        return updates;
    }
}
