using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The content accessors of <see cref="ItemRef"/>, the slot kind they read through, and the rules that keep it equal to the content object's.</summary>
[TestClass]
public class ItemRefContentTests
{
    private static ItemRef NewItem(Slab slab, IContent content, long clock = 0) =>
        ItemRef.New(slab, new ItemId(1, clock), default, default, new YMap(), null, content);

    private static ItemRef Text(Slab slab, string text, long clock = 0) =>
        ItemRef.New(slab, new ItemId(1, clock), default, default, new YMap(), null, text.AsSpan());

    private static ItemRef Values(Slab slab, object[] values, long clock = 0) =>
        ItemRef.New(slab, new ItemId(1, clock), default, default, new YMap(), null, values.AsSpan());

    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 });

    [TestMethod]
    [DataRow((int)SlotKind.Deleted, ItemKind.Deleted)]
    [DataRow((int)SlotKind.Json, ItemKind.Json)]
    [DataRow((int)SlotKind.Binary, ItemKind.Binary)]
    [DataRow((int)SlotKind.String, ItemKind.String)]
    [DataRow((int)SlotKind.Embed, ItemKind.Embed)]
    [DataRow((int)SlotKind.Format, ItemKind.Format)]
    [DataRow((int)SlotKind.Type, ItemKind.Type)]
    [DataRow((int)SlotKind.Any, ItemKind.Any)]
    [DataRow((int)SlotKind.Doc, ItemKind.Doc)]
    [DataRow((int)SlotKind.Skip, ItemKind.Skip)]
    [DataRow((int)SlotKind.GC, ItemKind.GC)]
    public void ToItemKind_EveryKindButFree_IsThePublicKind(int kind, ItemKind expected)
    {
        Assert.AreEqual(expected, ((SlotKind)kind).ToItemKind());
    }

    [TestMethod]
    public void ToItemKind_Free_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SlotKind.Free.ToItemKind());
    }

    [TestMethod]
    public void SlotKind_Free_IsTheSlabsFreeKind()
    {
        var free = Slot.FreeKind;

        Assert.AreEqual((int)SlotKind.Free, free);
    }

    [TestMethod]
    public void IsCountable_EveryKind_FollowsYjs()
    {
        var countable = new List<SlotKind>();
        foreach (var kind in Enum.GetValues<SlotKind>())
        {
            if (kind.IsCountable())
            {
                countable.Add(kind);
            }
        }

        CollectionAssert.AreEquivalent(
            new[] { SlotKind.Json, SlotKind.Binary, SlotKind.String, SlotKind.Embed, SlotKind.Type, SlotKind.Any, SlotKind.Doc },
            countable);
    }

    [TestMethod]
    public void IsCold_EveryKind_IsTheKindsWithoutInlineStorage()
    {
        var cold = new List<SlotKind>();
        foreach (var kind in Enum.GetValues<SlotKind>())
        {
            if (kind.IsCold())
            {
                cold.Add(kind);
            }
        }

        CollectionAssert.AreEquivalent(new[] { SlotKind.Json, SlotKind.Binary, SlotKind.Embed, SlotKind.Format, SlotKind.Doc }, cold);
    }

    [TestMethod]
    public void Kind_EachContent_IsItsKindAndGcIsGc()
    {
        var slab = new Slab();
        var kinds = new[]
        {
            Text(slab, "a", 0).Kind,
            Values(slab, new object[] { 1 }, 1).Kind,
            NewItem(slab, new ContentType(new YMap()), 3).Kind,
            NewItem(slab, new ContentFormat("b", true), 4).Kind,
            NewItem(slab, new ContentEmbed("e"), 5).Kind,
            NewItem(slab, new ContentBinary([1]), 6).Kind,
            NewItem(slab, new ContentDoc(new YDoc()), 7).Kind,
            ItemRef.NewGC(slab, new ItemId(1, 8), 1).Kind,
        };

        CollectionAssert.AreEqual(
            new[] { SlotKind.String, SlotKind.Any, SlotKind.Type, SlotKind.Format, SlotKind.Embed, SlotKind.Binary, SlotKind.Doc, SlotKind.GC },
            kinds);
    }

    [TestMethod]
    public void New_EachKind_CountableFollowsTheKind()
    {
        var slab = new Slab();
        var items = new[]
        {
            Text(slab, "a", 0),
            NewItem(slab, new ContentFormat("b", true), 1),
            NewItem(slab, new ContentType(new YMap()), 2),
        };

        CollectionAssert.AreEqual(new[] { true, false, true }, new[] { items[0].Countable, items[1].Countable, items[2].Countable });
    }

    [TestMethod]
    public void Chars_StringItem_IsTheText()
    {
        var item = Text(new Slab(), "héllo");

        Assert.AreEqual("héllo", item.Chars.ToString());
    }

    [TestMethod]
    public void Chars_NotAString_Throws()
    {
        var item = Values(new Slab(), new object[] { 1 });

        Assert.ThrowsExactly<InvalidOperationException>(() => item.Chars.ToString());
    }

    [TestMethod]
    public void ValueAtAndValues_AnyItem_AreTheValues()
    {
        var item = Values(new Slab(), new object[] { "x", 2L });

        Assert.AreEqual(("x", 2L, 2), (item.ValueAt(0), item.ValueAt(1), item.Values.Count));
    }

    [TestMethod]
    public void ValueAt_NotAny_Throws()
    {
        var item = Text(new Slab(), "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => item.ValueAt(0));
    }

    [TestMethod]
    public void TypeOf_TypeItem_IsTheType()
    {
        var type = new YMap();
        var item = NewItem(new Slab(), new ContentType(type));

        Assert.AreSame(type, item.TypeOf);
    }

    [TestMethod]
    public void TypeOf_NotAType_Throws()
    {
        var item = Text(new Slab(), "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => item.TypeOf.ToString());
    }

    [TestMethod]
    public void Cold_ColdKinds_AreTheContentObjectAndTheOthersAreNull()
    {
        var slab = new Slab();
        var format = new ContentFormat("b", true);
        var embed = new ContentEmbed("e");
        var binary = new ContentBinary([1]);

        Assert.IsTrue(
            ReferenceEquals(NewItem(slab, format, 0).Cold, format)
            && ReferenceEquals(NewItem(slab, embed, 1).Cold, embed)
            && ReferenceEquals(NewItem(slab, binary, 2).Cold, binary)
            && Text(slab, "a", 3).Cold is null
            && Text(slab, "b", 4).Cold is null);
    }

    [TestMethod]
    public void AppendContentTo_Items_AppendsTheElements()
    {
        var slab = new Slab();
        var destination = new List<object> { "first" };

        Values(slab, new object[] { 1L, 2L }, 0).AppendContentTo(destination);
        Text(slab, "ab", 2).AppendContentTo(destination);

        CollectionAssert.AreEqual(new object[] { "first", 1L, 2L, "a", "b" }, destination);
    }

    [TestMethod]
    public void NewCopyOf_StringItem_CopiesTheRangeToAnotherOne()
    {
        var slab = new Slab();
        var item = Text(slab, "abc");

        var copy = ItemRef.NewCopyOf(slab, new ItemId(1, 10), default, default, item.Parent, null, item);

        Assert.AreEqual(("abc", true), (copy.Chars.ToString(), slab.Read(copy.Handle).Payload != slab.Read(item.Handle).Payload));
    }

    [TestMethod]
    public void CopyContent_StringItem_Throws()
    {
        var item = Text(new Slab(), "abc");

        Assert.ThrowsExactly<InvalidOperationException>(() => item.CopyContent());
    }

    [TestMethod]
    public void NewCopyOf_AnyItem_CopiesTheValuesToARangeOfItsOwn()
    {
        var slab = new Slab();
        var item = Values(slab, new object[] { 1L, 2L });

        var copy = ItemRef.NewCopyOf(slab, new ItemId(1, 10), default, default, item.Parent, null, item);

        Assert.AreEqual(("1,2", true), (string.Join(",", copy.ValueSpan.ToArray()), copy.Slab.Read(copy.Handle).Payload != item.Slab.Read(item.Handle).Payload));
    }

    [TestMethod]
    public void CopyContent_AnyItem_Throws()
    {
        var item = Values(new Slab(), new object[] { 1L });

        Assert.ThrowsExactly<InvalidOperationException>(() => item.CopyContent());
    }

    [TestMethod]
    public void AppendChars_StringItem_ExtendsTheText()
    {
        var item = Text(new Slab(), "ab");

        item.AppendChars("cd");
        item.Length += 2;

        Assert.AreEqual("abcd", item.Chars.ToString());
    }

    [TestMethod]
    public void AppendChars_NotAString_Throws()
    {
        var item = Values(new Slab(), new object[] { 1 });

        Assert.ThrowsExactly<InvalidOperationException>(() => item.AppendChars("x"));
    }

    [TestMethod]
    public void AppendValues_AnyItem_ExtendsTheValues()
    {
        var item = Values(new Slab(), new object[] { 1L });

        item.AppendValues([2L, 3L]);
        item.Length += 2;

        CollectionAssert.AreEqual(new object[] { 1L, 2L, 3L }, new List<object>(item.Values));
    }

    [TestMethod]
    public void Deleted_CollectedItem_HoldsNoObjectAndKeepsItsLength()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "abc");
        doc.GetText("t").Remove(0, 3);

        var item = doc.Store.Find(new ItemId(1, 0));

        Assert.AreEqual((SlotKind.Deleted, null, 3, 0), (item.Kind, doc.Store.Slab.GetRef(item.Handle), item.Length, item.Elements.Count));
    }

    [TestMethod]
    public void NewCopyOf_DeletedItem_IsDeletedOfTheSameLengthWithoutAnObject()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "abc");
        doc.GetText("t").Remove(0, 3);
        var item = doc.Store.Find(new ItemId(1, 0));

        var copy = ItemRef.NewCopyOf(doc.Store.Slab, new ItemId(1, 10), default, default, item.Parent, null, item);

        Assert.AreEqual((SlotKind.Deleted, 3, null), (copy.Kind, copy.Length, doc.Store.Slab.GetRef(copy.Handle)));
    }

    [TestMethod]
    public void CopyContent_DeletedItem_Throws()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "abc");
        doc.GetText("t").Remove(0, 3);
        var item = doc.Store.Find(new ItemId(1, 0));

        Assert.ThrowsExactly<InvalidOperationException>(() => item.CopyContent());
    }

    [TestMethod]
    public void BecomeGC_Item_IsKindGcAndNotCountable()
    {
        var item = Text(new Slab(), "abc");

        item.BecomeGC();

        Assert.AreEqual((SlotKind.GC, false), (item.Kind, item.Countable));
    }

    [TestMethod]
    public void Collect_CountableItem_KeepsCountableAndTurnsDeleted()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcd");

        text.Remove(0, 4);

        var item = doc.Store.Find(new ItemId(1, 0));
        Assert.AreEqual((SlotKind.Deleted, true), (item.Kind, item.Countable));
    }

    [TestMethod]
    public void Split_CollectedCountableItem_RightPartIsNotCountable()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcd");
        text.Remove(0, 4);
        var parts = string.Empty;

        doc.Transact(transaction =>
        {
            var left = doc.Store.Find(new ItemId(1, 0));
            var right = doc.Store.GetItemCleanStart(transaction, new ItemId(1, 2));
            parts = $"{left.Kind} {left.Countable} {right.Kind} {right.Countable}";
        });

        Assert.AreEqual("Deleted True Deleted False", parts);
    }

    [TestMethod]
    public void Split_LiveStringItem_BothPartsAreStringAndCountable()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcd");
        var parts = string.Empty;

        doc.Transact(transaction =>
        {
            var left = doc.Store.Find(new ItemId(1, 0));
            var right = doc.Store.GetItemCleanStart(transaction, new ItemId(1, 1));
            parts = $"{left.Kind} {left.Countable} {right.Kind} {right.Countable} {left.Chars} {right.Chars}";
        });

        Assert.AreEqual("String True String True a bcd", parts);
    }

    [TestMethod]
    public void Adopt_UpdateWithEveryKind_SlotKindsMatchTheirContent()
    {
        var a = NewDoc();
        var text = a.GetText("t");
        text.Insert(0, "ab");
        text.InsertEmbed(1, (YValue)5);
        a.GetArray("arr").Add(1, 2);
        a.GetArray("arr").Add(new YMap());
        var b = new YDoc(new YDocOptions { ClientId = 2 });

        b.ApplyUpdate(a.EncodeStateAsUpdate());

        b.Store.CheckForTests();
        var kinds = new List<SlotKind>();
        foreach (var structs in b.Store.Clients.Values)
        {
            foreach (var s in structs)
            {
                kinds.Add(s.Kind);
            }
        }

        CollectionAssert.IsSubsetOf(new[] { SlotKind.String, SlotKind.Embed, SlotKind.Any, SlotKind.Type }, kinds);
    }

    [TestMethod]
    public void Check_KindOfTheSlotDiffersFromTheContent_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "b");
            ref var slot = ref doc.Store.Find(new ItemId(1, 1)).Poke();
            slot.Info = (slot.Info & ~(0xFF << 8)) | ((int)SlotKind.Any << 8);
        }));
    }

    [TestMethod]
    public void Check_CountableFlagOfALiveItemDiffersFromItsKind_Throws()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "a");

        Assert.ThrowsExactly<InvalidOperationException>(() => doc.Transact(_ =>
        {
            text.Insert(0, "b");
            doc.Store.Find(new ItemId(1, 1)).Poke().Info &= ~Slot.CountableFlag;
        }));
    }
}
