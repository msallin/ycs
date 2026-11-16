// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class UndoRedoTests : YTestBase
{
    [TestMethod]
    public void TestUndoText()
    {
        Init(users: 3);
        var text0 = Texts[Users[0]];
        var text1 = Texts[Users[1]];
        var undoManager = new UndoManager(text0);

        // Items that are added and deleted in the same transaction won't be undone.
        text0.Insert(0, "test");
        text0.Remove(0, 4);
        undoManager.Undo();
        Assert.AreEqual(string.Empty, text0.ToString());

        // Follow redone items.
        text0.Insert(0, "a");
        undoManager.StopCapturing();
        text0.Remove(0, 1);
        undoManager.StopCapturing();
        undoManager.Undo();
        Assert.AreEqual("a", text0.ToString());
        undoManager.Undo();
        Assert.AreEqual(string.Empty, text0.ToString());

        text0.Insert(0, "abc");
        text1.Insert(0, "xyz");
        Connector.SyncAll();
        undoManager.Undo();
        Assert.AreEqual("xyz", text0.ToString());
        undoManager.Redo();
        Assert.AreEqual("abcxyz", text0.ToString());
        Connector.SyncAll();
        text1.Remove(0, 1);
        Connector.SyncAll();
        undoManager.Undo();
        Assert.AreEqual("xyz", text0.ToString());
        undoManager.Redo();
        Assert.AreEqual("bcxyz", text0.ToString());

        // Test formats.
        text0.Format(1, 3, new Dictionary<string, YValue> { { "bold", true } });
        var delta = text0.ToDelta();
        Assert.AreEqual(3, delta?.Count);
        Assert.AreEqual("b", delta[0].Inserted());
        Assert.IsNull(delta[0].Attributes);
        Assert.AreEqual("cxy", delta[1].Inserted());
        Assert.AreEqual(true, delta[1].Attributes["bold"]);

        undoManager.Undo();
        delta = text0.ToDelta();
        Assert.AreEqual(1, delta?.Count);
        Assert.AreEqual("bcxyz", delta[0].Inserted());
        Assert.IsNull(delta[0].Attributes);

        undoManager.Redo();
        delta = text0.ToDelta();
        Assert.AreEqual(3, delta?.Count);
        Assert.AreEqual("b", delta[0].Inserted());
        Assert.IsNull(delta[0].Attributes);
        Assert.AreEqual("cxy", delta[1].Inserted());
        Assert.AreEqual(true, delta[1].Attributes["bold"]);
    }

    [TestMethod]
    public void TestDoubleUndo()
    {
        var doc = new YDoc();
        var text = doc.GetText();
        text.Insert(0, "1221");

        var undoManager = new UndoManager(text);

        text.Insert(2, "3");
        text.Insert(3, "3");

        undoManager.Undo();
        undoManager.Undo();

        text.Insert(2, "3");
        Assert.AreEqual("12321", text.ToString());
    }

    [TestMethod]
    public void TestUndoMap()
    {
        Init(users: 3);
        var map0 = Maps[Users[0]];
        var map1 = Maps[Users[1]];
        var undoManager = new UndoManager(map0);

        map0["a"] = 1;
        undoManager.Undo();
        Assert.IsFalse(map0.ContainsKey("a"));
        undoManager.Redo();
        Assert.AreEqual(1, map0["a"].ToObject());

        // Test subtypes and whether it can restore the whole type.
        var subType = new YMap();
        map0["a"] = subType;
        subType["x"] = 42;
        Assert.AreEqual(42, (map0["a"].ToObject() as YMap)?["x"].ToObject());
        undoManager.Undo();
        Assert.AreEqual(1, map0["a"].ToObject());
        undoManager.Redo();
        Assert.AreEqual(42, (map0["a"].ToObject() as YMap)?["x"].ToObject());
        Connector.SyncAll();

        // If content is overwritten by another user, undo operations should be skipped.
        map1["a"] = 44;
        Connector.SyncAll();
        undoManager.Undo();
        Assert.AreEqual(44, map0["a"].ToObject());
        undoManager.Redo();
        Assert.AreEqual(44, map0["a"].ToObject());

        // Test setting value multiple times.
        map0["b"] = "initial";
        undoManager.StopCapturing();
        map0["b"] = "val1";
        map0["b"] = "val2";
        undoManager.StopCapturing();
        undoManager.Undo();
        Assert.AreEqual("initial", map0["b"].ToObject());
    }

    [TestMethod]
    public void TestUndoArray()
    {
        Init(users: 3);
        var array0 = Arrays[Users[0]];
        var array1 = Arrays[Users[1]];
        var undoManager = new UndoManager(array0);

        array0.Insert(0, 1, 2, 3);
        array1.Insert(0, 4, 5, 6);
        Connector.SyncAll();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, array0.ToArray().Select(v => v.ToObject()).ToList());
        undoManager.Undo();
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, array0.ToArray().Select(v => v.ToObject()).ToList());
        undoManager.Redo();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, array0.ToArray().Select(v => v.ToObject()).ToList());
        Connector.SyncAll();
        // User1 deletes [1]
        array1.RemoveRange(0, 1);
        Connector.SyncAll();
        undoManager.Undo();
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, array0.ToArray().Select(v => v.ToObject()).ToList());
        undoManager.Redo();
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, array0.ToArray().Select(v => v.ToObject()).ToList());
        array0.RemoveRange(0, 5);

        // Test nested structure.
        var ymap = new YMap();
        array0.Insert(0, ymap);
        Assert.AreEqual(0, (array0[0].ToObject() as YMap)?.Count);
        undoManager.StopCapturing();
        ymap["a"] = 1;
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?.Count);
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?["a"].ToObject());
        undoManager.Undo();
        Assert.AreEqual(0, (array0[0].ToObject() as YMap)?.Count);
        undoManager.Undo();
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, array0.ToArray().Select(v => v.ToObject()).ToList());
        undoManager.Redo();
        Assert.AreEqual(0, (array0[0].ToObject() as YMap)?.Count);
        undoManager.Redo();
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?.Count);
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?["a"].ToObject());
        Connector.SyncAll();
        (array1[0].ToObject() as YMap)["b"] = 2;
        Connector.SyncAll();
        Assert.AreEqual(2, (array0[0].ToObject() as YMap)?.Count);
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?["a"].ToObject());
        Assert.AreEqual(2, (array0[0].ToObject() as YMap)?["b"].ToObject());
        undoManager.Undo();
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?.Count);
        Assert.AreEqual(2, (array0[0].ToObject() as YMap)?["b"].ToObject());
        undoManager.Undo();
        CollectionAssert.AreEqual(new[] { 2, 3, 4, 5, 6 }, array0.ToArray().Select(v => v.ToObject()).ToList());
        undoManager.Redo();
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?.Count);
        Assert.AreEqual(2, (array0[0].ToObject() as YMap)?["b"].ToObject());
        undoManager.Redo();
        Assert.AreEqual(2, (array0[0].ToObject() as YMap)?.Count);
        Assert.AreEqual(1, (array0[0].ToObject() as YMap)?["a"].ToObject());
        Assert.AreEqual(2, (array0[0].ToObject() as YMap)?["b"].ToObject());
    }

    [TestMethod]
    public void TestUndoEvents()
    {
        Init(users: 3);
        var text0 = Texts[Users[0]];
        var undoManager = new UndoManager(text0);

        int counter = 0;
        int receivedMetadata = -1;

        undoManager.StackItemAdded += (s, e) =>
        {
            Assert.IsTrue(e.ChangedParentTypes?.ContainsKey(text0) ?? false);
            e.StackItem.Meta["test"] = counter++;
        };

        undoManager.StackItemPopped += (s, e) =>
        {
            Assert.IsTrue(e.ChangedParentTypes?.ContainsKey(text0) ?? false);
            receivedMetadata = e.StackItem.Meta.TryGetValue("test", out var val) ? (int)val : -1;
        };

        text0.Insert(0, "abc");
        undoManager.Undo();
        Assert.AreEqual(0, receivedMetadata);
        undoManager.Redo();
        Assert.AreEqual(1, receivedMetadata);
    }

    [TestMethod]
    public void TestTrackClass()
    {
        Init(users: 3);
        var text0 = Texts[Users[0]];
        var undoManager = new UndoManager(text0, new UndoManagerOptions { TrackedOrigins = new HashSet<object> { typeof(int) } });

        Users[0].Transact(tr =>
        {
            text0.Insert(0, "abc");
        }, origin: 42);

        Assert.AreEqual("abc", text0.ToString());
        undoManager.Undo();
        Assert.AreEqual(string.Empty, text0.ToString());
    }

    [TestMethod]
    public void TestTypeScope()
    {
        Init(users: 3);
        var array0 = Arrays[Users[0]];

        var text0 = new YText();
        var text1 = new YText();
        array0.Insert(0, text0, text1);

        var undoManager = new UndoManager(text0);
        var undoManagerBoth = new UndoManager(new[] { text0, text1 });
        text1.Insert(0, "abc");
        Assert.AreEqual(0, undoManager.UndoStack.Count);
        Assert.AreEqual(1, undoManagerBoth.UndoStack.Count);
        Assert.AreEqual("abc", text1.ToString());
        undoManager.Undo();
        Assert.AreEqual("abc", text1.ToString());
        undoManagerBoth.Undo();
        Assert.AreEqual(string.Empty, text1.ToString());
    }

    [TestMethod]
    public void TestUndoDeleteFilter()
    {
        Init(users: 3);
        var array0 = Arrays[Users[0]];
        var undoManager = new UndoManager(array0, new UndoManagerOptions { DeleteFilter = it => it.Kind == ItemKind.Type && it.Content[0] is YType t && t._map.Count == 0 });
        var map0 = new YMap();
        map0["hi"] = 1;
        var map1 = new YMap();
        array0.Insert(0, map0, map1);
        undoManager.Undo();
        Assert.AreEqual(1, array0.Length);
        Assert.AreEqual(1, (array0[0].ToObject() as YMap).Keys.Count());
    }
}
