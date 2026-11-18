using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="YText.Chunks()"/> reads the text in place: the whole text, a range of it, from any shape of item list,
/// without a transaction and without changing the store, and as the text is when each step runs.
/// </summary>
[TestClass]
public class YTextChunksTests
{
    private static string Read(YText text)
    {
        var result = new StringBuilder();
        foreach (var chunk in text.Chunks())
        {
            Assert.IsGreaterThan(0, chunk.Length);
            result.Append(chunk);
        }

        return result.ToString();
    }

    private static string Read(YText text, int start, int length)
    {
        var result = new StringBuilder();
        foreach (var chunk in text.Chunks(start, length))
        {
            Assert.IsGreaterThan(0, chunk.Length);
            result.Append(chunk);
        }

        return result.ToString();
    }

    private static int CountChunks(YText text)
    {
        var count = 0;
        foreach (var _ in text.Chunks())
        {
            count++;
        }

        return count;
    }

    /// <summary>Two clients typing into one text, then an embed, a nested type, a format and a delete among them.</summary>
    private static YText Mixed()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var text = a.GetText("t");
        text.Insert(0, "abcdef");
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetText("t").Insert(3, "XY");
        text.Insert(3, "pq");
        a.ApplyUpdate(b.EncodeStateAsUpdate());
        text.InsertEmbed(2, (YValue)5);
        text.InsertEmbed(7, new YMap());
        text.Format(1, 6, ("bold", true));
        text.Insert(text.Length, "end");
        text.Remove(9, 2);
        return text;
    }

    [TestMethod]
    public void Chunks_OneItem_YieldsTheTextAsOneChunk()
    {
        var text = new YDoc().GetText("t");
        text.Insert(0, "hello");

        Assert.AreEqual((1, "hello"), (CountChunks(text), Read(text)));
    }

    [TestMethod]
    public void Chunks_EmptyText_YieldsNothing()
    {
        var text = new YDoc().GetText("t");

        Assert.AreEqual((0, string.Empty), (CountChunks(text), Read(text, 0, 0)));
    }

    [TestMethod]
    public void Chunks_ManyItemsOfTwoClients_EqualToString()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        for (var i = 0; i < 40; i++)
        {
            var doc = i % 2 == 0 ? a : b;
            var text = doc.GetText("t");
            text.Insert(text.Length / 2, i.ToString());
            (i % 2 == 0 ? b : a).ApplyUpdate(doc.EncodeStateAsUpdate());
        }

        var result = a.GetText("t");
        Assert.AreEqual((result.ToString(), true), (Read(result), CountChunks(result) > 5));
    }

    [TestMethod]
    public void Chunks_EmbedsNestedTypesAndFormats_YieldTheCharactersOnly()
    {
        var text = Mixed();

        Assert.AreEqual(text.ToString(), Read(text));
    }

    [TestMethod]
    public void Chunks_DetachedText_YieldsWhatWasInserted()
    {
        var text = new YText("hello");
        text.Insert(5, " world");

        Assert.AreEqual(("hello world", "lo w"), (Read(text), Read(text, 3, 4)));
    }

    [TestMethod]
    public void Chunks_DetachedTextWithoutContent_YieldsNothing()
    {
        Assert.AreEqual(string.Empty, Read(new YText()));
    }

    [TestMethod]
    public void Chunks_EveryRangeOfAMixedText_EqualsSubstring()
    {
        var text = Mixed();
        var mismatches = new List<string>();

        for (var start = 0; start <= text.Length; start++)
        {
            for (var length = 0; start + length <= text.Length; length++)
            {
                if (Read(text, start, length) != text.Substring(start, length))
                {
                    mismatches.Add($"{start},{length}");
                }
            }
        }

        Assert.AreEqual(string.Empty, string.Join(" ", mismatches));
    }

    [TestMethod]
    [DataRow(-1, 1)]
    [DataRow(0, -1)]
    [DataRow(3, 3)]
    [DataRow(6, 1)]
    [DataRow(int.MaxValue, 1)]
    [DataRow(1, int.MaxValue)]
    public void Chunks_RangeOutsideTheText_Throws(int start, int length)
    {
        var text = new YDoc().GetText("t");
        text.Insert(0, "hello");

        Assert.Throws<ArgumentOutOfRangeException>(() => text.Chunks(start, length));
    }

    [TestMethod]
    public void Chunks_RangeBeyondTheLiteralSteps_JumpsToIt()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        doc.LiteralWalkStepsForTests = 3;
        var text = doc.GetText("t");
        var builder = new StringBuilder();
        for (var i = 0; i < 60; i++)
        {
            text.Insert(text.Length, "x");
            builder.Append('x');
            text.Insert(0, "y");
            builder.Insert(0, 'y');
        }

        text.Remove(10, 5);
        builder.Remove(10, 5);
        var expected = builder.ToString();

        Assert.AreEqual(
            (expected.Substring(80, 25), expected.Substring(3, 100), expected.Substring(105)),
            (Read(text, 80, 25), Read(text, 3, 100), Read(text, 105, text.Length - 105)));
    }

    [TestMethod]
    public void Chunks_RangeOnATextWithFormatsAndTheJump_EqualsSubstring()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        doc.LiteralWalkStepsForTests = 2;
        var text = doc.GetText("t");
        for (var i = 0; i < 30; i++)
        {
            text.Insert(0, i.ToString());
        }

        text.Format(5, 20, ("bold", true));
        text.Format(30, 10, ("italic", true));
        var mismatches = new List<string>();

        for (var start = 0; start < text.Length; start += 7)
        {
            var length = Math.Min(23, text.Length - start);
            if (Read(text, start, length) != text.Substring(start, length))
            {
                mismatches.Add($"{start},{length}");
            }
        }

        Assert.AreEqual(string.Empty, string.Join(" ", mismatches));
    }

    [TestMethod]
    public void Chunks_Read_StartsNoTransactionAndKeepsTheStoreVersion()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = Mixed().Doc.GetText("t");
        doc.ApplyUpdate(text.Doc.EncodeStateAsUpdate());
        var inner = doc.GetText("t");
        var transactions = 0;
        doc.BeforeTransaction += (_, _) => transactions++;
        var version = doc.Store.Version;

        _ = Read(inner, 3, 5);
        _ = Read(inner);

        Assert.AreEqual((0, version), (transactions, doc.Store.Version));
    }

    [TestMethod]
    public void Chunks_Read_KeepsTheCachedString()
    {
        var text = Mixed();
        var cached = text.ToString();

        _ = Read(text, 3, 5);

        Assert.AreSame(cached, text.ToString());
    }

    [TestMethod]
    public void Chunks_Read_AllocatesNothing()
    {
        var text = Mixed();
        var sink = 0;
        sink += Walk(text);
        sink += WalkRange(text);

        var before = GC.GetAllocatedBytesForCurrentThread();
        sink += Walk(text);
        sink += WalkRange(text);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual((0L, true), (allocated, sink > 0));
    }

    private static int Walk(YText text)
    {
        var sum = 0;
        foreach (var chunk in text.Chunks())
        {
            sum += chunk.Length;
        }

        return sum;
    }

    private static int WalkRange(YText text)
    {
        var sum = 0;
        foreach (var chunk in text.Chunks(2, 9))
        {
            sum += chunk.Length;
        }

        return sum;
    }

    [TestMethod]
    public void Chunks_BodyDeletesTheRestOfTheText_YieldsNothingMore()
    {
        var text = ThreeItems();
        var seen = new List<string>();

        foreach (var chunk in text.Chunks())
        {
            seen.Add(chunk.ToString());
            text.Remove(0, text.Length);
        }

        Assert.AreEqual("abcd", string.Join(",", seen));
    }

    [TestMethod]
    public void Chunks_BodySplitsTheNextItem_YieldsItsCurrentText()
    {
        var text = ThreeItems();
        var seen = new List<string>();

        foreach (var chunk in text.Chunks())
        {
            seen.Add(chunk.ToString());
            if (seen.Count == 1)
            {
                text.Insert(6, "!");
            }
        }

        Assert.AreEqual("abcd,wx,!,yz,tail", string.Join(",", seen));
    }

    [TestMethod]
    public void Chunks_BodySplitsTheItemBeforeTheCursor_ReadsOnFromTheCursor()
    {
        var text = ThreeItems();
        var seen = new List<string>();

        foreach (var chunk in text.Chunks(1, 7))
        {
            seen.Add(chunk.ToString());
            if (seen.Count == 1)
            {
                text.Insert(2, "!");
            }
        }

        Assert.AreEqual("bcd,wxyz", string.Join(",", seen));
    }

    [TestMethod]
    public void Chunks_BodyInsertsBeforeTheCursor_DoesNotReadItAgain()
    {
        var text = ThreeItems();
        var seen = new List<string>();

        foreach (var chunk in text.Chunks())
        {
            seen.Add(chunk.ToString());
            if (seen.Count == 1)
            {
                text.Insert(0, "<<");
            }
        }

        Assert.AreEqual("abcd,wxyz,tail", string.Join(",", seen));
    }

    [TestMethod]
    public void Chunks_BodyEditsInsideAnObserver_ReadsOnAsTheTextIs()
    {
        var text = ThreeItems();
        var seen = new List<string>();

        text.Doc.Transact(_ =>
        {
            foreach (var chunk in text.Chunks())
            {
                seen.Add(chunk.ToString());
                if (seen.Count == 1)
                {
                    text.Remove(4, 4);
                }
            }
        });

        Assert.AreEqual("abcd,tail", string.Join(",", seen));
    }

    /// <summary>"abcd" of client 1, "wxyz" of client 2 and "tail" of client 3, in this order, as three items.</summary>
    private static YText ThreeItems()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        a.GetText("t").Insert(0, "abcd");
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetText("t").Insert(4, "wxyz");
        var c = new YDoc(new YDocOptions { ClientId = 3 });
        c.ApplyUpdate(b.EncodeStateAsUpdate());
        c.GetText("t").Insert(8, "tail");
        a.ApplyUpdate(c.EncodeStateAsUpdate());
        return a.GetText("t");
    }
}
