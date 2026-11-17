using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="ContentString"/> is the decoded form of a String item: a string to read, copy and write. A store item holds its
/// text in the arena, which splits, merges and copies it (<see cref="CharArenaTests"/>).
/// </summary>
[TestClass]
public class ContentStringTests
{
    [TestMethod]
    public void GetContent_WithSurrogatePair_OneStringPerCodeUnitAsYjs()
    {
        // Yjs: new ContentString('a😀').getContent() is ['a', '\ud83d', '\ude00'].
        var content = new ContentString("a😀");

        CollectionAssert.AreEqual(new object[] { "a", "\ud83d", "\ude00" }, content.GetContent().ToArray());
    }

    [TestMethod]
    public void Splice_Decoded_Throws()
    {
        var content = new ContentString("abcd");

        Assert.ThrowsExactly<NotSupportedException>(() => content.Splice(1));
    }

    [TestMethod]
    public void MergeWith_Decoded_Throws()
    {
        var content = new ContentString("ab");

        Assert.ThrowsExactly<NotSupportedException>(() => content.MergeWith(new ContentString("cd")));
    }

    [TestMethod]
    public void Copy_Decoded_IsAnotherObjectOfTheSameText()
    {
        var content = new ContentString("abc");

        var copy = (ContentString)content.Copy();

        Assert.IsTrue(copy.GetString() == "abc" && !ReferenceEquals(copy, content));
    }

    [TestMethod]
    public void CopyTo_Decoded_CopiesTheText()
    {
        var destination = new char[5];

        new ContentString("abc").CopyTo(destination);

        Assert.AreEqual("abc\0\0", new string(destination));
    }

    [TestMethod]
    public void TypedRun_SplitInTheMiddle_EncodesAsAFreshDocumentDecodesIt()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("text");
        for (var i = 0; i < 100; i++)
        {
            text.Insert(i, ((char)('a' + (i % 26))).ToString());
        }

        text.Insert(50, "|");
        var update = doc.EncodeStateAsUpdate();
        var copy = new YDoc(new YDocOptions { ClientId = 2 });
        copy.ApplyUpdate(update);

        Assert.AreEqual(text.ToString(), copy.GetText("text").ToString());
        Assert.AreEqual(Convert.ToBase64String(update), Convert.ToBase64String(copy.EncodeStateAsUpdate()));
        Assert.AreEqual(Convert.ToBase64String(doc.EncodeStateAsUpdate(format: UpdateFormat.V2)), Convert.ToBase64String(copy.EncodeStateAsUpdate(format: UpdateFormat.V2)));
    }

    [TestMethod]
    public void Write_Slice_WritesOnlyItsPart()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("text");
        text.Insert(0, "abcdef");
        text.Format(2, 2, new Dictionary<string, YValue> { ["bold"] = true });
        var copy = new YDoc(new YDocOptions { ClientId = 2 });

        copy.ApplyUpdate(doc.EncodeStateAsUpdate(format: UpdateFormat.V2), format: UpdateFormat.V2);

        Assert.AreEqual(Json(text.ToDelta()), Json(copy.GetText("text").ToDelta()));
    }

    private static string Json(IEnumerable<Delta> delta) => string.Join(",", delta.Select(d => $"{d.Inserted()}:{string.Join(";", d.Attributes?.Select(a => a.Key) ?? [])}"));
}
