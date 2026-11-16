using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// What the enumerators hold across the caller's code (docs/design-item-layout.md, 3.2): the id of the struct they
/// stand on, so that a cleanup that merges it away or a collection of its type does not leave them on a struct the
/// store has dropped. Where Yjs' orphaned item continues, so do they; the values below are what Yjs 13.6.32 gives
/// (interop/properties/node_modules, the scripts in each test's comment), except where a test says it differs.
/// </summary>
[TestClass]
public class EnumeratorLifetimeTests
{
    /// <summary>
    /// A (client 1) pushes a b c d; B (client 2) pushes z; A deletes "b". The list is then I=[a] J=[b, deleted] K=[c d] Z=[z]:
    /// deleting c and d makes K merge into J when the transaction of the delete ends.
    /// </summary>
    private static (YDoc A, YArray Array) Split()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var array = a.GetArray("a");
        array.Push(new object[] { "a", "b", "c", "d" });
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetArray("a").Push(new object[] { "z" });
        a.ApplyUpdate(b.EncodeStateAsUpdate());
        array.Delete(1);
        return (a, array);
    }

    /// <summary>B's item "y" after "d", seen by a document that has everything up to "z" but "y".</summary>
    private static byte[] InsertAfterD(YDoc a)
    {
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetArray("a").Insert(3, new object[] { "y" });
        return b.EncodeStateAsUpdate(a.EncodeStateVectorV1());
    }

    [TestMethod]
    public void Enumerate_ItemMergedAwayWhileItsContentIsRead_ContinuesWithTheNextItem()
    {
        // for (const v of arr) { seen.push(v); if (v === 'c') arr.delete(1, 2) } -> ["a","c","d","z"]
        var (doc, array) = Split();
        var departures = doc.Store.Departures;
        var seen = new List<object>();

        foreach (var value in array)
        {
            seen.Add(value);
            if (value.Equals("c"))
            {
                array.Delete(1, 2);
            }
        }

        Assert.AreEqual((true, "a,c,d,z"), (doc.Store.Departures > departures, string.Join(",", seen)));
    }

    [TestMethod]
    public void Enumerate_ItemInsertedRightOfTheStructThatHoldsTheId_VisitsIt()
    {
        // Differs from Yjs, whose orphaned item continues with its old right neighbour: Yjs gives ["a","c","d","z"].
        var (doc, array) = Split();
        var inserted = InsertAfterD(doc);
        var seen = new List<object>();

        foreach (var value in array)
        {
            seen.Add(value);
            if (value.Equals("c"))
            {
                array.Delete(1, 2);
                doc.ApplyUpdate(inserted);
            }
        }

        Assert.AreEqual("a,c,d,y,z", string.Join(",", seen));
    }

    [TestMethod]
    public void MapAndForEach_ItemMergedAwayInTheCallback_ContinueWithTheNextItem()
    {
        var (doc, array) = Split();
        var seen = new List<string>();
        array.ForEach((value, _, _) =>
        {
            seen.Add((string)value);
            if (value.Equals("c"))
            {
                array.Delete(1, 2);
            }
        });
        var (_, second) = Split();
        var mapped = second.Map((value, _, _) =>
        {
            if (value.Equals("c"))
            {
                second.Delete(1, 2);
            }

            return (string)value;
        });

        Assert.AreEqual("a,c,d,z|a,c,d,z", string.Join(",", seen) + "|" + string.Join(",", mapped));
    }

    [TestMethod]
    public void Enumerate_TypeCollectedWhileItsContentIsRead_EndsAfterTheContentInHand()
    {
        // for (const v of arr) { seen.push(v); if (v === 'x') m.delete('k') } -> ["x"], with "y" of the other client gone
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var map = doc.GetMap("m");
        var array = new YArray();
        map.Set("k", array);
        array.Push(new object[] { "x" });
        var other = new YDoc(new YDocOptions { ClientId = 2 });
        other.ApplyUpdate(doc.EncodeStateAsUpdate());
        ((YArray)other.GetMap("m").Get("k")).Push(new object[] { "y" });
        doc.ApplyUpdate(other.EncodeStateAsUpdate());
        var seen = new List<object>();

        foreach (var value in array)
        {
            seen.Add(value);
            map.Delete("k");
        }

        Assert.AreEqual((0, "x"), (array.Length, string.Join(",", seen)));
    }

    [TestMethod]
    public void ForEachOfAFragment_TypeCollectedInTheCallback_Ends()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var map = doc.GetMap("m");
        var fragment = new YXmlFragment();
        map.Set("k", fragment);
        fragment.Insert(0, new[] { new YXmlElement("a") });
        fragment.Insert(1, new[] { new YXmlElement("b") });
        var visited = 0;

        fragment.ForEach((_, _, _) =>
        {
            visited++;
            map.Delete("k");
        });

        Assert.AreEqual(1, visited);
    }

    [TestMethod]
    public void WriteTo_ItemMergedAwayWhileTheWriterRuns_ContinuesWithTheNextItem()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var text = a.GetText("t");
        text.Insert(0, "abcd");
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetText("t").Insert(4, "z");
        a.ApplyUpdate(b.EncodeStateAsUpdate());
        text.Delete(1, 1);
        var writer = new CallbackWriter(onSpan: count =>
        {
            if (count == 2)
            {
                text.Delete(1, 2);
            }
        });

        text.WriteTo(writer);

        Assert.AreEqual("acdz", new string(writer.Written.ToArray()));
    }

    [TestMethod]
    public void TreeWalker_StandingInACollectedType_Ends()
    {
        // Differs from Yjs, whose orphaned node walks on through its old parent to <e3>: Yjs gives e1, c, e3.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var fragment = doc.GetXmlFragment("x");
        var e1 = new YXmlElement("e1");
        e1.Insert(0, new[] { new YXmlElement("c") });
        fragment.Insert(0, new[] { e1, new YXmlElement("e3") });
        var seen = new List<string>();
        var walker = fragment.CreateTreeWalker();

        while (walker.MoveNext())
        {
            seen.Add(((YXmlElement)walker.Current).NodeName);
            if (walker.Current is YXmlElement { NodeName: "c" })
            {
                fragment.Delete(0, 1);
            }
        }

        Assert.AreEqual("e1,c", string.Join(",", seen));
    }

    [TestMethod]
    public void TreeWalker_StructsLeftThatAreNotItsNode_KeepsWalking()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var fragment = doc.GetXmlFragment("x");
        fragment.Insert(0, new[] { new YXmlElement("a"), new YXmlElement("b"), new YXmlElement("c") });
        var seen = new List<string>();
        var walker = fragment.CreateTreeWalker();

        while (walker.MoveNext())
        {
            seen.Add(((YXmlElement)walker.Current).NodeName);
            var other = doc.GetText("t");
            other.Insert(0, "xy");
            other.Delete(0, 1);
            other.Delete(0, 1);
        }

        Assert.AreEqual("a,b,c", string.Join(",", seen));
    }

    private sealed class CallbackWriter(Action<int> onSpan) : IBufferWriter<char>
    {
        private readonly List<char> _written = new();
        private char[] _span = Array.Empty<char>();
        private int _spans;

        public IReadOnlyList<char> Written => _written;

        public void Advance(int count) => _written.AddRange(_span.Take(count));

        public Memory<char> GetMemory(int sizeHint = 0) => _span = new char[Math.Max(sizeHint, 1)];

        public Span<char> GetSpan(int sizeHint = 0)
        {
            onSpan(++_spans);
            return GetMemory(sizeHint).Span;
        }
    }
}
