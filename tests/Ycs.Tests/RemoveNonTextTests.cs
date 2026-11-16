using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class RemoveNonTextTests
{
    [TestMethod]
    public void EmbedsTypesAndFormatting_AreDeletedAndTheStringsStay()
    {
        // Written by Yjs: "ab" with a Y.Map embedded between the two (Ycs inserts no nested type into a text).
        var doc = new YDoc();
        doc.ApplyUpdate(Convert.FromBase64String("AQQBAAQBBHRleHQBYYQBAAFixwEAAQEBKAABAgFrAXcBdgA="));
        var text = doc.GetText("text");
        text.Insert(3, "c", new Dictionary<string, object> { ["bold"] = true });
        text.InsertEmbed(0, new Dictionary<string, object> { ["image"] = "x" });
        text.Format(0, 3, new Dictionary<string, object> { ["italic"] = true });

        var removed = text.RemoveNonText();

        Assert.IsTrue(removed);
        Assert.AreEqual("abc", text.ToString());
        Assert.AreEqual(3, text.Length);
        var delta = text.ToDelta();
        Assert.AreEqual(1, delta.Count);
        Assert.IsNull(delta[0].Attributes);
    }

    [TestMethod]
    public void TheDeletion_ReachesAPeer()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        text.Insert(0, "ab", new Dictionary<string, object> { ["bold"] = true });
        text.InsertEmbed(1, new Dictionary<string, object> { ["image"] = "x" });
        var peer = new YDoc();
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());
        var vector = peer.EncodeStateVector();

        text.RemoveNonText();
        peer.ApplyUpdate(doc.EncodeStateAsUpdate(vector));

        var peerText = peer.GetText("text");
        Assert.AreEqual(2, peerText.Length);
        Assert.IsNull(peerText.ToDelta().Single().Attributes);
    }

    [TestMethod]
    public void PlainText_IsLeftAloneWithoutAnUpdate()
    {
        var doc = new YDoc();
        var text = doc.GetText("text");
        text.Insert(0, "plain");
        text.Delete(1, 1);
        var updates = 0;
        doc.Updated += (_, _) => updates++;

        var removed = text.RemoveNonText();

        Assert.IsFalse(removed);
        Assert.AreEqual(0, updates);
        Assert.AreEqual("pain", text.ToString());
    }

    [TestMethod]
    public void IndexesAfterwards_AreStringIndexes()
    {
        // Search markers made while the embeds counted must not be used with their old indexes.
        var doc = new YDoc();
        var text = doc.GetText("text");
        var expected = new System.Text.StringBuilder();
        for (var i = 0; i < 50; i++)
        {
            text.Insert(text.Length, "0123456789");
            text.InsertEmbed(text.Length, new Dictionary<string, object> { ["n"] = i });
            expected.Append("0123456789");
        }

        for (var i = 0; i < 20; i++)
        {
            text.Insert(i * 23 % text.Length, "z");
            text.Delete(i * 23 % text.Length, 1);
        }

        text.RemoveNonText();
        text.Insert(455, "X");
        expected.Insert(455, "X");

        Assert.AreEqual(expected.ToString(), text.ToString());
    }

    [TestMethod]
    public void TextNotInADocument_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => new YText("a").RemoveNonText());
    }
}
