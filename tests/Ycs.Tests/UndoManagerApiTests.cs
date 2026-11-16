using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class UndoManagerApiTests
{
    [TestMethod]
    public void CaptureTimeout_ReadsTheTimeProvider()
    {
        var clock = new FakeTimeProvider();
        var doc = new YDoc();
        var text = doc.GetText("t");
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.FromSeconds(1), TimeProvider = clock });

        text.Insert(0, "a");
        clock.Advance(TimeSpan.FromMilliseconds(999));
        text.Insert(1, "b");
        clock.Advance(TimeSpan.FromMilliseconds(1000));
        text.Insert(2, "c");

        Assert.AreEqual(2, um.UndoStack.Count);
    }

    [TestMethod]
    public void StopCapturing_WithinTheTimeout_StartsANewStackItem()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.FromHours(1), TimeProvider = new FakeTimeProvider() });

        text.Insert(0, "a");
        um.StopCapturing();
        text.Insert(1, "b");

        Assert.AreEqual(2, um.UndoStack.Count);
    }

    [TestMethod]
    public void AddToScope_ADocument_TracksEverythingAndKeepsTheTypeList()
    {
        var doc = new YDoc();
        var a = doc.GetText("a");
        var b = doc.GetText("b");
        var um = new UndoManager(a, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });

        b.Insert(0, "x");
        um.AddToScope(doc);
        b.Insert(1, "y");

        Assert.AreEqual((true, 1, 1), (um.TracksWholeDocument, um.Scope.Count, um.UndoStack.Count));
    }

    [TestMethod]
    public void Constructors_ByKindOfScope_SayWhatTheyTrack()
    {
        var doc = new YDoc();
        var a = doc.GetText("a");
        var b = doc.GetMap("b");

        var forType = new UndoManager(a);
        var forTypes = new UndoManager(new YType[] { a, b });
        var forDoc = new UndoManager(doc);

        Assert.AreEqual("False 1|False 2|True 0", $"{forType.TracksWholeDocument} {forType.Scope.Count}|{forTypes.TracksWholeDocument} {forTypes.Scope.Count}|{forDoc.TracksWholeDocument} {forDoc.Scope.Count}");
    }

    [TestMethod]
    public void Constructor_WithoutADocument_Throws()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new UndoManager(Array.Empty<YType>()));
        Assert.ThrowsExactly<ArgumentException>(() => new UndoManager(new YMap()));
    }

    [TestMethod]
    public void AddToScope_AnotherDocument_Throws()
    {
        var um = new UndoManager(new YDoc());

        Assert.ThrowsExactly<ArgumentException>(() => um.AddToScope(new YDoc()));
    }

    [TestMethod]
    public void TrackedOrigins_AreCopied_AndChangedByAddAndRemove()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var origins = new HashSet<object?> { "a" };
        var um = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero, TrackedOrigins = origins });

        origins.Add("b");
        doc.Transact(_ => text.Insert(0, "1"), "b");
        um.AddTrackedOrigin("b");
        doc.Transact(_ => text.Insert(0, "2"), "b");
        um.RemoveTrackedOrigin("a");
        doc.Transact(_ => text.Insert(0, "3"), "a");

        Assert.AreEqual(1, um.UndoStack.Count);
    }

    [TestMethod]
    public void Dispose_UnhooksTheManager_AndKeepsTheStacks()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var um = new UndoManager(text);
        text.Insert(0, "a");

        um.Dispose();
        um.Dispose();
        text.Insert(1, "b");

        Assert.AreEqual(1, um.UndoStack.Count);
    }

    [TestMethod]
    public void StackEvents_CarryTheOperationAndTheChangedTypes()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        var um = new UndoManager(text);
        var seen = new List<string>();
        um.StackItemAdded += (_, e) => seen.Add($"added {e.Operation} {e.ChangedParentTypes.Count} {e.ChangedParentTypes[text].Count}");
        um.StackItemPopped += (_, e) => seen.Add($"popped {e.Operation} {ReferenceEquals(e.Origin, um)}");

        text.Insert(0, "a");
        um.Undo();

        CollectionAssert.AreEqual(new[] { "added Undo 1 1", "added Redo 1 1", "popped Undo True" }, seen);
    }

    [TestMethod]
    public void Options_AreRecords_WithTheirDefaults()
    {
        var options = new UndoManagerOptions();

        Assert.AreEqual((TimeSpan.FromMilliseconds(500), false, null, null), (options.CaptureTimeout, options.IgnoreRemoteMapChanges, options.TrackedOrigins, options.TimeProvider));
        Assert.AreEqual(TimeSpan.Zero, (options with { CaptureTimeout = TimeSpan.Zero }).CaptureTimeout);
    }
}
