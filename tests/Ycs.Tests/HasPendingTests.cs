using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class HasPendingTests
{
    [TestMethod]
    public void NewDocument_HasNothingPending()
    {
        Assert.IsFalse(new YDoc().HasPending);
    }

    [TestMethod]
    public void StructWithoutItsPredecessor_IsPendingUntilThePredecessorArrives()
    {
        var updates = Updates(text =>
        {
            text.Insert(0, "a");
            text.Insert(1, "b");
        });
        var doc = new YDoc();

        doc.ApplyUpdate(updates[1]);
        var pending = doc.HasPending;
        doc.ApplyUpdate(updates[0]);

        Assert.IsTrue(pending);
        Assert.IsFalse(doc.HasPending);
        Assert.AreEqual("ab", doc.GetText("text").ToString());
    }

    [TestMethod]
    public void DeletionOfAStructNotReceived_IsPendingUntilTheStructArrives()
    {
        var updates = Updates(text =>
        {
            text.Insert(0, "ab");
            text.Delete(0, 1);
        });
        var doc = new YDoc();

        doc.ApplyUpdate(updates[1]);
        var pending = doc.HasPending;
        doc.ApplyUpdate(updates[0]);

        Assert.IsTrue(pending);
        Assert.IsFalse(doc.HasPending);
        Assert.AreEqual("b", doc.GetText("text").ToString());
    }

    [TestMethod]
    public void IndependentUpdate_IntegratesWhileAnotherIsPending()
    {
        var first = Updates(text =>
        {
            text.Insert(0, "a");
            text.Insert(1, "b");
        });
        var other = Updates(text => text.Insert(0, "x"));
        var doc = new YDoc();

        doc.ApplyUpdate(first[1]);
        doc.ApplyUpdate(other[0]);

        Assert.IsTrue(doc.HasPending);
        Assert.AreEqual("x", doc.GetText("text").ToString());
    }

    [TestMethod]
    public void RejectedUpdate_LeavesNothingPending()
    {
        var doc = new YDoc();

        Assert.IsFalse(doc.TryApplyUpdate(Convert.FromBase64String("AQEB/////wuEAQoBcgA=")));
        Assert.IsFalse(doc.HasPending);
    }

    // Each call on the text runs in its own transaction: one v1 update each.
    private static List<byte[]> Updates(Action<YText> edit)
    {
        var doc = new YDoc();
        var updates = new List<byte[]>();
        doc.Update += (_, e) => updates.Add(e.data);
        edit(doc.GetText("text"));
        return updates;
    }
}
