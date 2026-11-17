using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Ports of the Yjs 13.6.32 undo-redo tests of XML, embeds, text formats and redone nested types (tests/undo-redo.tests.js).</summary>
[TestClass]
public class UndoManagerXmlEmbedTests
{
    private static readonly UndoManagerOptions NoMerge = new() { CaptureTimeout = TimeSpan.Zero };

    private static YMap MapOf(YValue value) => value.TryGetSharedType<YMap>(out var map) ? map! : throw new AssertFailedException("not a map");

    [TestMethod]
    public void Undo_XmlTextFormat_RevertsAndRestoresTheFormatAndTheDeletedElement()
    {
        // testUndoXml. Yjs names the root fragment's node "undefined"; Ycs prints the children only.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var xml = doc.GetXmlFragment("xml");
        var undoManager = new UndoManager((YType)xml);
        var child = new YXmlElement("p");
        xml.Insert(0, [child]);
        var textChild = new YXmlText("content");
        child.Insert(0, [textChild]);
        var steps = new List<string> { xml.ToString() };

        undoManager.StopCapturing();
        textChild.Format(3, 4, new Dictionary<string, YValue> { ["bold"] = YValue.Object([]) });
        steps.Add(xml.ToString());
        undoManager.Undo();
        steps.Add(xml.ToString());
        undoManager.Redo();
        steps.Add(xml.ToString());
        xml.RemoveAt(0);
        steps.Add(xml.ToString());
        undoManager.Undo();
        steps.Add(xml.ToString());

        CollectionAssert.AreEqual(
            new[]
            {
                "<p>content</p>",
                "<p>con<bold>tent</bold></p>",
                "<p>content</p>",
                "<p>con<bold>tent</bold></p>",
                string.Empty,
                "<p>con<bold>tent</bold></p>",
            },
            steps);
    }

    [TestMethod]
    public void Undo_TextEmbedOfANestedText_UndoesTheNestedEditThenTheEmbed()
    {
        // testUndoInEmbed
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        var undoManager = new UndoManager(text);
        var nested = new YText("initial text");
        undoManager.StopCapturing();
        text.InsertEmbed(0, nested, new Dictionary<string, YValue> { ["bold"] = true });
        var inserted = nested.ToString();
        undoManager.StopCapturing();
        nested.Remove(0, nested.Length);
        nested.Insert(0, "other text");
        var edited = nested.ToString();

        undoManager.Undo();
        var afterFirstUndo = nested.ToString();
        undoManager.Undo();

        Assert.AreEqual(("initial text", "other text", "initial text", 0), (inserted, edited, afterFirstUndo, text.Length));
    }

    [TestMethod]
    public void Redo_TextEmbedOfANestedText_RestoresTheEmbedWithItsContent()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        var undoManager = new UndoManager(text);
        undoManager.StopCapturing();
        text.InsertEmbed(0, new YText("initial text"), new Dictionary<string, YValue> { ["bold"] = true });
        undoManager.Undo();

        undoManager.Redo();

