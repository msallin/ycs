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
        using var subscription = text.Observe(_ => views.Add(ItemView.Of(doc, doc.Store.Find(new ItemId(1, clock)))));
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
        array.Add(map);
        map["a"] = "x";
        map["b"] = "y";
        map["c"] = "z";
        return (array, new[] { "a", "b", "c" }.Select(key => ItemView.Of(doc, map.MapItem(map._map[key]))).ToArray());
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
        var whole = ItemView.Of(doc, text._start);
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

        array.RemoveAt(0);

        Assert.AreEqual((ItemKind.Any, ItemKind.GC, true, 3, 0, false, null), (before, views[0].Kind, views[0].Deleted, views[0].Length, views[0].Content.Count, views[0].Countable, views[0].Parent));
    }

    [TestMethod]
    public void Read_GcStructsMerged_EachReadsTheStructThatHoldsItsId()
    {
        var doc = NewDoc();
        var (array, views) = MapWithThreeValues(doc);

        array.RemoveAt(0);

        Assert.AreEqual("GC 3, GC 2, GC 1", string.Join(", ", views.Select(v => $"{v.Kind} {v.Length}")));
    }

    [TestMethod]
    public void Read_GcStructMergedIntoItsLeft_ReadsTheStructThatHoldsItsIdFromTheOffset()
    {
        var doc = NewDoc();
        _ = AdoptGc(doc, 0, 3);
        var right = AdoptGc(doc, 3, 2);
        var view = ItemView.Of(doc, right);

        DeleteSet.TryToMergeWithLefts(doc.Store.Clients[1], 1);

        Assert.AreEqual((2, ItemKind.GC, new ItemId(1, 4), true), (view.Length, view.Kind, view.LastId, right.IsFreed));
    }

    [TestMethod]
    public void Flag_ItemReplacedByAGcStruct_KeepsItsSlot()
    {
        var doc = NewDoc();
        var (array, _) = MapWithThreeValues(doc);
        var child = doc.Store.Find(new ItemId(1, 1));
        var handle = child.Handle;

        array.RemoveAt(0);

        Assert.AreEqual((false, true, handle), (child.IsFreed, child.IsGC, doc.Store.Find(new ItemId(1, 1)).Handle));
    }

    [TestMethod]
    public void Flag_GcStructMergedIntoItsLeft_IsSetOnTheRightPartOnly()
    {
        var doc = NewDoc();
        var left = AdoptGc(doc, 0, 3);
        var right = AdoptGc(doc, 3, 2);

        DeleteSet.TryToMergeWithLefts(doc.Store.Clients[1], 1);

        Assert.AreEqual((false, true), (left.IsFreed, right.IsFreed));
    }

    [TestMethod]
    public void Flag_ItemMergedIntoItsLeft_IsSetOnTheRightPartOnly()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        var parts = new List<ItemRef>();
        using var subscription = text.Observe(_ => parts.Add(doc.Store.Find(new ItemId(1, parts.Count))));
        text.Insert(0, "a");
        text.Insert(1, "b");

        Assert.AreEqual((false, true), (parts[0].IsFreed, parts[1].IsFreed));
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

    // A GC struct of client 1 in the store, outside any transaction (a transaction merges the ones it adds).
    private static ItemRef AdoptGc(YDoc doc, long clock, int length)
    {
        return doc.Store.AddGcForTests(new ItemId(1, clock), length);
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
        var made = ItemView.Of(doc, text._start);
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

    private static StagingTable StagedString(long client, long clock, string text, ItemId? leftOrigin = null, ItemId? rightOrigin = null, string parentSub = null)
    {
        var table = new StagingTable();
        table.BeginClient(client, clock);
        var i = table.Add(StagedKind.String, text.Length, leftOrigin, rightOrigin);
        table.SetParentRoot(i, "t");
        if (parentSub != null)
        {
            table.SetParentSub(i, parentSub);
        }

        text.CopyTo(table.Chars(i));
        return table;
    }

    [TestMethod]
    public void Read_DetachedView_ReadsItsStagedStructAndEqualsOnlyThatStruct()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "x");
        var table = StagedString(1, 0, "yz");
        var view = ItemView.Detached(doc, table, 0);

        Assert.AreEqual(
            (ItemKind.String, 2, "yz", true, false, false, true, false),
            (view.Kind, view.Length, Text(view.Content), view.IsDetached, view == ItemView.At(doc, new ItemId(1, 0)), view == ItemView.Detached(doc, StagedString(1, 0, "yz"), 0), view == ItemView.Detached(doc, table, 0), view.Deleted));
    }

    [TestMethod]
    public void Read_DetachedViewWithNoDocument_ReadsOriginsAndNamesNoNeighbourOrParent()
    {
        var table = StagedString(2, 4, "q", new ItemId(2, 3), new ItemId(5, 1), "k");
        var view = ItemView.Detached(null, table, 0);

        Assert.AreEqual(
            (false, (ItemId?)new ItemId(2, 3), (ItemId?)new ItemId(5, 1), (ItemView?)null, (ItemView?)null, (string)"k", (object)null, (ItemId?)null, true, false, new ItemId(2, 4), new ItemId(2, 4)),
            (view.IsDefault, view.LeftOrigin, view.RightOrigin, view.Left, view.Right, view.ParentSub, view.Parent, view.Redone, view.Countable, view.Keep, view.Id, view.LastId));
    }

    [TestMethod]
    public void Read_DetachedSkipAndGC_AreDeletedWithNoContent()
    {
        var table = new StagingTable();
        table.BeginClient(3, 7);
        table.Add(StagedKind.Skip, 5);
        table.Add(StagedKind.GC, 2);
        var skip = ItemView.Detached(null, table, 0);
        var gc = ItemView.Detached(null, table, 1);

        Assert.AreEqual(
            (ItemKind.Skip, 5, true, 0, new ItemId(3, 11), ItemKind.GC, 2, true, 0, new ItemId(3, 13)),
            (skip.Kind, skip.Length, skip.Deleted, skip.Content.Count, skip.LastId, gc.Kind, gc.Length, gc.Deleted, gc.Content.Count, gc.LastId));
    }

    [TestMethod]
    public void Read_DetachedViewsOfOtherKinds_ReadTheirContent()
    {
        var table = new StagingTable();
        table.BeginClient(1, 0);
        var any = table.Add(StagedKind.Any, 2);
        table.Values(any)[0] = 5L;
        table.Values(any)[1] = "s";
        var deleted = table.Add(StagedKind.Deleted, 3);
        var type = table.Add(StagedKind.Type, 1);
        table.SetType(type, 0, null);
        var embed = table.Add(StagedKind.Embed, 1);
        table.SetCold(embed, new ContentEmbed("e"));
        var views = Enumerable.Range(0, 4).Select(i => ItemView.Detached(null, table, i)).ToArray();

        Assert.AreEqual(
            ("5,s", ItemKind.Deleted, 0, true, ItemKind.Type, 0, ItemKind.Embed, "e"),
            (string.Join(",", views[0].Content), views[1].Kind, views[1].Content.Count, views[0].Countable, views[2].Kind, views[2].Content.Count, views[3].Kind, (string)views[3].Content.Single()));
    }

    [TestMethod]
    public void Read_DetachedViewAfterReset_ReadsTheStoreStructThatHoldsItsId()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "abcdef");
        var table = StagedString(1, 2, "XY");
        var view = ItemView.Detached(doc, table, 0);
        Assert.AreEqual("XY", Text(view.Content));

        table.Reset();

        Assert.AreEqual(("cdef", 4, new ItemId(1, 2), new ItemId(1, 5)), (Text(view.Content), view.Length, view.Id, view.LastId));
    }

    [TestMethod]
    public void Read_DetachedViewAfterTruncate_ReadsTheStore()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "abc");
        var table = StagedString(1, 1, "Q");
        var view = ItemView.Detached(doc, table, 0);

        table.Truncate();

        Assert.AreEqual("bc", Text(view.Content));
    }

    [TestMethod]
    public void Read_DetachedViewAfterAnotherStructWasStaged_StillReadsItsStruct()
    {
        var table = StagedString(1, 0, "ab");
        var view = ItemView.Detached(null, table, 0);

        table.Add(StagedKind.GC, 1);

        Assert.AreEqual("ab", Text(view.Content));
    }

    [TestMethod]
    public void Read_DetachedViewAfterReleaseWhoseStructIsNotInTheStore_Throws()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "abc");
        var beyond = StagedString(1, 3, "x");
        var other = StagedString(9, 0, "x");
        var noDocument = StagedString(1, 0, "x");
        var views = new[] { ItemView.Detached(doc, beyond, 0), ItemView.Detached(doc, other, 0), ItemView.Detached(null, noDocument, 0) };

        beyond.Reset();
        other.Reset();
        noDocument.Reset();

        Assert.AreEqual(3, views.Count(v => Throws(() => v.Length)));
    }

    [TestMethod]
    public void Read_RedoneAtAnOffset_MovesWithTheOffset()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");
        var start = text._start;
        start.Redone = new ItemId(9, 10);
        var view = ItemView.At(doc, new ItemId(1, 2));

        Assert.AreEqual((9L, 12L), (view.Redone.Value.Client, view.Redone.Value.Clock));
    }
}
