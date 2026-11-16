using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// What a view reads (docs/design-item-layout.md, 3.2): the struct that holds its id now, from the id on; after a merge
/// the struct that took the id in, after a collection a GC struct; a detached view reads its decoded object.
/// </summary>
[TestClass]
public class ItemViewTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 });

    /// <summary>
    /// Appends the parts to a text one transaction each, observed so that every append is an item of its own until the cleanup
    /// merges it; the views of those items, made in the observer, before the merge.
    /// </summary>
    private static List<ItemView> AppendObserved(YDoc doc, params string[] parts)
    {
        var text = doc.GetText("t");
        var views = new List<ItemView>();
        var clock = 0;
        using var subscription = text.Observe(_ => views.Add(ItemView.Of(doc, doc.Store.Find(new ItemId(1, clock)).ToStruct())));
        foreach (var part in parts)
        {
            text.Insert(text.Length, part);
            clock += part.Length;
        }

        return views;
    }

    /// <summary>A map of three keys in an array, and the views of the three items of its keys (clocks 1 to 3).</summary>
    private static (YArray Array, ItemView[] Views) MapWithThreeValues(YDoc doc)
    {
        var map = new YMap();
        var array = doc.GetArray("a");
        array.Push(new object[] { map });
        map.Set("a", "x");
        map.Set("b", "y");
        map.Set("c", "z");
        return (array, new[] { "a", "b", "c" }.Select(key => ItemView.Of(doc, map._map[key].ToItem())).ToArray());
    }

    private static string Text(IReadOnlyList<object> content) => string.Concat(content.Select(c => (string)c));

    [TestMethod]
    public void Read_ItemsMergedIntoOne_EachReadsTheMergedStructFromItsId()
    {
        var doc = NewDoc();

        var views = AppendObserved(doc, "a", "b", "c");

        Assert.AreEqual("abc, bc, c", string.Join(", ", views.Select(v => Text(v.Content))));
    }

    [TestMethod]
    public void Read_ItemMergedIntoItsLeft_ReadsOriginsAndNeighboursAtTheOffset()
    {
        var doc = NewDoc();

        var b = AppendObserved(doc, "a", "b", "c")[1];

        Assert.AreEqual(
            (new ItemId(1, 0), null, null, new ItemId(1, 0), new ItemId(1, 2), 2),
            (b.LeftOrigin, b.RightOrigin, b.Right, b.Left?.Id, b.LastId, b.Length));
    }

    [TestMethod]
    public void Content_AtAnOffset_IsASliceAndLeavesTheStructsContentWhole()
    {
        var doc = NewDoc();
        var b = AppendObserved(doc, "a", "b", "c")[1];

        var slice = Text(b.Content);

        Assert.AreEqual(("bc", "abc"), (slice, doc.GetText("t").ToString()));
    }

    [TestMethod]
    public void Read_ItemSplitAfterTheViewWasMade_ReadsTheLeftPartAndAViewOfTheRightPartTheRight()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var whole = ItemView.Of(doc, text.StartForTests);
        var right = ItemView.At(doc, new ItemId(1, 2));

        text.Insert(1, "X");

        Assert.AreEqual(("a", "c", new ItemId(1, 2)), (Text(whole.Content), Text(right.Content), right.Id));
    }

    [TestMethod]
    public void Read_ItemReplacedByAGcStruct_IsAGcStruct()
    {
        var doc = NewDoc();
        var (array, views) = MapWithThreeValues(doc);
        var before = views[0].Kind;

        array.Delete(0);

        Assert.AreEqual((ItemKind.Any, ItemKind.GC, true, 3, 0, false, null), (before, views[0].Kind, views[0].Deleted, views[0].Length, views[0].Content.Count, views[0].Countable, views[0].Parent));
    }

    [TestMethod]
    public void Read_GcStructsMerged_EachReadsTheStructThatHoldsItsId()
    {
        var doc = NewDoc();
        var (array, views) = MapWithThreeValues(doc);

        array.Delete(0);

        Assert.AreEqual("GC 3, GC 2, GC 1", string.Join(", ", views.Select(v => $"{v.Kind} {v.Length}")));
    }

    [TestMethod]
    public void Read_GcStructMergedIntoItsLeft_ReadsTheStructThatHoldsItsIdFromTheOffset()
    {
        var doc = NewDoc();
        var (left, right) = (new GC(new ItemId(1, 0), 3), new GC(new ItemId(1, 3), 2));
        doc.Store.AddStruct(ItemRef.Of(left));
        doc.Store.AddStruct(ItemRef.Of(right));
        var view = ItemView.Of(doc, right);

        DeleteSet.TryToMergeWithLefts(doc.Store.Clients[1], 1);

        Assert.AreEqual((2, ItemKind.GC, new ItemId(1, 4), true), (view.Length, view.Kind, view.LastId, right.Detached));
    }

    [TestMethod]
    public void Flag_ItemReplacedByAGcStruct_IsSetOnTheItem()
    {
        var doc = NewDoc();
        var (array, _) = MapWithThreeValues(doc);
        var child = (Item)doc.Store.Find(new ItemId(1, 1)).ToStruct();

        array.Delete(0);

        Assert.AreEqual((true, false), (child.Detached, doc.Store.Find(new ItemId(1, 1)).ToStruct() is Item));
    }

    [TestMethod]
    public void Flag_GcStructMergedIntoItsLeft_IsSetOnTheRightPartOnly()
    {
        var doc = NewDoc();
        var (left, right) = (new GC(new ItemId(1, 0), 3), new GC(new ItemId(1, 3), 2));
        doc.Store.AddStruct(ItemRef.Of(left));
        doc.Store.AddStruct(ItemRef.Of(right));

        DeleteSet.TryToMergeWithLefts(doc.Store.Clients[1], 1);

        Assert.AreEqual((false, true), (left.Detached, right.Detached));
    }

    [TestMethod]
    public void Flag_ItemMergedIntoItsLeft_IsSetOnTheRightPartOnly()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var parts = new List<Item>();
        using var subscription = text.Observe(_ => parts.Add((Item)doc.Store.Find(new ItemId(1, parts.Count)).ToStruct()));
        text.Insert(0, "a");
        text.Insert(1, "b");

        Assert.AreEqual((false, true), (parts[0].Detached, parts[1].Detached));
    }

    [TestMethod]
    public void Read_DefaultView_ThrowsOnEveryMember()
    {
        var view = default(ItemView);

        var all = new Func<object>[]
        {
            () => view.Id, () => view.Kind, () => view.Length, () => view.LastId, () => view.Deleted, () => view.Countable,
            () => view.Keep, () => view.Content, () => view.LeftOrigin, () => view.RightOrigin, () => view.Left, () => view.Right,
            () => view.Parent, () => view.ParentSub, () => view.Redone,
        };

        Assert.AreEqual((true, all.Length), (view.IsDefault, all.Count(read => Throws(read))));
    }

    private static bool Throws(Func<object> read)
    {
        try
        {
            read();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    [TestMethod]
    public void Equals_SameDocAndId_IgnoresTheHintAndHashesTheId()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var made = ItemView.Of(doc, text.StartForTests);
        var found = ItemView.At(doc, new ItemId(1, 0));
        var other = ItemView.At(doc, new ItemId(1, 1));

        Assert.AreEqual(
            (true, true, false, true, false, false, new HashSet<ItemView> { made, found, other }.Count),
            (made == found, made.Equals((object)found), made == other, made.GetHashCode() == found.GetHashCode(), made.Equals(default(ItemView)), made.Equals((object)"x"), 2));
    }

    [TestMethod]
    public void Equals_SameIdOtherDocument_IsNotEqual()
    {
        var a = NewDoc();
        var b = NewDoc();
        a.GetText("t").Insert(0, "x");
        b.GetText("t").Insert(0, "x");

        Assert.AreEqual((false, true), (ItemView.At(a, new ItemId(1, 0)) == ItemView.At(b, new ItemId(1, 0)), default(ItemView) == default));
    }

    [TestMethod]
    public void Read_DetachedView_ReadsItsDecodedObjectAndEqualsOnlyThatObject()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "x");
        var decoded = new Item(new ItemId(1, 0), null, null, null, null, "t", null, new ContentString("yz"));
        var view = ItemView.Detached(doc, decoded);

        Assert.AreEqual(
            (ItemKind.String, 2, "yz", true, false, false, true),
            (view.Kind, view.Length, Text(view.Content), view.IsDetached, view == ItemView.At(doc, new ItemId(1, 0)), view == ItemView.Detached(doc, new Item(new ItemId(1, 0), null, null, null, null, "t", null, new ContentString("yz"))), view == ItemView.Detached(doc, decoded)));
    }

    [TestMethod]
    public void Read_DetachedViewWithNoDocument_NamesNoStoreStruct()
    {
        var decoded = new Item(new ItemId(2, 4), null, new ItemId(2, 3), null, null, "t", null, new ContentString("q"));
        var view = ItemView.Detached(null, decoded);

        Assert.AreEqual((false, (ItemId?)new ItemId(2, 3), (ItemView?)null, (ItemView?)null, "t", null), (view.IsDefault, view.LeftOrigin, view.Left, view.Right, "t", view.Parent as object));
    }

    [TestMethod]
    public void Read_DetachedViewOfAPlannedItem_NamesTheStoreStructsItPointsAt()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var left = text.StartForTests;
        var decoded = new Item(new ItemId(2, 0), left, new ItemId(1, 1), null, null, text, null, new ContentString("c"));

        var view = ItemView.Detached(doc, decoded);

        Assert.AreEqual((new ItemId(1, 0), 2, text), (view.Left.Value.Id, view.Left.Value.Length, view.Parent));
    }

    [TestMethod]
    public void Read_DetachedSkip_IsASkip()
    {
        var view = ItemView.Detached(null, new Skip(new ItemId(3, 7), 5));

        Assert.AreEqual((ItemKind.Skip, 5, true, 0, new ItemId(3, 11)), (view.Kind, view.Length, view.Deleted, view.Content.Count, view.LastId));
    }

    [TestMethod]
    public void Read_RedoneAtAnOffset_MovesWithTheOffset()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        text.StartForTests.Redone = new ItemId(9, 10);
        var view = ItemView.At(doc, new ItemId(1, 2));

        Assert.AreEqual((9L, 12L), (view.Redone.Value.Client, view.Redone.Value.Clock));
    }
}
