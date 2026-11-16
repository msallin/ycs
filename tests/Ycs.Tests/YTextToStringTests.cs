using System;
using System.Buffers;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="YText.ToString"/> keeps its result until the text changes; every way the text can
/// change must be seen. Also <see cref="YText.CopyTo"/> and <see cref="YText.WriteTo"/>.
/// </summary>
[TestClass]
public class YTextToStringTests
{
    [TestMethod]
    public void ToString_Unchanged_ReturnsTheSameString()
    {
        var text = NewText("hello");

        Assert.AreSame(text.ToString(), text.ToString());
    }

    [TestMethod]
    public void ToString_AfterLocalInsert_SeesIt()
    {
        var text = NewText("hello");
        _ = text.ToString();

        text.Insert(5, "!");

        Assert.AreEqual("hello!", text.ToString());
    }

    [TestMethod]
    public void ToString_AfterLocalDelete_SeesIt()
    {
        var text = NewText("hello");
        _ = text.ToString();

        text.Delete(0, 1);

        Assert.AreEqual("ello", text.ToString());
    }

    [TestMethod]
    public void ToString_DeleteAndInsertOfTheSameLengthInOneTransaction_SeesThem()
    {
        var text = NewText("hello");
        string inside = null;

        text.Doc.Transact(tr =>
        {
            _ = text.ToString();
            text.Delete(0, 1);
            text.Insert(0, "j");
            inside = text.ToString();
        });

        Assert.AreEqual("jello", inside);
    }

    [TestMethod]
    public void ToString_AfterRemoteInsertAndDelete_SeesThem()
    {
        var local = NewText("hello");
        var remote = new YDoc(new YDocOptions { ClientId = 2 });
        remote.ApplyUpdate(local.Doc.EncodeStateAsUpdate());
        var remoteText = remote.GetText("text");
        _ = local.ToString();

        remoteText.Insert(0, ">");
        local.Doc.ApplyUpdate(remote.EncodeStateAsUpdate(local.Doc.EncodeStateVector()));
        var afterInsert = local.ToString();
        remoteText.Delete(1, 2);
        local.Doc.ApplyUpdate(remote.EncodeStateAsUpdate(local.Doc.EncodeStateVector()));

        Assert.AreEqual(">hello", afterInsert);
        Assert.AreEqual(">llo", local.ToString());
    }

    [TestMethod]
    public void ToString_AfterUndoAndRedo_SeesThem()
    {
        var text = NewText("");
        var undo = new UndoManager(text);
        text.Insert(0, "abc");
        _ = text.ToString();

        undo.Undo();
        var undone = text.ToString();
        undo.Redo();

        Assert.AreEqual("", undone);
        Assert.AreEqual("abc", text.ToString());
    }

    [TestMethod]
    public void ToString_AfterGarbageCollectedDelete_SeesIt()
    {
        var text = NewText("abcdef");
        text.Insert(3, "XYZ");
        _ = text.ToString();

        text.Delete(2, 5);

        Assert.AreEqual("abef", text.ToString());
        Assert.AreEqual("abef", Reloaded(text.Doc).ToString());
    }

    [TestMethod]
    public void ToString_SplitOfASurrogatePairBySnapshot_SeesTheReplacementCharacters()
    {
        // "a" + high surrogate, a snapshot, then the low surrogate merged into the same item. Reading
        // the snapshot splits the item between the two halves, which replaces both: the only change
        // to the text that adds and deletes nothing.
        var doc = new YDoc(new YDocOptions { ClientId = 1, Gc = false });
        var text = doc.GetText("text");
        text.Insert(0, "a\uD83D");
        var snapshot = doc.CreateSnapshot();
        text.Insert(2, "\uDE00");
        Assert.AreEqual("a😀", text.ToString());

        text.ToDelta(snapshot);

        Assert.AreEqual("a��", text.ToString());
    }

    [TestMethod]
    public void ToString_AfterFormat_IsUnchanged()
    {
        var text = NewText("hello");
        _ = text.ToString();

        text.Format(1, 2, new Dictionary<string, object> { ["bold"] = true });

        Assert.AreEqual("hello", text.ToString());
    }

    [TestMethod]
    public void ToString_WithEmbed_LeavesItOut()
    {
        var text = NewText("ab");

        text.InsertEmbed(1, new Dictionary<string, object> { ["image"] = "x" });

        Assert.AreEqual("ab", text.ToString());
    }

    [TestMethod]
    public void CopyTo_LengthLongDestination_WritesTheTextWithoutEmbeds()
    {
        var text = NewText("ab");
        text.InsertEmbed(1, new Dictionary<string, object> { ["image"] = "x" });
        var destination = new char[text.Length];

        var written = text.CopyTo(destination);

        Assert.AreEqual("ab", new string(destination, 0, written));
    }

    [TestMethod]
    public void CopyTo_TooShortDestination_Throws()
    {
        var text = NewText("abc");

        Assert.ThrowsExactly<ArgumentException>(() => text.CopyTo(new char[2]));
    }

    [TestMethod]
    public void WriteTo_BufferWriter_WritesTheText()
    {
        var text = NewText("abc");
        text.Insert(1, "XY");
        var writer = new ArrayBufferWriter<char>(1);

        text.WriteTo(writer);

        Assert.AreEqual("aXYbc", new string(writer.WrittenSpan));
    }

    private static YText NewText(string content)
    {
        var text = new YDoc(new YDocOptions { ClientId = 1 }).GetText("text");
        text.Insert(0, content);
        return text;
    }

    private static YText Reloaded(YDoc doc)
    {
        var copy = new YDoc(new YDocOptions { ClientId = 9 });
        copy.ApplyUpdate(doc.EncodeStateAsUpdate());
        return copy.GetText("text");
    }
}
