using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The slot of an item names its neighbours by handle and its parent by index in the document's type table, and
/// <c>StructStore.CheckForTests</c> compares them with the objects, the structs a transaction touched always.
/// </summary>
[TestClass]
public class StructLinkSlotTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 }) { RunExtensionsDisabledForTests = true };

    private static Slot SlotOf(YDoc doc, long clock) => doc.Store.Slab.At(doc.Store.Find(new ItemId(1, clock)).Handle);

    private static int HandleOf(YDoc doc, long clock) => doc.Store.Find(new ItemId(1, clock)).Handle;

    [TestMethod]
    public void Insert_BeforeTheFirstItem_LinksBothSlots()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "b");

        text.Insert(0, "a");

        Assert.AreEqual((-1, HandleOf(doc, 0), HandleOf(doc, 1), -1), (SlotOf(doc, 1).Left, SlotOf(doc, 1).Right, SlotOf(doc, 0).Left, SlotOf(doc, 0).Right));
    }

    [TestMethod]
    public void Split_InTheMiddleOfAnItem_LinksTheRightPartAndItsNeighbours()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcd");
        text.InsertEmbed(4, YValue.FromObject(new Dictionary<string, object> { ["img"] = "x" }));
        text.Insert(2, "X");

        // abcd is split at 2 (clock 0 and 2), X (clock 5) between them, the embed (clock 4) after the right part.
        var first = HandleOf(doc, 0);
        var inserted = HandleOf(doc, 5);
        var tail = HandleOf(doc, 2);
        var embed = HandleOf(doc, 4);
        Assert.AreEqual((inserted, first, tail, inserted, embed, tail), (SlotOf(doc, 0).Right, SlotOf(doc, 5).Left, SlotOf(doc, 5).Right, SlotOf(doc, 2).Left, SlotOf(doc, 2).Right, SlotOf(doc, 4).Left));
    }

    [TestMethod]
    public void Cleanup_MergingTheRightPart_PointsTheLeftPartAtTheRightRight()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        text.Insert(2, "cd");
        text.Insert(0, "x");
        text.Remove(0, 1);

        // "ab" and "cd" merged into the item at clock 0, whose left neighbour is the deleted "x".
        Assert.AreEqual((HandleOf(doc, 4), -1, HandleOf(doc, 0)), (SlotOf(doc, 0).Left, SlotOf(doc, 0).Right, SlotOf(doc, 4).Right));
    }

    [TestMethod]
    public void Insert_IntoANestedType_NamesTheParentByIndex()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var map = new YMap();
        array.Add(map);
        map["k"] = 1;

        var childParent = SlotOf(doc, 1).Parent;
        var rootParent = SlotOf(doc, 0).Parent;
        Assert.AreEqual((true, true, 2), (ReferenceEquals(doc.Store.Types[childParent], map), ReferenceEquals(doc.Store.Types[rootParent], array), doc.Store.Types.LiveCount));
    }

    [TestMethod]
    public void Collect_ANestedType_ClearsItsEntryAndKeepsTheIndex()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var map = new YMap();
        array.Add(map);
        map["k"] = 1;
        var index = SlotOf(doc, 1).Parent;

        array.RemoveAt(0);

        Assert.AreEqual((null, 2, 1), (doc.Store.Types[index], doc.Store.Types.Count, doc.Store.Types.LiveCount));
    }

    [TestMethod]
    public void Check_ALinkOfAStructAddedInTheTransactionThatDiffers_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "b");

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "a");
            doc.Store.Slab.At(HandleOf(doc, 1)).Right = -1;
        }));
    }

    [TestMethod]
    public void Check_AParentOfAStructAddedInTheTransactionThatDiffers_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "b");

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "a");
            doc.Store.Slab.At(HandleOf(doc, 1)).Parent = -1;
        }));
    }

    [TestMethod]
    public void Check_ALeftLinkInAListOverTheSamplingSizeThatDiffers_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        for (var i = 0; i < 1100; i++)
        {
            text.Insert(0, "a");
        }

        // The check of this transaction does not sample the list (its count is not a multiple of the sampling step), and still compares what it touched.
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "a");
            doc.Store.Slab.At(HandleOf(doc, 1100)).Left = HandleOf(doc, 0);
        }));
    }

    [TestMethod]
    public void Check_ALinkOfAStructTheMergePassTouchedThatDiffers_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        text.Insert(2, "cd");

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(1, "X");
            doc.Store.Slab.At(HandleOf(doc, 0)).Right = HandleOf(doc, 0);
        }));
    }
}
