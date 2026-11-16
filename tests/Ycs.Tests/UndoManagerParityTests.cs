// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Ports the meaningful behavioural cases for the <see cref="UndoManager"/> surface added to
/// reach Yjs 13.6.32 parity: whole-document scope (<c>new UndoManager(doc)</c>), <c>AddToScope</c>,
/// <c>AddTrackedOrigin</c>/<c>RemoveTrackedOrigin</c>, <c>CanUndo</c>/<c>CanRedo</c>, the
/// <c>stack-cleared</c> event, and <c>Dispose()</c>. The existing <c>UndoRedoTests</c> already
/// cover redo/delete-filter/stack-item-metadata behaviour and are left untouched.
/// </summary>
[TestClass]
public class UndoManagerParityTests
{
    [TestMethod]
    public void TestCanUndoCanRedo()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var um = new UndoManager(text);

        Assert.IsFalse(um.CanUndo);
        Assert.IsFalse(um.CanRedo);

        text.Insert(0, "a");
        Assert.IsTrue(um.CanUndo);
        Assert.IsFalse(um.CanRedo);

        um.Undo();
        Assert.IsFalse(um.CanUndo);
        Assert.IsTrue(um.CanRedo);

        um.Redo();
        Assert.IsTrue(um.CanUndo);
        Assert.IsFalse(um.CanRedo);
    }

    [TestMethod]
    public void TestStackClearedEventFiresOnClear()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var um = new UndoManager(text);
        text.Insert(0, "abc");

        UndoStackClearedEventArgs? captured = null;
        um.StackCleared += (s, e) => captured = e;

        Assert.IsTrue(um.CanUndo);
        um.Clear();

        Assert.IsNotNull(captured);
        Assert.IsTrue(captured.Value.UndoStackCleared);
        Assert.IsTrue(captured.Value.RedoStackCleared);
        Assert.IsFalse(um.CanUndo);
        Assert.IsFalse(um.CanRedo);
    }

    [TestMethod]
    public void TestClearDoesNotFireWhenBothStacksAlreadyEmpty()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var um = new UndoManager(text);

        bool fired = false;
        um.StackCleared += (s, e) => fired = true;

        um.Clear();
        Assert.IsFalse(fired);
    }

    [TestMethod]
    public void TestClearUndoStackOnlyLeavesRedoStackIntact()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var um = new UndoManager(text);

        // Populate the undo stack with two independent entries, then undo one: the undo stack
        // keeps one entry ("a"), and the redo stack now holds the undone one ("b").
        text.Insert(0, "a");
        um.StopCapturing();
        text.Insert(0, "b");
        um.Undo();
        Assert.IsTrue(um.CanUndo);
        Assert.IsTrue(um.CanRedo);

        um.Clear(true, false);
        Assert.IsFalse(um.CanUndo);
        Assert.IsTrue(um.CanRedo, "Clear(true, false) must leave the redo stack untouched.");
    }

    [TestMethod]
    public void TestAddAndRemoveTrackedOrigin()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var um = new UndoManager(text, new UndoManagerOptions { TrackedOrigins = new HashSet<object>() });

        doc.Transact(_ => text.Insert(0, "untracked"), origin: "some-origin");
        Assert.AreEqual(0, um.UndoStack.Count);

        um.AddTrackedOrigin("some-origin");
        doc.Transact(_ => text.Insert(0, "tracked"), origin: "some-origin");
        Assert.AreEqual(1, um.UndoStack.Count);

        um.RemoveTrackedOrigin("some-origin");
        doc.Transact(_ => text.Insert(0, "untracked-again"), origin: "some-origin");
        Assert.AreEqual(1, um.UndoStack.Count, "After RemoveTrackedOrigin, further edits with that origin must not be tracked.");
    }

    [TestMethod]
    public void TestAddToScopeExtendsTrackingToANewType()
    {
        var doc = new YDoc();
        var map1 = doc.GetMap("map1");
        var map2 = doc.GetMap("map2");
        var um = new UndoManager(map1);

        map2["x"] = 1;
        Assert.AreEqual(0, um.UndoStack.Count, "map2 isn't in scope yet.");

        um.AddToScope(map2);
        Assert.IsTrue(um.Scope.Contains(map2));

        map2["y"] = 2;
        Assert.AreEqual(1, um.UndoStack.Count);

        um.Undo();
        Assert.IsFalse(map2.ContainsKey("y"));
        Assert.IsTrue(map2.ContainsKey("x"), "The change made before AddToScope must remain untouched.");
    }

    [TestMethod]
    public void TestDocScopeTracksEveryTypeInTheDocument()
    {
        var doc = new YDoc();
        var map = doc.GetMap("map");
        var arr = doc.GetArray("array");
        var text = doc.GetText("text");
        var um = new UndoManager(doc);

        map["a"] = 1;
        um.StopCapturing();
        arr.Insert(0, 1, 2);
        um.StopCapturing();
        text.Insert(0, "hi");

        Assert.AreEqual(3, um.UndoStack.Count);

        um.Undo();
        Assert.AreEqual(string.Empty, text.ToString());
        um.Undo();
        Assert.AreEqual(0, arr.Length);
        um.Undo();
        Assert.IsFalse(map.ContainsKey("a"));
    }

    [TestMethod]
    public void TestDisposeStopsTrackingAndIsIdempotent()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var um = new UndoManager(text);

        text.Insert(0, "a");
        Assert.AreEqual(1, um.UndoStack.Count);

        um.Dispose();
        um.Dispose(); // Must be safe to call twice.

        text.Insert(1, "b");
        Assert.AreEqual(1, um.UndoStack.Count, "No further transactions should be tracked after Dispose().");
    }

    [TestMethod]
    public void TestUndoManagerDetachesWhenDocIsDestroyed()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        var um = new UndoManager(text);

        doc.Destroy();

        // The manager must have unhooked itself from the (now-destroyed) doc's events: further
        // transactions must not grow its stack, and nothing should throw.
        doc.Transact(_ => text.Insert(0, "x"));
        Assert.AreEqual(0, um.UndoStack.Count);
    }
}
