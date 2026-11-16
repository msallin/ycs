using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A user can hold a type or a subdocument after it was collected, when its struct has merged away (and a slot
/// can be reused, docs/design-item-layout.md, 2.4): it still answers as Yjs' orphaned item does. The expected
/// values are Yjs 13.6.32's, for the same script.
/// </summary>
[TestClass]
public class CollectedTypeFactsTests
{
    // Whether the scenarios run with a collected type's handle replaced by an unrelated live item (a reused slot).
    protected virtual bool PoisonHandles => false;

    [TestInitialize]
    public void SetPoison() => YType.PoisonCollectedHandleForTests = PoisonHandles || YType.PoisonCollectedHandleForTests;

    // inner's item is a GC struct that merges with its neighbours' (all one client, consecutive clocks); outer's item
    // is a deleted item that merges into the deleted item left of it.
    private static (YDoc Doc, YArray Array, YMap Outer, YMap Inner) Collected()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var outer = new YMap();
        var inner = new YMap();
        array.Push(new object[] { "x" });
        array.Push(new object[] { outer });
        outer.Set("a1", 1);
        outer.Set("i", inner);
        outer.Set("b1", 2);
        inner.Set("p", "v");

        array.Delete(0, 2);

        return (doc, array, outer, inner);
    }

    [TestMethod]
    public void Parent_OfCollectedTypes_IsTheTypeTheyWereIn()
    {
        var (_, array, outer, inner) = Collected();

        Assert.AreEqual((true, true), (ReferenceEquals(inner.Parent, outer), ReferenceEquals(outer.Parent, array)));
    }

    [TestMethod]
    public void FromTypeIndex_OfCollectedTypes_NamesTheirItems()
    {
        var (_, _, outer, inner) = Collected();

        var ids = new[] { inner, outer }.Select(t => RelativePosition.FromIndex(t, 0).TypeId).ToArray();

        CollectionAssert.AreEqual(new ItemId?[] { new ItemId(1, 3), new ItemId(1, 1) }, ids);
    }

    [TestMethod]
    public void Set_OnACollectedType_ChangesNothingVisibleAndEncodesAsYjs()
    {
        var (doc, _, _, inner) = Collected();

        inner.Set("z", 1);

        Assert.AreEqual(
            ("0", "1,3,1,0,1,1,1,97,2,0,4,33,0,1,3,1,122,1,1,1,1,0,7"),
            (inner.Count.ToString(), string.Join(",", doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void Change_OfACollectedType_IsNotReportedToItsFormerParentsDeepObservers()
    {
        var (doc, array, outer, _) = Collected();
        var events = 0;
        array.DeepEventHandler += (_, _) => events++;

        outer.Set("z", 1);

        Assert.AreEqual((0, 0), (events, array.Length));
    }

    [TestMethod]
    public void Destroy_OfASubdocumentRemovedByCleanup_ReportsItRemovedAgain()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var map = doc.GetMap("m");
        var sub = new YDoc(new YDocOptions { Guid = "g", ShouldLoad = false });
        var log = new System.Collections.Generic.List<string>();
        doc.SubdocsChanged += (_, e) => log.Add($"{e.Loaded.Count}{e.Added.Count}{e.Removed.Count}");
        map.Set("s", sub);
        map.Set("t", 1);

        doc.Transact(_ =>
        {
            map.Delete("s");
            map.Delete("t");
        });
        sub.Load();
        sub.Destroy();

        Assert.AreEqual("010,001,001", string.Join(",", log));
    }

    private static string Bytes(YDoc doc) => string.Join(",", doc.EncodeStateAsUpdate());

    private static string SubdocLog(YDoc doc, System.Collections.Generic.List<string> log)
    {
        doc.SubdocsChanged += (_, e) => log.Add($"L{e.Loaded.Count}A{e.Added.Count}R{e.Removed.Count}");
        return "";
    }

    [TestMethod]
    public void LoadAndDestroy_OfACollectedSubdocumentCleanupDoesNotDestroy_ReachTheParentDocument()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var m = doc.GetMap("m");
        var sub = new YDoc(new YDocOptions { Guid = "g", ShouldLoad = false });
        var log = new System.Collections.Generic.List<string>();
        SubdocLog(doc, log);
        doc.Transact(_ =>
        {
            m.Set("s", sub);
            m.Set("t", 1);
            m.Delete("s");
        });
        m.Delete("t");

        sub.Load();
        sub.Destroy();

        Assert.AreEqual("L1A0R0,L0A0R1", string.Join(",", log));
    }

    [TestMethod]
    public void Insert_OfACollectedSubdocumentAgain_BelongsToTheNewItem()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var m = doc.GetMap("m");
        var sub = new YDoc(new YDocOptions { Guid = "g", ShouldLoad = false });
        var log = new System.Collections.Generic.List<string>();
        SubdocLog(doc, log);
        doc.Transact(_ =>
        {
            m.Set("s", sub);
            m.Delete("s");
        });
        m.Set("s2", sub);

        sub.Destroy();

        Assert.AreEqual(("L0A1R0,L0A1R1", true), (string.Join(",", log), !ReferenceEquals(m.Get("s2"), sub)));
    }

    private static (YDoc Doc, YMap M, YMap Mid, YMap Inner) NestedCollected()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var m = doc.GetMap("m");
        var mid = new YMap();
        var inner = new YMap();
        m.Set("mid", mid);
        mid.Set("inner", inner);
        mid.Delete("inner");
        return (doc, m, mid, inner);
    }

    [TestMethod]
    public void Set_OnACollectedTypeUnderAScopedUndoManager_KeepsItsParents()
    {
        var (doc, m, _, inner) = NestedCollected();
        _ = new UndoManager(m);
        doc.Transact(_ =>
        {
            inner.Set("z", "payload");
            m.Set("q", 1);
        });

        doc.Transact(_ => m.Delete("mid"), origin: "remote");

        Assert.AreEqual(
            "1,4,1,0,39,1,1,109,3,109,105,100,1,33,0,1,0,5,105,110,110,101,114,1,40,0,1,1,1,122,1,119,7,112,97,121,108,111,97,100,40,1,1,109,1,113,1,125,1,1,1,1,0,3",
            Bytes(doc));
    }

    [TestMethod]
    public void Set_OnACollectedTypeUnderADocumentUndoManager_KeepsItsParents()
    {
        var (doc, m, _, inner) = NestedCollected();
        _ = new UndoManager(doc);
        inner.Set("z", "payload");
        m.Set("q", 1);

        doc.Transact(_ => m.Delete("mid"), origin: "remote");

        Assert.AreEqual(
            "1,4,1,0,39,1,1,109,3,109,105,100,1,33,0,1,0,5,105,110,110,101,114,1,40,0,1,1,1,122,1,119,7,112,97,121,108,111,97,100,40,1,1,109,1,113,1,125,1,1,1,1,0,3",
            Bytes(doc));
    }

    [TestMethod]
    public void Path_OfAHeldEventWhoseTargetWasCollected_IsTheOldPath()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var m = doc.GetMap("m");
        var mid = new YMap();
        var arr = new YArray();
        m.Set("mid", mid);
        mid.Set("arr", arr);
        YEvent held = null;
        m.DeepEventHandler += (_, e) => held ??= e.Events.FirstOrDefault(ev => ReferenceEquals(ev.Target, arr));
        arr.Push(new object[] { 1 });
        m.Delete("mid");

        Assert.AreEqual("mid/arr", string.Join("/", held.Path));
    }
}

/// <summary>The same scenarios with a collected type's handle replaced by an unrelated live item, as a reused slot would be.</summary>
[TestClass]
public class CollectedTypeFactsPoisonedTests : CollectedTypeFactsTests
{
    protected override bool PoisonHandles => true;

    [TestCleanup]
    public void ClearPoison() => YType.PoisonCollectedHandleForTests = false;
}
