using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class YArrayShapeTests
{
    private static YArray Filled(params YValue[] values)
    {
        var array = new YDoc().GetArray("a");
        array.Add(values);
        return array;
    }

    [TestMethod]
    public void Indexer_OutOfRange_Throws()
    {
        var array = Filled(1, 2);

        Assert.AreEqual((typeof(ArgumentOutOfRangeException), typeof(ArgumentOutOfRangeException)), (Catch(() => array[2]), Catch(() => array[-1])));
    }

    private static Type Catch(Func<object> read)
    {
        try
        {
            read();
            return typeof(void);
        }
        catch (Exception e)
        {
            return e.GetType();
        }
    }

    [TestMethod]
    public void TryGetValue_InAndOutOfRange_SaysSo()
    {
        var array = Filled("a");

        Assert.AreEqual((true, "a", false, false), (array.TryGetValue(0, out var v), v.GetString(), array.TryGetValue(1, out _), array.TryGetValue(-1, out _)));
    }

    [TestMethod]
    public void Slice_AsARange_GivesTheValues()
    {
        var array = Filled(0, 1, 2, 3, 4);

        Assert.AreEqual("1,2|3,4|0,1,2,3,4|", string.Join("|", string.Join(",", array[1..3].Select(v => v.GetInt32())), string.Join(",", array[^2..].Select(v => v.GetInt32())), string.Join(",", array[..].Select(v => v.GetInt32())), string.Join(",", array[5..].Select(v => v.GetInt32()))));
    }

    [TestMethod]
    public void Slice_RangeBeyondTheArray_Throws()
    {
        var array = Filled(0, 1);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Slice(1, 2));
    }

    [TestMethod]
    public void Insert_AtEachPlace_KeepsTheOrder()
    {
        var array = Filled(1, 4);

        array.Insert(1, 2, 3);
        array.Insert(0, 0);
        array.Insert(array.Count, 5);

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, array.Select(v => v.GetInt32()).ToArray());
    }

    [TestMethod]
    public void Insert_BeyondTheCount_ThrowsAndChangesNothing()
    {
        var array = Filled(1);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Insert(2, 9));
        Assert.AreEqual(1, array.Count);
    }

    [TestMethod]
    public void Remove_AtAndRange_RemovesThem()
    {
        var array = Filled(0, 1, 2, 3, 4);

        array.RemoveAt(0);
        array.RemoveRange(1, 2);

        CollectionAssert.AreEqual(new[] { 1, 4 }, array.Select(v => v.GetInt32()).ToArray());
    }

    [TestMethod]
    public void RemoveRange_NotWithinTheArray_ThrowsAndChangesNothing()
    {
        var array = Filled(0, 1);

        Assert.AreEqual((typeof(ArgumentOutOfRangeException), typeof(ArgumentOutOfRangeException), 2), (Catch(() => { array.RemoveRange(1, 2); return 0; }), Catch(() => { array.RemoveAt(2); return 0; }), array.Count));
    }

    [TestMethod]
    public void ToArray_AtASnapshot_GivesWhatWasThere()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        var array = doc.GetArray("a");
        array.Add("x", "y");
        var snapshot = doc.CreateSnapshot();
        array.Insert(0, "w");
        array.RemoveAt(1);

        Assert.AreEqual("x,y|w,y", string.Join("|", string.Join(",", array.ToArray(snapshot).Select(v => v.GetString())), string.Join(",", array.ToArray().Select(v => v.GetString()))));
    }

    [TestMethod]
    public void NestedTypes_AreValues()
    {
        var array = Filled(new YMap { ["k"] = 1 }, YValue.Null, YValue.Array(1, 2));

        Assert.AreEqual((true, 1, true, 2), (array[0].TryGetSharedType<YMap>(out var map) ? map["k"].GetInt32() == 1 : false, 1, array[1].IsNull, array[2].Length));
        Assert.IsFalse(array[0].TryGetSharedType<YArray>(out _));
    }

    [TestMethod]
    public void NotInADocument_ConstructorAndReads_Work()
    {
        var array = new YArray(1, "two");
        array.Add(3);
        array.RemoveAt(0);
        array.Insert(0, YValue.Null);

        Assert.AreEqual("null,\"two\",3", string.Join(",", array.Select(v => v.ToString())));
        Assert.AreEqual((3, "two"), (array.Count, array[1].GetString()));
    }

    [TestMethod]
    public void Clone_CopiesTheValuesAndNestedTypes()
    {
        var array = Filled(1, new YArray(2));

        var clone = array.Clone();
        ((YArray)array[1].ToObject()).Add(3);

        Assert.AreEqual("1,[2]", string.Join(",", clone.Select(v => v.TryGetSharedType<YArray>(out var a) ? "[" + string.Join(",", a.Select(x => x.GetInt32())) + "]" : v.ToString())));
    }

    [TestMethod]
    public void Value_ByteArray_IsNotChangedByTheCallerLater()
    {
        var bytes = new byte[] { 1, 2 };
        var array = Filled(bytes);

        bytes[0] = 9;

        Assert.IsTrue(array[0].TryGetBytes(out var read) && read.Span.SequenceEqual(new byte[] { 1, 2 }));
    }

    [TestMethod]
    public void Enumerator_ThroughTheInterfaces_GivesTheValues()
    {
        IReadOnlyList<YValue> list = Filled(1, 2, 3);
        System.Collections.IEnumerable plain = list;

        Assert.AreEqual((3, 3), (list.Count, plain.Cast<YValue>().Count()));
    }

    [TestMethod]
    public void IndexOfContainsRemove_ByValue_FindTheFirst()
    {
        var array = new YDoc().GetArray("a");
        array.Add(1, "x", 1);

        var removed = array.Remove(1);

        Assert.AreEqual((true, 0, true, false, false, 1), (removed, array.IndexOf("x"), array.Contains("x"), array.Contains(2), array.Remove(2), array.IndexOf(1)));
        Assert.AreEqual(2, array.Count);
    }

    [TestMethod]
    public void CollectionExpression_OfValues_BuildsTheArray()
    {
        YArray array = [1, 2, 3];

        Assert.AreEqual(3, array.Count);
    }
}
