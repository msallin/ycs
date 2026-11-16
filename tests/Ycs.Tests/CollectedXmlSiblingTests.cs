using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A user can hold an XML node after it was collected: its struct has merged away or become a GC struct, and the slot
/// is reused by another struct (docs/design-item-layout.md, 2.4). NextSibling, PrevSibling and InsertAfter answer from
/// the store as it is, through the node's id, never from a handle.
/// </summary>
[TestClass]
public class CollectedXmlSiblingTests
{
    // Reuse happens at the end of the batch that freed the slots; this inserts and removes until the slot of the
    // collected node names another struct.
    private static void ChurnUntilReused(YDoc doc, YXmlFragment fragment, int handle, object? old)
    {
        for (var i = 0; i < 64 && ReferenceEquals(doc.Store.Slab.ReadOwner(handle), old); i++)
        {
            fragment.Add(new YXmlElement("churn"));
        }

        Assert.AreNotSame(old, doc.Store.Slab.ReadOwner(handle), "the slot was not reused");
    }

    // [left, held, right]: left and held are deleted and merge into one deleted item, so held's struct is merged away.
    private static (YDoc Doc, YXmlFragment Fragment, YXmlElement Held, YXmlElement Right, int Handle, object Old) MergedAway()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var fragment = doc.GetXmlFragment("f");
        var left = new YXmlElement("left");
        var held = new YXmlElement("held");
        var right = new YXmlElement("right");
        fragment.Add(left, held, right);
        var handle = held._item.Handle;
        var old = held._item.ToStruct();
        fragment.RemoveRange(0, 2);
        Assert.IsTrue(held.IsCollected);
        Assert.IsTrue(held._item.IsDetached, "the struct of the held node did not merge away");
        return (doc, fragment, held, right, handle, old);
    }

    [TestMethod]
    public void NextSibling_OfACollectedNodeWhoseSlotWasReused_IsTheNodeRightOfIt()
    {
        var (doc, fragment, held, right, handle, old) = MergedAway();
        ChurnUntilReused(doc, fragment, handle, old);

        Assert.AreSame(right, held.NextSibling);
    }

    [TestMethod]
    public void PrevSibling_OfACollectedNodeWhoseSlotWasReused_IsNone()
    {
        var (doc, fragment, held, _, handle, old) = MergedAway();
        ChurnUntilReused(doc, fragment, handle, old);

        Assert.IsNull(held.PrevSibling);
    }

    [TestMethod]
    public void InsertAfter_OfACollectedNodeWhoseSlotWasReused_InsertsWhereTheNodeWas()
    {
        var (doc, fragment, held, right, handle, old) = MergedAway();
        ChurnUntilReused(doc, fragment, handle, old);
        var inserted = new YXmlElement("inserted");

        fragment.InsertAfter(held, inserted);

        Assert.AreSame(inserted, fragment[0]);
        Assert.AreSame(right, inserted.NextSibling);
    }

    // [outer[held, tail]]: outer is deleted, held's struct becomes a GC struct and merges with tail's.
    private static (YDoc Doc, YXmlFragment Fragment, YXmlElement Held, int Handle, object Old) Nested()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var fragment = doc.GetXmlFragment("f");
        var outer = new YXmlElement("outer");
        var held = new YXmlElement("held");
        var tail = new YXmlElement("tail");
        fragment.Add(outer);
        outer.Add(held, tail);
        var handle = held._item.Handle;
        var old = held._item.ToStruct();
        fragment.RemoveRange(0, 1);
        Assert.IsTrue(held._item.IsDetached, "the struct of the held node was not replaced");
        return (doc, fragment, held, handle, old);
    }

    [TestMethod]
    public void Siblings_OfANestedCollectedNodeWhoseSlotWasReused_AreNone()
    {
        var (doc, fragment, held, handle, old) = Nested();
        ChurnUntilReused(doc, fragment, handle, old);

        Assert.AreEqual((null, null), (held.NextSibling, held.PrevSibling));
    }

    [TestMethod]
    public void InsertAfter_OfANestedCollectedNodeWhoseSlotWasReused_ThrowsAndInsertsNothing()
    {
        var (doc, fragment, held, handle, old) = Nested();
        ChurnUntilReused(doc, fragment, handle, old);
        var before = fragment.Count;

        Assert.ThrowsExactly<ArgumentException>(() => fragment.InsertAfter(held, new YXmlElement("x")));

        Assert.AreEqual(before, fragment.Count);
    }
}
