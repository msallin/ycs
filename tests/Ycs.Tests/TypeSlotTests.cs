using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A store slot of kind Type holds the type-table index of its type in the payload and no object; the index is given
/// when the item is made, and the entry is cleared when the type is collected.
/// </summary>
[TestClass]
public class TypeSlotTests
{
    private static YDoc NewDoc() => new(new YDocOptions { ClientId = 1 });

    [TestMethod]
    public void Insert_NestedType_HoldsItsTableIndexAndNoObject()
    {
        var doc = NewDoc();
        var nested = new YMap();

        doc.GetMap("m")["k"] = nested;

        var item = doc.Store.Find(new ItemId(1, 0));
        Assert.AreEqual((SlotKind.Type, nested.TableIndexForTests, true, null), (item.Kind, (int)doc.Store.Slab.Read(item.Handle).Payload, ReferenceEquals(doc.Store.Types[nested.TableIndexForTests], nested), doc.Store.Slab.GetRef(item.Handle)));
    }

    [TestMethod]
    public void New_TypeItemNotYetIntegrated_HasItsIndexAndFreeingItClearsTheEntry()
    {
        var doc = NewDoc();
        var nested = new YMap();
        var liveBefore = doc.Store.Types.LiveCount;

        var item = ItemRef.New(doc.Store.Slab, new ItemId(1, 0), default, default, doc.GetMap("m"), "k", new ContentType(nested));
        var named = (item.Kind, ReferenceEquals(item.TypeOf, nested), doc.Store.Types.LiveCount - liveBefore, nested.TableIndexForTests >= 0);
        item.FreeSlot();

        // The root parent got its index with the item; the item's type is the other.
        Assert.AreEqual(((SlotKind.Type, true, 2, true), 1), (named, doc.Store.Types.LiveCount - liveBefore));
    }

    [TestMethod]
    public void Collect_NestedType_ClearsItsEntryAndLeavesNoTypeSlot()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var outer = new YMap();
        var inner = new YMap();
        array.Add(outer);
        outer["i"] = inner;

        array.RemoveRange(0, 1);

        var kinds = (doc.Store.Find(new ItemId(1, 0)).Kind, doc.Store.Find(new ItemId(1, 1)).Kind);
        Assert.AreEqual(((SlotKind.Deleted, SlotKind.GC), (true, true)), (kinds, (doc.Store.Types[outer.TableIndexForTests] == null, doc.Store.Types[inner.TableIndexForTests] == null)));
    }

    [TestMethod]
    public void Check_TypeSlotNamingAClearedEntry_IsReported()
    {
        var doc = NewDoc();
        var nested = new YMap();
        doc.GetMap("m")["k"] = nested;
        doc.Store.Types.Collect(nested.TableIndexForTests);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => doc.Store.CheckForTests());

        StringAssert.Contains(thrown.Message, "cleared or missing");
    }

    [TestMethod]
    public void Check_TypeSlotWithAnObject_IsReported()
    {
        var doc = NewDoc();
        doc.GetMap("m")["k"] = new YMap();
        var item = doc.Store.Find(new ItemId(1, 0));
        doc.Store.Slab.SetRef(item.Handle, new ContentType(new YMap()));

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => doc.Store.CheckForTests());

        StringAssert.Contains(thrown.Message, "holds the kind Type and a ContentType");
    }

    [TestMethod]
    public void Insert_NestedTypeIntoACollectedType_IsDeletedAndCollectedWithItsOwnEntryCleared()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var outer = new YMap();
        array.Add(outer);
        array.RemoveRange(0, 1);
        var late = new YMap();

        outer["z"] = late;

        doc.Store.CheckForTests();
        Assert.AreEqual((true, true), (doc.Store.Types[late.TableIndexForTests] == null, ReferenceEquals(doc.Store.Types[outer.TableIndexForTests], outer)));
    }

    [TestMethod]
    public void Redo_DeletedNestedType_MakesANewTypeWithItsOwnIndex()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var undo = new UndoManager(array);
        var nested = new YMap();
        array.Add(nested);
        undo.StopCapturing();
        array.RemoveRange(0, 1);
        undo.StopCapturing();

        undo.Undo();
        var redone = (YMap)array[0].ToObject();

        Assert.AreEqual((false, true), (ReferenceEquals(redone, nested), redone.TableIndexForTests != nested.TableIndexForTests));
    }

    [TestMethod]
    public void Insert_TypeAlreadyIntegrated_ThrowsAndLeavesTheDocumentUnchanged()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var nested = new YMap();
        doc.GetMap("m")["k"] = nested;
        var before = doc.Store.GetState(1);

        Assert.ThrowsExactly<ArgumentException>(() => array.Add(nested));

        doc.Store.CheckForTests();
        Assert.AreEqual((before, 0), (doc.Store.GetState(1), array.Count));
    }

    [TestMethod]
    public void Insert_SameTypeTwiceInOneBatch_ThrowsAndLeavesTheDocumentUnchanged()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var nested = new YMap();
        var before = doc.Store.GetState(1);

        Assert.ThrowsExactly<ArgumentException>(() => array.Add(1, nested, 2, nested));

        doc.Store.CheckForTests();
        Assert.AreEqual((before, 0), (doc.Store.GetState(1), array.Count));
    }

    [TestMethod]
    public void Insert_CollectedType_ThrowsAndLeavesTheDocumentUnchanged()
    {
        var doc = NewDoc();
        var array = doc.GetArray("a");
        var nested = new YMap();
        array.Add(nested);
        array.RemoveRange(0, 1);
        var before = doc.Store.GetState(1);

        Assert.ThrowsExactly<ArgumentException>(() => array.Add(nested));
        Assert.ThrowsExactly<ArgumentException>(() => doc.GetMap("m")["k"] = nested);

        doc.Store.CheckForTests();
        Assert.AreEqual((before, 0), (doc.Store.GetState(1), array.Count));
    }

    [TestMethod]
    public void TypeOf_CollectedType_ThrowsNamingTheStruct()
    {
        var doc = NewDoc();
        var nested = new YMap();
        doc.GetMap("m")["k"] = nested;
        var item = doc.Store.Find(new ItemId(1, 0));
        doc.Store.Types.Collect(nested.TableIndexForTests);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => item.TypeOf);

        StringAssert.Contains(thrown.Message, "1:0");
    }
}