        text.ToDelta()[0].Inserted().TryGetSharedType<YText>(out var restored);
        Assert.AreEqual((1, "initial text"), (text.Length, restored?.ToString()));
    }

    [TestMethod]
    public void Undo_FormatDeletion_KeepsTheFormatOnBothPeers()
    {
        // testUndoDeleteTextFormat (issue 392): undoing the removal of a bold range must not corrupt the peer.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "Attack ships on fire off the shoulder of Orion.");
        var peer = new YDoc(new YDocOptions { ClientId = 2 });
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());
        var undoManager = new UndoManager(text);

        text.Format(13, 7, new Dictionary<string, YValue> { ["bold"] = true });
        undoManager.StopCapturing();
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());
        text.Format(16, 4, new Dictionary<string, YValue> { ["bold"] = YValue.Null });
        undoManager.StopCapturing();
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());
        undoManager.Undo();
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());

        const string expected = """[{"insert":"Attack ships "},{"attributes":{"bold":true},"insert":"on fire"},{"insert":" off the shoulder of Orion."}]""";
        Assert.AreEqual((expected, expected), (CanonicalJson.Write(text.ToDelta()), CanonicalJson.Write(peer.GetText("t").ToDelta())));
    }

    [TestMethod]
    public void Undo_AttributeChangeAndElementDeletionInOneTransaction_RestoresTheOriginalAttributes()
    {
        // testSpecialDeletionCase (issue 447)
        const string origin = "undoable";
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var fragment = doc.GetXmlFragment("f");
        var undoManager = new UndoManager((YType)fragment, new UndoManagerOptions { TrackedOrigins = new HashSet<object> { origin } });
        doc.Transact(_ =>
        {
            var e = new YXmlElement("test");
            e.SetAttribute("a", "1");
            e.SetAttribute("b", "2");
            fragment.Insert(0, [e]);
        });
        var created = fragment.ToString();
        doc.Transact(_ =>
        {
            ((YXmlElement)fragment[0]).SetAttribute("b", "3");
            fragment.RemoveAt(0);
        }, origin);
        var deleted = fragment.ToString();

        undoManager.Undo();

        Assert.AreEqual(("<test a=\"1\" b=\"2\"></test>", string.Empty, "<test a=\"1\" b=\"2\"></test>"), (created, deleted, fragment.ToString()));
    }

    [TestMethod]
    public void UndoRedo_ReplacedNestedMapsWithoutGc_WalkBothWays()
    {
        // testUndoNestedUndoIssue (issue 317)
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var design = doc.GetMap("design");
        var undoManager = new UndoManager(design, NoMerge);
        var text = new YMap();
        doc.Transact(_ =>
        {
            var blocks = new YArray();
            var block = new YMap { ["text"] = "Type Something" };
            blocks.Add([block]);
            text["blocks"] = block;
            design["text"] = text;
        });
        foreach (var content in new[] { "Something", "Something Else" })
        {
            doc.Transact(_ =>
            {
                var blocks = new YArray();
                var block = new YMap { ["text"] = content };
                blocks.Add([block]);
                text["blocks"] = block;
            });
        }

        var states = new List<string> { design.ToJsonString() };
        for (var i = 0; i < 3; i++)
        {
            undoManager.Undo();
            states.Add(design.ToJsonString());
        }

        for (var i = 0; i < 3; i++)
        {
            undoManager.Redo();
            states.Add(design.ToJsonString());
        }

        CollectionAssert.AreEqual(
            new[]
            {
                """{"text":{"blocks":{"text":"Something Else"}}}""",
                """{"text":{"blocks":{"text":"Something"}}}""",
                """{"text":{"blocks":{"text":"Type Something"}}}""",
                "{}",
                """{"text":{"blocks":{"text":"Type Something"}}}""",
                """{"text":{"blocks":{"text":"Something"}}}""",
                """{"text":{"blocks":{"text":"Something Else"}}}""",
            },
            states);
    }

    [TestMethod]
    public void Redo_FourReplacedNestedMapsWithoutGc_EndsAtTheLastOne()
    {
        // testUndoBlockBug (issue 343)
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var design = doc.GetMap("design");
        var undoManager = new UndoManager(design, NoMerge);
        var text = new YMap();
        var first = true;
        foreach (var content in new[] { "1", "2", "3", "4" })
        {
            doc.Transact(_ =>
            {
                var blocks = new YArray();
                var block = new YMap { ["text"] = content };
                blocks.Add([block]);
                text["blocks"] = block;
                if (first)
                {
                    design["text"] = text;
                    first = false;
                }
            });
        }

        for (var i = 0; i < 4; i++)
        {
            undoManager.Undo();
        }

        for (var i = 0; i < 4; i++)
        {
            undoManager.Redo();
        }

        Assert.AreEqual("""{"text":{"blocks":{"text":"4"}}}""", design.ToJsonString());
    }

    [TestMethod]
    public void Redo_PointRecreatedByUndoAndRedone_EndsAtTheLastValues()
    {
        // testConsecutiveRedoBug (issue 355)
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var root = doc.GetMap("root");
        var undoManager = new UndoManager(root);
        var point = new YMap { ["x"] = 0, ["y"] = 0 };
        root["a"] = point;
        undoManager.StopCapturing();
        foreach (var v in new[] { 100, 200, 300 })
        {
            point["x"] = v;
            point["y"] = v;
            undoManager.StopCapturing();
        }

        var states = new List<string> { point.ToJsonString() };
        for (var i = 0; i < 3; i++)
        {
            undoManager.Undo();
            states.Add(point.ToJsonString());
        }

        undoManager.Undo();
        var removed = !root.ContainsKey("a");
        for (var i = 0; i < 4; i++)
        {
            undoManager.Redo();
            states.Add(MapOf(root["a"]).ToJsonString());
        }

        CollectionAssert.AreEqual(
            new[]
            {
                """{"x":300,"y":300}""",
                """{"x":200,"y":200}""",
                """{"x":100,"y":100}""",
                """{"x":0,"y":0}""",
                """{"x":0,"y":0}""",
                """{"x":100,"y":100}""",
                """{"x":200,"y":200}""",
                """{"x":300,"y":300}""",
            },
            states);
        Assert.IsTrue(removed);
    }

    [TestMethod]
    public void UndoRedo_XmlAttributeEditsOfTrackedTransactions_EndAtTheLastAttributes()
    {
        // testUndoXmlBug (issue 304)
        const string origin = "origin";
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var fragment = doc.GetXmlFragment("t");
        var undoManager = new UndoManager((YType)fragment, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero, TrackedOrigins = new HashSet<object> { origin } });
        doc.Transact(_ =>
        {
            var e = new YXmlElement("test-node");
            e.SetAttribute("a", "100");
            e.SetAttribute("b", "0");
            fragment.Insert(fragment.Count, [e]);
        }, origin);
        doc.Transact(_ => ((YXmlElement)fragment[0]).SetAttribute("a", "200"), origin);
        doc.Transact(_ =>
        {
            var e = (YXmlElement)fragment[0];
            e.SetAttribute("a", "180");
            e.SetAttribute("b", "50");
        }, origin);

        for (var i = 0; i < 3; i++)
        {
            undoManager.Undo();
        }

        for (var i = 0; i < 3; i++)
        {
            undoManager.Redo();
        }

        Assert.AreEqual("<test-node a=\"180\" b=\"50\"></test-node>", fragment.ToString());
    }
}
