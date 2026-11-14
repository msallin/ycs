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
        text.EventHandler += (_, e) => seen = e.Event;

        var changedParentTypes = InsertCapturingChangedParentTypes(doc, text);

        Assert.AreSame(seen, changedParentTypes[text][0]);
    }

    [TestMethod]
    public void Transact_DeepObservedAncestor_BuildsEventForTheChain()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var text = new YText();
        map.Set("text", text);
        IList<YEvent> seen = null;
        map.DeepEventHandler += (_, e) => seen = e.Events;

        var changedParentTypes = InsertCapturingChangedParentTypes(doc, text);

        CollectionAssert.AreEqual(new AbstractType[] { text, map }, new List<AbstractType>(changedParentTypes.Keys));
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
    public void ChangedKeys_KeyThenChildList_AddsToTheSameSet()
    {
        var keys = ChangedKeys.Add(null, "key");

        var both = ChangedKeys.Add(keys, null);

        Assert.AreSame(keys, both);
        CollectionAssert.AreEquivalent(new[] { "key", null }, new List<string>(both));
    }

    [TestMethod]
    public void TextEvent_KeysChanged_LeavesOutTheChildList()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        YTextEvent seen = null;
        text.EventHandler += (_, e) => seen = (YTextEvent)e.Event;

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
    private static Dictionary<AbstractType, IList<YEvent>> InsertCapturingChangedParentTypes(YDoc doc, YText text)
    {
        Dictionary<AbstractType, IList<YEvent>> captured = null;
        doc.Update += (_, e) => captured = new Dictionary<AbstractType, IList<YEvent>>(e.transaction.ChangedParentTypes);

        text.Insert(0, "a");

        return captured;
    }
}
