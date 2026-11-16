using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// A changed type builds its event, and adds itself and its ancestors to
/// <see cref="Transaction.ChangedParentTypes"/>, only when someone can see it.
/// </summary>
[TestClass]
public class EventGatingTests
{
    [TestMethod]
    public void Transact_Unobserved_BuildsNoEvent()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");

        var changedParentTypes = InsertCapturingChangedParentTypes(doc, text);

        Assert.AreEqual(0, changedParentTypes.Count);
    }

    [TestMethod]
    public void Transact_ObservedType_BuildsEvent()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        YEvent seen = null;
        text.Observe(e => seen = e);

        var changedParentTypes = InsertCapturingChangedParentTypes(doc, text);

        Assert.AreSame(seen, changedParentTypes[text][0]);
    }

    [TestMethod]
    public void Transact_DeepObservedAncestor_BuildsEventForTheChain()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var text = new YText();
        map["text"] = text;
        IReadOnlyList<YEvent> seen = null;
        map.ObserveDeep((deepEvents, _) => seen = deepEvents);

        var changedParentTypes = InsertCapturingChangedParentTypes(doc, text);

        CollectionAssert.AreEqual(new YType[] { text, map }, new List<YType>(changedParentTypes.Keys));
        Assert.AreSame(text, seen[0].Target);
    }

    [TestMethod]
    public void Transact_AfterTransactionListener_BuildsEvent()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var count = -1;
        doc.AfterTransaction += (_, tr) => count = tr.ChangedParentTypes.Count;

        InsertCapturingChangedParentTypes(doc, text);

        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public void Undo_WithoutObservers_StillTracksTheType()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var undo = new UndoManager(text);
        text.Insert(0, "abc");

        undo.Undo();

        Assert.AreEqual("", text.ToString());
    }

    [TestMethod]
    public void ChangedKeys_ChildListThenKey_ReturnsNewSetWithBoth()
    {
        var childList = ChangedKeys.Add(null, null);

        var both = ChangedKeys.Add(childList, "key");

        Assert.AreSame(ChangedKeys.ChildList, childList);
        CollectionAssert.AreEquivalent(new[] { null, "key" }, new List<string>(both));
    }

    [TestMethod]
    public void ChangedKeys_ChildListAgain_KeepsTheSharedSet()
    {
        var childList = ChangedKeys.Add(null, null);

        Assert.AreSame(childList, ChangedKeys.Add(childList, null));
    }

    [TestMethod]
    public void ChangedKeys_KeyThenChildList_HoldsBoth()
    {
        var keys = ChangedKeys.Add(null, "key");

        var both = ChangedKeys.Add(keys, null);

        CollectionAssert.AreEquivalent(new[] { "key", null }, new List<string>(both));
    }

    [TestMethod]
    public void ChangedKeys_OneKey_AddingAgainKeepsIt()
    {
        var keys = ChangedKeys.Add(null, "key");

        var again = ChangedKeys.Add(keys, "key");

        Assert.IsTrue(!keys.IsReadOnly && ReferenceEquals(keys, again) && keys.Count == 1 && keys.Contains("key") && !keys.Contains("other"));
    }

    [TestMethod]
    public void ChangedKeys_OneKeyChangedByItsHolder_HoldsBoth()
    {
        var keys = ChangedKeys.Add(null, "key");

        var added = keys.Add("other") && !keys.Add("key");

        Assert.IsTrue(added && keys.SetEquals(new[] { "key", "other" }) && ReferenceEquals(keys, ChangedKeys.Add(keys, "third")) && keys.Count == 3);
    }

    [TestMethod]
    public void Changed_MapKeyAddedByHandler_IsInTheSet()
    {
        // The sets inside the library stay mutable (Transaction.Changed hands them out read-only).
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var seen = new List<string>();
        doc.AfterTransaction += (_, tr) => tr._changed[map].Add("added");
        doc.AfterTransactionCleanup += (_, tr) => seen.AddRange(tr._changed[map]);

        map["key"] = 1;

        CollectionAssert.AreEqual(new[] { "key", "added" }, seen);
    }

    [TestMethod]
    public void ChangedKeys_ThirdKey_AddsToTheSetOfTwo()
    {
        var two = ChangedKeys.Add(ChangedKeys.Add(null, "a"), "b");

        var three = ChangedKeys.Add(two, "c");

        Assert.IsTrue(ReferenceEquals(two, three) && three.SetEquals(new[] { "a", "b", "c" }));
    }

    [TestMethod]
    public void TextEvent_KeysChanged_LeavesOutTheChildList()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        YTextEvent seen = null;
        text.Observe(e => seen = (YTextEvent)e);

        doc.Transact(_ =>
        {
            text.Insert(0, "a");
            text.SetAttribute("lang", "en");
        });

        Assert.IsTrue(seen.ChildListChanged);
        CollectionAssert.AreEqual(new[] { "lang" }, new List<string>(seen.KeysChanged));
    }

    // The Update handler sees the transaction after its observers, and is no transaction listener
    // (YDoc.HasTransactionListeners); a copy, because the transaction is reused once it has ended.
    private static Dictionary<YType, IReadOnlyList<YEvent>> InsertCapturingChangedParentTypes(YDoc doc, YText text)
    {
        Dictionary<YType, IReadOnlyList<YEvent>> captured = null;
        doc.Updated += (_, e) => captured = new Dictionary<YType, IReadOnlyList<YEvent>>(e.Transaction.ChangedParentTypes);

        text.Insert(0, "a");

        return captured;
    }
}
