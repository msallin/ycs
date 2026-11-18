using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YTextInsertMemoryTests
{
    private static YDoc Doc() => new(new YDocOptions { ClientId = 1 });

    [TestMethod]
    public void Insert_SliceOfLargerArray_SameTextAndUpdateAsString()
    {
        var big = ("0123456789 hello wörld " + new string('x', 5000) + " tail").ToCharArray();
        var slice = big.AsMemory(11, 4000);
        var a = Doc();
        var b = Doc();
        a.GetText("t").Insert(0, "ab");
        b.GetText("t").Insert(0, "ab");

        a.GetText("t").Insert(1, slice);
        b.GetText("t").Insert(1, new string(slice.Span));

        Assert.AreEqual(b.GetText("t").ToString(), a.GetText("t").ToString());
        CollectionAssert.AreEqual(b.EncodeStateAsUpdate(), a.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void Insert_MemoryIntoTextNotInDocument_AppliesWhenAdded()
    {
        var text = new YText();
        var source = "xxhelloxx".ToCharArray();

        text.Insert(0, source.AsMemory(2, 5));
        source.AsSpan().Fill('!');
        Assert.AreEqual("hello", text.ToString());

        var doc = Doc();
        doc.GetArray("a").Add(text);
        Assert.AreEqual("hello", text.ToString());
    }

    [TestMethod]
    public void Insert_MemoryWithAttributes_SameDeltaAsString()
    {
        var a = Doc();
        var b = Doc();
        YAttributes attrs = [new("bold", true)];

        a.GetText("t").Insert(0, "abcdef".AsMemory(1, 3), attrs);
        b.GetText("t").Insert(0, "bcd", attrs);

        CollectionAssert.AreEqual(b.EncodeStateAsUpdate(), a.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void Insert_SurrogatePairsAtTheEdges_SameUpdateAsString()
    {
        var source = "a😀b😀c";
        var a = Doc();
        var b = Doc();
        a.GetText("t").Insert(0, "XY");
        b.GetText("t").Insert(0, "XY");

        a.GetText("t").Insert(1, source.AsMemory(1, 4));
        b.GetText("t").Insert(1, source.Substring(1, 4));
        a.GetText("t").Insert(0, source.AsMemory(2, 2));
        b.GetText("t").Insert(0, source.Substring(2, 2));

        Assert.AreEqual(b.GetText("t").ToString(), a.GetText("t").ToString());
        CollectionAssert.AreEqual(b.EncodeStateAsUpdate(), a.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void Insert_EmptyMemory_ChangesNothing()
    {
        var doc = Doc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");

        text.Insert(1, ReadOnlyMemory<char>.Empty);
        text.Insert(1, "xyz".AsMemory(1, 0));

        Assert.AreEqual("abc", text.ToString());
    }
}
