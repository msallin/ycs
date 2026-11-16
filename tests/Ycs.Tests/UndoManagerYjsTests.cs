using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Scenarios where Ycs' UndoManager differed from Yjs 13.6.32's; each expected value is what Yjs does.
/// The differential fuzz in tests/Ycs.Corpus (UndoDifferentialTests) covers the combinations.
/// </summary>
[TestClass]
public class UndoManagerYjsTests
{
    private static YDoc Doc(int clientId) => new YDoc(new YDocOptions { ClientId = clientId });

    private static void Sync(YDoc from, YDoc to) => to.ApplyUpdate(from.EncodeStateAsUpdate(to.EncodeStateVector()), "remote");

    [TestMethod]
    public void Undo_MergedCaptureAroundAnUntrackedChange_KeepsTheUntrackedChange()
    {
        var doc = Doc(1);
        var text = doc.GetText("t");
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 1_000_000_000, TrackedOrigins = new HashSet<object> { "t" } });
        doc.Transact(_ => text.Insert(0, "a"), "t");
        doc.Transact(_ => text.Insert(1, "X"), "u");
        doc.Transact(_ => text.Insert(2, "b"), "t");

        um.Undo();

        Assert.AreEqual("X", text.ToString());
    }

    [TestMethod]
    public void Undo_MergedCaptureAroundARemoteChange_KeepsTheRemoteChange()
    {
        var doc = Doc(1);
        var remote = Doc(2);
        var text = doc.GetText("t");
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 1_000_000_000 });
        text.Insert(0, "a");
        Sync(doc, remote);
        remote.GetText("t").Insert(1, "R");
        Sync(remote, doc);
        text.Insert(2, "b");

        um.Undo();

        Assert.AreEqual("R", text.ToString());
    }

    [TestMethod]
    public void Undo_StackItemWhoseChangeIsGone_UndoesTheNextOne()
    {
        var doc = Doc(1);
        var text = doc.GetText("t");
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0 });
        text.Insert(0, "a");
        text.Insert(1, "b");
        doc.Transact(_ => text.Delete(1, 1), "untracked");

        var result = um.Undo();

        Assert.AreEqual(("", true, 0, 1), (text.ToString(), result != null, um.UndoStack.Count, um.RedoStack.Count));
    }

    [TestMethod]
    public void Undo_MapKeySetSinceByAnUntrackedChange_KeepsThatValue()
    {
        var doc = Doc(1);
        var map = doc.GetMap("m");
        var um = new UndoManager(map, new UndoManagerOptions { CaptureTimeout = 0, TrackedOrigins = new HashSet<object> { "t" } });
        doc.Transact(_ => map.Set("a", 1), "t");
        doc.Transact(_ => map.Set("a", 2), "t");
        map.Set("a", 3);

        var result = um.Undo();

        Assert.AreEqual((3, true, 0, 0), (map.Get("a"), result == null, um.UndoStack.Count, um.RedoStack.Count));
    }

    [TestMethod]
    public void Undo_MapKeySetSinceByARemotePeer_KeepsTheRemoteValue()
    {
        var (map, um, result) = UndoAfterRemoteSet(ignoreRemoteMapChanges: false);

        Assert.AreEqual((3, true, 0, 0), (map.Get("a"), result == null, um.UndoStack.Count, um.RedoStack.Count));
    }

    [TestMethod]
    public void Undo_IgnoreRemoteMapChanges_OverwritesTheRemoteValue()
    {
        var (map, um, result) = UndoAfterRemoteSet(ignoreRemoteMapChanges: true);

        Assert.AreEqual((1, true, 1, 1), (map.Get("a"), result != null, um.UndoStack.Count, um.RedoStack.Count));
    }

    private static (YMap Map, UndoManager Um, StackItem Result) UndoAfterRemoteSet(bool ignoreRemoteMapChanges)
    {
        var doc = Doc(1);
        var remote = Doc(2);
        var map = doc.GetMap("m");
        var um = new UndoManager(map, new UndoManagerOptions { CaptureTimeout = 0, IgnoreRemoteMapChanges = ignoreRemoteMapChanges });
        map.Set("a", 1);
        map.Set("a", 2);
        Sync(doc, remote);
        remote.GetMap("m").Set("a", 3);
        Sync(remote, doc);
        return (map, um, um.Undo());
    }

    [TestMethod]
    public void Undo_DeletedNestedText_RestoresIt()
    {
        var doc = Doc(1);
        var array = doc.GetArray("a");
        var um = new UndoManager(array, new UndoManagerOptions { CaptureTimeout = 0 });
        var nested = new YText();
        array.Insert(0, new object[] { nested });
        nested.Insert(0, "pq");
        array.Delete(0, 1);

        um.Undo();

        Assert.AreEqual("pq", (array.Get(0) as YText)?.ToString());
    }

    [TestMethod]
    public void CaptureTransaction_ReturnsFalse_TransactionIsNotCaptured()
    {
        var doc = Doc(1);
        var text = doc.GetText("t");
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0, CaptureTransaction = tr => tr.Origin is not "skip" });
        text.Insert(0, "a");
        doc.Transact(_ => text.Insert(1, "b"), "skip");

        um.Undo();

        Assert.AreEqual(("b", 0), (text.ToString(), um.UndoStack.Count));
    }

    [TestMethod]
    public void Events_MergedChangeUndoAndClear_FireInYjsOrder()
    {
        var doc = Doc(1);
        var text = doc.GetText("t");
        var um = new UndoManager(text);
        var events = new List<string>();
        um.StackItemAdded += (_, e) => events.Add($"added {e.Type} current={um.CurrentStackItem != null}");
        um.StackItemUpdated += (_, e) => events.Add($"updated {e.Type}");
        um.StackItemPopped += (_, e) => events.Add($"popped {e.Type} current={um.CurrentStackItem == e.StackItem}");
        um.StackCleared += (_, e) => events.Add($"cleared inTransaction={doc._transaction != null}");
        text.Insert(0, "a");
        text.Insert(1, "b");

        um.Undo();
        um.Clear();

        CollectionAssert.AreEqual(
            new[] { "added Undo current=False", "updated Undo", "added Redo current=True", "popped Undo current=True", "cleared inTransaction=True" },
            events);
    }

    [TestMethod]
    public void Stacks_AfterUndo_HoldTheItemsOldestFirst()
    {
        var doc = Doc(1);
        var text = doc.GetText("t");
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = 0 });
        text.Insert(0, "a");
        var first = um.UndoStack[0];
        text.Insert(1, "b");

        var undone = um.Undo();

        Assert.AreEqual((1, true, true, false), (um.UndoStack.Count, um.UndoStack[0] == first, um.RedoStack.Count == 1 && undone != first, um.CurrentStackItem != null));
    }

    [TestMethod]
    public void Popped_UndoInsideAnOuterTransaction_ChangedParentTypesFilledWhenItEnds()
    {
        var doc = Doc(1);
        var text = doc.GetText("t");
        var um = new UndoManager(text);
        text.Insert(0, "abc");
        IDictionary<YType, IList<YEvent>> changed = null;
        um.StackItemPopped += (_, e) => changed = e.ChangedParentTypes;

        doc.Transact(_ => um.Undo());

        Assert.IsTrue(changed.ContainsKey(text));
    }
}
