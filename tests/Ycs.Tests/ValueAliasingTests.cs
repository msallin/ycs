using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>What a caller holds and what a document holds are separate: a change to one never reaches the other.</summary>
[TestClass]
public class ValueAliasingTests
{
    private static Dictionary<string, object?> Sample(out List<object?> inner, out byte[] bytes)
    {
        bytes = [1, 2, 3];
        inner = ["x", new Dictionary<string, object?> { ["deep"] = 1 }, bytes];
        return new Dictionary<string, object?> { ["z"] = 1, ["a"] = inner, ["m"] = bytes };
    }

    private static void Mutate(Dictionary<string, object?> map, List<object?> inner, byte[] bytes)
    {
        bytes[0] = 99;
        inner.Add("late");
        ((Dictionary<string, object?>)inner[1]!)["deep"] = 2;
        map["new"] = true;
        map.Remove("z");
    }

    [TestMethod]
    public void MapSet_CallerMutatesAfterInsert_EncodingUnchanged()
    {
        var doc = new YDoc();
        var map = Sample(out var inner, out var bytes);
        doc.GetMap("m").Set("k", map);
        var before = doc.EncodeStateAsUpdate();

        Mutate(map, inner, bytes);

        CollectionAssert.AreEqual(before, doc.EncodeStateAsUpdate());
        Assert.AreEqual(3, ((IDictionary<string, object?>)doc.GetMap("m").Get("k")).Count);
    }

    [TestMethod]
    public void ArrayAdd_CallerMutatesAfterInsert_EncodingUnchanged()
    {
        var doc = new YDoc();
        var map = Sample(out var inner, out var bytes);
        var list = new List<object?> { map, inner, bytes };
        doc.GetArray("a").Add(list.Cast<object>().ToList());
        var before = doc.EncodeStateAsUpdate(format: UpdateFormat.V2);

        Mutate(map, inner, bytes);

        CollectionAssert.AreEqual(before, doc.EncodeStateAsUpdate(format: UpdateFormat.V2));
    }

