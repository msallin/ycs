using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="ContentString"/> is a string while it is short or never grew, and a StringBuilder once
/// merges make it long; splitting and merging behave the same in both forms, as in Yjs.
/// </summary>
[TestClass]
public class ContentStringTests
{
    [TestMethod]
    public void Splice_String_SplitsAtOffset()
    {
        var left = new ContentString("abcd");

        var right = (ContentString)left.Splice(1);

        Assert.AreEqual("a|bcd", left.GetString() + "|" + right.GetString());
    }

    [TestMethod]
    public void Splice_Builder_SplitsAtOffset()
    {
        var left = Grown(40);

        var right = (ContentString)left.Splice(10);

        Assert.AreEqual(new string('x', 10) + "|" + new string('x', 30), left.GetString() + "|" + right.GetString());
    }

    [TestMethod]
    public void Splice_StringInsideSurrogatePair_ReplacesBothHalves()
    {
        var left = new ContentString("a😀b");

        var right = (ContentString)left.Splice(2);

        Assert.AreEqual("a�|�b", left.GetString() + "|" + right.GetString());
    }

    [TestMethod]
    public void Splice_BuilderInsideSurrogatePair_ReplacesBothHalves()
    {
        var left = Grown(40);
        left.MergeWith(new ContentString("😀b"));

        var right = (ContentString)left.Splice(41);

        Assert.AreEqual(new string('x', 40) + "�|�b", left.GetString() + "|" + right.GetString());
    }

    [TestMethod]
    public void Splice_AfterLowSurrogate_KeepsThePair()
    {
        var left = new ContentString("a😀b");

        var right = (ContentString)left.Splice(3);

        Assert.AreEqual("a😀|b", left.GetString() + "|" + right.GetString());
    }

    [TestMethod]
    public void MergeWith_ShortRuns_Concatenates()
    {
        var left = new ContentString("ab");

        left.MergeWith(new ContentString("cd"));

        Assert.AreEqual("abcd", left.GetString());
    }

    [TestMethod]
    public void MergeWith_PastTheThreshold_KeepsEveryCharacter()
    {
        var left = new ContentString("");
        var expected = "";
        for (var i = 0; i < 100; i++)
        {
            var c = ((char)('a' + (i % 26))).ToString();
            left.MergeWith(new ContentString(c));
            expected += c;
        }

        Assert.AreEqual(expected, left.GetString());
        Assert.AreEqual(100, left.Length);
    }

    [TestMethod]
    public void MergeWith_BuilderIntoString_AppendsIt()
    {
        var left = new ContentString("ab");

        left.MergeWith(Grown(40));

        Assert.AreEqual("ab" + new string('x', 40), left.GetString());
    }

    [TestMethod]
    public void CopyTo_Builder_CopiesTheText()
    {
        var content = Grown(40);
        var destination = new char[40];

        content.CopyTo(destination);

        Assert.AreEqual(new string('x', 40), new string(destination));
    }

    [TestMethod]
    public void Copy_Builder_IsIndependent()
    {
        var content = Grown(40);

        var copy = (ContentString)content.Copy();
        content.MergeWith(new ContentString("y"));

        Assert.AreEqual(new string('x', 40), copy.GetString());
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
        var update = doc.EncodeStateAsUpdateV1();
        var copy = new YDoc(new YDocOptions { ClientId = 2 });
        copy.ApplyUpdateV1(update);

        Assert.AreEqual(text.ToString(), copy.GetText("text").ToString());
        Assert.AreEqual(Convert.ToBase64String(update), Convert.ToBase64String(copy.EncodeStateAsUpdateV1()));
        Assert.AreEqual(Convert.ToBase64String(doc.EncodeStateAsUpdateV2()), Convert.ToBase64String(copy.EncodeStateAsUpdateV2()));
    }

    private static ContentString Grown(int length)
    {
        var content = new ContentString("x");
        for (var i = 1; i < length; i++)
        {
            content.MergeWith(new ContentString("x"));
        }

        return content;
    }
}