    [TestMethod]
    public void MapSet_ByteArray_CallerMutatesAfterInsert_DocumentKeepsItsBytes()
    {
        var doc = new YDoc();
        var bytes = new byte[] { 1, 2, 3 };
        doc.GetMap("m").Set("k", bytes);

        bytes[0] = 9;

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, (byte[])doc.GetMap("m").Get("k"));
    }

    [TestMethod]
    public void Read_CallerMutatesTheBytesItGot_DocumentKeepsItsBytes()
    {
        var doc = new YDoc();
        doc.GetMap("m").Set("top", new byte[] { 1, 2, 3 });
        doc.GetMap("m").Set("nested", new List<object?> { new byte[] { 4, 5 } });
        doc.GetArray("a").Add(new object[] { new byte[] { 6 }, new Dictionary<string, object?> { ["b"] = new byte[] { 7 } } });
        var before = doc.EncodeStateAsUpdate();

        ((byte[])doc.GetMap("m").Get("top"))[0] = 9;
        ((byte[])((IList<object?>)doc.GetMap("m").Get("nested"))[0]!)[0] = 9;
        ((byte[])doc.GetArray("a").Get(0))[0] = 9;
        ((byte[])((IDictionary<string, object?>)doc.GetArray("a").Get(1))["b"]!)[0] = 9;
        ((byte[])doc.GetMap("m").ToJson().As<IDictionary<string, object?>>()["top"]!)[0] = 9;
        foreach (var value in doc.GetArray("a"))
        {
            (value as byte[])?.AsSpan().Fill(9);
        }

        CollectionAssert.AreEqual(before, doc.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void Read_DecodedFromAnUpdate_CallerMutatesTheBytesItGot_DocumentKeepsItsBytes()
    {
        var source = new YDoc();
        source.GetMap("m").Set("nested", new List<object?> { new byte[] { 4, 5 } });
        var remote = new YDoc();
        remote.ApplyUpdate(source.EncodeStateAsUpdate());

        ((byte[])((IList<object?>)remote.GetMap("m").Get("nested"))[0]!)[0] = 9;

        CollectionAssert.AreEqual(source.EncodeStateAsUpdate(), remote.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void Read_ContainersFromADocument_AreReadOnly()
    {
        var doc = new YDoc();
        doc.GetMap("m").Set("list", new List<object?> { 1, 2 });
        doc.GetMap("m").Set("obj", new Dictionary<string, object?> { ["k"] = 1 });
        var list = (IList<object?>)doc.GetMap("m").Get("list");
        var obj = (IDictionary<string, object?>)doc.GetMap("m").Get("obj");

        Assert.IsTrue(list.IsReadOnly && obj.IsReadOnly && ((IList)list).IsFixedSize);
        Assert.ThrowsExactly<NotSupportedException>(() => list.Add(3));
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = 3);
        Assert.ThrowsExactly<NotSupportedException>(() => list.Insert(0, 3));
        Assert.ThrowsExactly<NotSupportedException>(() => list.RemoveAt(0));
        Assert.ThrowsExactly<NotSupportedException>(() => list.Remove(1));
        Assert.ThrowsExactly<NotSupportedException>(() => list.Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList)list).Add(3));
        Assert.ThrowsExactly<NotSupportedException>(() => obj["k"] = 2);
        Assert.ThrowsExactly<NotSupportedException>(() => obj.Add("n", 2));
        Assert.ThrowsExactly<NotSupportedException>(() => obj.Add(new KeyValuePair<string, object?>("n", 2)));
        Assert.ThrowsExactly<NotSupportedException>(() => obj.Remove("k"));
        Assert.ThrowsExactly<NotSupportedException>(() => obj.Clear());
        Assert.ThrowsExactly<NotSupportedException>(() => ((ICollection<string>)obj.Keys).Add("n"));
    }

    [TestMethod]
    public void Read_ContainersFromADocument_ReadLikeCollections()
    {
        var doc = new YDoc();
        doc.GetMap("m").Set("list", new List<object?> { 1, "b", null });
        doc.GetMap("m").Set("obj", new Dictionary<string, object?> { ["z"] = 1, ["a"] = 2 });
        var list = (IList<object?>)doc.GetMap("m").Get("list");
        var obj = (IReadOnlyDictionary<string, object?>)doc.GetMap("m").Get("obj");

        Assert.AreEqual(
            (3, 1, "b", 1, true, "z,a", "1,2", 2, false),
            (list.Count, list[0], list[1], list.IndexOf("b"), list.Contains(null), string.Join(',', obj.Keys), string.Join(',', obj.Values), obj["a"], obj.TryGetValue("q", out _)));
        CollectionAssert.AreEqual(new object?[] { 1, "b", null }, list.ToArray());
        CollectionAssert.AreEqual(new[] { "z", "a" }, obj.Select(kv => kv.Key).ToArray());
    }

    [TestMethod]
    public void MapSet_ObjectKeys_KeepTheirOrderInTheEncoding()
    {
        var ordered = new YDoc();
        ordered.GetMap("m").Set("k", new Dictionary<string, object?> { ["z"] = 1, ["a"] = 2, ["m"] = 3 });
        var remote = new YDoc();

        remote.ApplyUpdate(ordered.EncodeStateAsUpdate());

        Assert.AreEqual("z,a,m", string.Join(',', ((IDictionary<string, object?>)remote.GetMap("m").Get("k")).Keys));
        CollectionAssert.AreEqual(ordered.EncodeStateAsUpdate(), remote.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void MapSet_ValueReadFromAnotherDocument_InsertsEqually()
    {
        var first = new YDoc();
        first.GetMap("m").Set("k", new Dictionary<string, object?> { ["l"] = new List<object?> { 1, new byte[] { 5 } } });
        var second = new YDoc();
        second.ClientId = first.ClientId;

        second.GetMap("m").Set("k", first.GetMap("m").Get("k"));

        CollectionAssert.AreEqual(first.EncodeStateAsUpdate(), second.EncodeStateAsUpdate());
    }

    [TestMethod]
    public void MapSet_FrozenValueBeyondTheLimitOfTheTargetDocument_Throws()
    {
        var first = new YDoc();
        object nested = new List<object?> { new List<object?> { new List<object?> { 1 } } };
        first.GetMap("m").Set("k", nested);
        var second = new YDoc(new YDocOptions { Limits = new UpdateLimits { MaxDepth = 2 } });

        Assert.ThrowsExactly<ArgumentException>(() => second.GetMap("m").Set("k", first.GetMap("m").Get("k")));
    }

    [TestMethod]
    public void Constructors_PrelimValues_CallerMutatesBeforeTheTypeJoinsADocument_DocumentHasTheInsertedValue()
    {
        var list = new List<object?> { 1 };
        var map = new YMap(new Dictionary<string, object> { ["l"] = list });
        var array = new YArray([list]);
        list.Add(2);
        var doc = new YDoc();

        doc.GetMap("root").Set("m", map);
        doc.GetMap("root").Set("a", array);

        Assert.AreEqual((1, 1), (((IList)doc.GetMap("root").Get("m").As<YMap>().Get("l")).Count, ((IList)doc.GetMap("root").Get("a").As<YArray>().Get(0)).Count));
    }

    [TestMethod]
    public void TextFormatAndEmbed_CallerMutatesAfterInsert_DocumentUnchanged()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var attribute = new List<object?> { 1 };
        var embed = new Dictionary<string, object?> { ["e"] = new List<object?> { 1 } };
        text.Format(0, 1, new Dictionary<string, YValue> { ["x"] = YValue.FromObject(attribute) });
        text.InsertEmbed(2, YValue.FromObject(embed));
        var before = doc.EncodeStateAsUpdate();

        attribute.Add(2);
        ((List<object?>)embed["e"]!).Add(2);
        embed["f"] = 1;

        CollectionAssert.AreEqual(before, doc.EncodeStateAsUpdate());
        var delta = text.ToDelta();
        Assert.AreEqual(1, delta[0].Attributes["x"].Length);
        Assert.AreEqual(1, delta[2].Inserted().MemberCount);
    }

    [TestMethod]
    public void TextEmbed_ByteArray_CallerMutatesTheOneItGot_DocumentUnchanged()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.InsertEmbed(0, YValue.FromObject(new byte[] { 1, 2 }));

        text.ToDelta()[0].Inserted().GetBytes()[0] = 9;

        CollectionAssert.AreEqual(new byte[] { 1, 2 }, text.ToDelta()[0].Inserted().GetBytes());
    }

    [TestMethod]
    public void Insert_UnsafeLong_ThrowsAtTheCallAndLeavesTheDocument()
    {
        var doc = new YDoc();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.GetMap("m").Set("k", 9007199254740992L));
        Assert.AreEqual(0, doc.GetMap("m").Count);
    }

    [TestMethod]
    public void Insert_ValueThatContainsItself_ThrowsAtTheCall()
    {
        var doc = new YDoc();
        var list = new List<object?>();
        list.Add(list);

        Assert.ThrowsExactly<ArgumentException>(() => doc.GetMap("m").Set("k", list));
        Assert.AreEqual(0, doc.GetMap("m").Count);
    }

    [TestMethod]
    public void Insert_DictionaryOfAnotherValueType_IsCopiedWithItsOrder()
    {
        var doc = new YDoc();
        var strings = new Dictionary<string, string> { ["b"] = "1", ["a"] = "2" };
        doc.GetMap("m").Set("k", strings);

        strings["b"] = "changed";

        Assert.AreEqual("b=1,a=2", string.Join(',', ((IDictionary<string, object?>)doc.GetMap("m").Get("k")).Select(kv => $"{kv.Key}={kv.Value}")));
    }
}

internal static class AliasingTestExtensions
{
    public static T As<T>(this object? value) => (T)value!;
}
