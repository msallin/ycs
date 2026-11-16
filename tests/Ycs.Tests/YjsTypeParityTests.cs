// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The shared types and their events against Yjs 13.6.32, scenario by scenario. Each expected value
/// is what Yjs gives for the same calls.
/// </summary>
[TestClass]
public class YjsTypeParityTests
{
    private static string Json(object value) => CanonicalJson.Write(value);

    private static string Json(IEnumerable<YValue> values) => CanonicalJson.Write(values.Select(v => v.ToObject()).ToList());

    private static string Json(IReadOnlyDictionary<string, YValue> values) => CanonicalJson.Write(values.ToDictionary(kv => kv.Key, kv => kv.Value.ToObject()));

    private static string Json(List<string> written) => $"[{string.Join(',', written)}]";

    private static (YDoc Doc, YText Text, List<string> Deltas) ObservedText(string content)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, content);
        var deltas = new List<string>();
        text.Observe(e => deltas.Add(Json(e.Delta)));
        return (doc, text, deltas);
    }

    [TestMethod]
    public void TextEventDelta_TwoInsertsInOneTransaction_EachInsertOnce()
    {
        var (doc, text, deltas) = ObservedText("xyz");

        doc.Transact(_ =>
        {
            text.Insert(0, "a");
            text.Insert(2, "b");
        });

        Assert.AreEqual("""[[{"insert":"a"},{"retain":1},{"insert":"b"}]]""", Json(deltas));
    }

    [TestMethod]
    public void TextEventDelta_EmbedThenInsert_InsertIsOnlyTheText()
    {
        var (doc, text, deltas) = ObservedText("x");

        doc.Transact(_ =>
        {
            text.InsertEmbed(0, YValue.FromObject(new Dictionary<string, object> { ["img"] = 1 }));
            text.Insert(2, "b");
        });

        Assert.AreEqual("""[[{"insert":{"img":1}},{"retain":1},{"insert":"b"}]]""", Json(deltas));
    }

    [TestMethod]
    public void TextEventDelta_RemoteFormatOverlapsOwnEqualFormat_RedundantFormatDeletedInANewTransaction()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        a.GetText("t").Insert(0, "abcd");
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        var beforeFormat = a.EncodeStateVector();
        a.GetText("t").Format(0, 4, new Dictionary<string, YValue> { ["bold"] = true });
        b.GetText("t").Format(1, 2, new Dictionary<string, YValue> { ["bold"] = true });
        var o = new YDoc(new YDocOptions { ClientId = 3 });
        o.ApplyUpdate(b.EncodeStateAsUpdate());
        var deltas = new List<string>();
        o.GetText("t").Observe(e => deltas.Add(Json(e.Delta)));
        var updates = 0;
        o.Updated += (_, _) => updates++;

        o.ApplyUpdate(a.EncodeStateAsUpdate(beforeFormat));

        // Yjs: the delta deletes B's redundant 'bold' start in its own transaction, which fires again.
        Assert.AreEqual("""[[{"attributes":{"bold":true},"retain":3}],[]]""", Json(deltas) + (updates == 2 ? string.Empty : $" updates {updates}"));
    }

    [TestMethod]
    public void TextWithEmbeddedType_RemoteInsert_ToDeltaAndEventShowTheType()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var t = a.GetText("t");
        t.Insert(0, "ab");
        var o = new YDoc(new YDocOptions { ClientId = 2 });
        o.ApplyUpdate(a.EncodeStateAsUpdate());
        var sv = a.EncodeStateVector();
        var deltas = new List<string>();
        o.GetText("t").Observe(e => deltas.Add(Json(e.Delta)));
        a.Transact(_ =>
        {
            t.InsertEmbed(1, new YMap { ["k"] = 1 });
            t.Insert(3, "c");
        });

        o.ApplyUpdate(a.EncodeStateAsUpdate(sv));

        var delta = o.GetText("t").ToDelta();
        Assert.AreEqual(
            """[{"insert":"a"},{"insert":"<YMap>"},{"insert":"bc"}] [[{"retain":1},{"insert":"<YMap>"},{"retain":1},{"insert":"c"}]] 4 1""",
            $"{Json(delta)} {Json(deltas)} {o.GetText("t").Length} {((YMap)delta[1].Inserted().ToObject())["k"].ToObject()}");
    }

    [TestMethod]
    public void TextInsertEmbed_SharedType_IntegratesItAsYjsDoes()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var map = new YMap();

        text.InsertEmbed(1, map, new Dictionary<string, YValue> { ["bold"] = true });
        map["x"] = 2;

        Assert.AreEqual(
            """[{"insert":"a"},{"attributes":{"bold":true},"insert":"<YMap>"},{"insert":"b"}] AQYBAAQBAXQBYYQBAAFixgEAAQEEYm9sZAR0cnVlxwECAQEBxgEDAQEEYm9sZARudWxsKAABAwF4AX0CAA==""",
            $"{Json(text.ToDelta())} {System.Convert.ToBase64String(doc.EncodeStateAsUpdate())}");
    }

    [TestMethod]
    public void TextEventChanges_InsertAndDelete_IsTheTextDeltaWithoutItems()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "xyz");
        YChanges changes = null;
        text.Observe(e => changes = e.Changes);

        doc.Transact(_ =>
        {
            text.Insert(1, "a");
            text.Remove(3, 1);
        });

        Assert.AreEqual(
            """[{"retain":1},{"insert":"a"},{"retain":1},{"delete":1}] 0 0 0""",
            $"{Json(changes.Delta)} {changes.Added.Count} {changes.Deleted.Count} {changes.Keys.Count}");
    }

    [TestMethod]
    public void ToDelta_ComputeYChangeWithUser_SeparatesRemovedAndAddedOfOneUser()
    {
        // Written by Yjs with client id 3000000001 (half of all Yjs client ids are above 2^31):
        // 'abc', then 'z' at 0, snapshot, delete 'a' and 'b', insert 'X' at 1, snapshot.
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        doc.ApplyUpdate(System.Convert.FromBase64String("AQSBvMGWCwAEAQF0AmFihIG8wZYLAQFjRIG8wZYLAAF6xIG8wZYLAYG8wZYLAgFYAYG8wZYLAQAC"));
        var before = Snapshot.Decode(System.Convert.FromBase64String("AAGBvMGWCwQ="));
        var after = Snapshot.Decode(System.Convert.FromBase64String("AYG8wZYLAQACAYG8wZYLBQ=="));
        var text = doc.GetText("t");

        var delta = text.ToDelta(after, before, (type, id) => new YChange(type, id.Client));

        Assert.AreEqual(
            """[{"insert":"z"},{"attributes":{"ychange":{"type":"removed","user":3000000001}},"insert":"ab"},{"attributes":{"ychange":{"type":"added","user":3000000001}},"insert":"X"},{"insert":"c"}]""",
            Json(delta));
    }

    [TestMethod]
    public void ToDelta_WithoutSnapshot_StartsNoTransaction()
    {
        var doc = new YDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var transactions = 0;
        doc.AfterTransaction += (_, _) => transactions++;

        text.ToDelta();

        Assert.AreEqual(0, transactions);
    }

    [TestMethod]
    public void TextNotInADocument_ToDeltaToStringClone_ReadAsEmpty()
    {
        var result = $"{Json(new YText("abc").ToDelta())} '{new YXmlText("abc")}' {Json(new YText("abc").Clone().ToDelta())}";

        Assert.AreEqual("[] '' []", result);
    }

    [TestMethod]
    public void ArrayPush_AfterDeletedTail_AppendsAfterTheDeletedItemAsYjs()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        array.Insert(0, 1, 2);
        array.RemoveRange(1, 1);

        array.Add(3);

        // Yjs: origin (1,1), no right origin.
        Assert.AreEqual("AQMBAAgBAWEBfQGBAQABiAEBAX0DAQEBAQE=", System.Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ArrayInsert_BeyondTheLength_ThrowsAndChangesNothing()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Insert(0, 1, 2);

        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => array.Insert(5, 9));

        Assert.AreEqual("[1,2]", Json(array.ToArray()));
    }

    [TestMethod]
    public void ArrayNotInADocument_InsertAndRemoveOutOfRange_Throw()
    {
        var doc = new YDoc();
        var array = new YArray();
        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => array.Insert(3, 1));
        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => array.Insert(-1, 0));
        array.Add(1, 2);
        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => array.RemoveRange(1, 5));

        doc.GetMap("m")["a"] = array;

        Assert.AreEqual("[1,2]", Json(array.ToArray()));
    }

    [TestMethod]
    public void ArraySlice_RangeOutsideTheArray_ThrowsAndTheRestIsTheValues()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Insert(0, 1, 2, 3);

        Assert.AreEqual("[3]", Json(array.Slice(2, 1)));
        Assert.AreEqual("[]", Json(array.Slice(3, 0)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Slice(-5, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.Slice(1, 10));
    }

    [TestMethod]
    public void MapIndexer_MissingKey_ThrowsAndTryGetValueTellsItApart()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map["n"] = YValue.Null;

        var result = $"{map.TryGetValue("missing", out _)} {map.TryGetValue("n", out var n)} {n.IsNull}";

        Assert.AreEqual("False True True", result);
        Assert.ThrowsExactly<KeyNotFoundException>(() => map["missing"]);
    }

    [TestMethod]
    public void TextDelete_EmptyText_Throws()
    {
        var text = new YDoc().GetText("t");

        Assert.ThrowsExactly<System.InvalidOperationException>(() => text.Remove(0, 1));
    }

    [TestMethod]
    public void TextNotInADocument_ChangeThatFailsOnReplay_DropsItAndTheRest()
    {
        var doc = new YDoc();
        var text = new YText();
        text.Remove(0, 1);
        text.Insert(0, "a");

        doc.GetMap("m")["t"] = text;

        Assert.AreEqual(string.Empty, text.ToString());
    }

    [TestMethod]
    public void EventKeys_ReadAfterTheOldValueWasCollected_OldValueIsNull()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        YEvent kept = null;
        map.Observe(e => kept = e);
        string keys = null;
        doc.AfterTransactionCleanup += (_, _) => keys = string.Join(",", kept.Keys.Select(kv => $"{kv.Key} {kv.Value.Action} {kv.Value.OldValue ?? "null"}"));
        map["k"] = 1;

        map["k"] = 2;

        Assert.AreEqual("k Update null", keys);
    }

    [TestMethod]
    public void MapSnapshot_ValueCollectedSinceTheSnapshot_IsNullAsYjsUndefined()
    {
        // Yjs 13.6.32 (gc on): ymap.set('k', 1); s = snapshot(doc); ymap.delete('k');
        // typeMapGetSnapshot(ymap, 'k', s) and typeMapGetAllSnapshot(ymap, s) give undefined and { k: undefined }.
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map["k"] = 1;
        var snapshot = doc.CreateSnapshot();
        map.Remove("k");

        var value = (map.TryGetValue("k", snapshot, out var __sv5) ? __sv5.ToObject() : null);
        var all = map.ToDictionary(snapshot).ToDictionary(kv => kv.Key, kv => kv.Value.ToObject());

        Assert.AreEqual("null k=null", $"{value ?? "null"} {string.Join(",", all.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"))}");
    }

    [TestMethod]
    public void Observe_HandlerRegisteredTwice_EachSubscriptionIsDisposedOnItsOwn()
    {
        // Yjs 13.6.32 removes both registrations of f at once; a subscription removes its own.
        var doc = new YDoc();
        var text = doc.GetText("t");
        var calls = 0;
        var deepCalls = 0;
        Action<YTextEvent> handler = _ => calls++;
        Action<IReadOnlyList<YEvent>, Transaction> deepHandler = (_, _) => deepCalls++;
        var first = text.Observe(handler);
        text.Observe(handler);
        var firstDeep = text.ObserveDeep(deepHandler);
        text.ObserveDeep(deepHandler);
        text.Insert(0, "a");

        first.Dispose();
        firstDeep.Dispose();
        text.Insert(0, "b");

        Assert.AreEqual((3, 3), (calls, deepCalls));
    }

    [TestMethod]
    public void TextFormat_ListEqualToObjectWithIndexKeys_AddsNoFormat()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "abc", new Dictionary<string, YValue> { ["l"] = YValue.FromObject(new Dictionary<string, object> { ["0"] = 1, ["1"] = 2 }) });

        text.Format(0, 3, new Dictionary<string, YValue> { ["l"] = YValue.FromObject(new List<object> { 1, 2 }) });

        Assert.AreEqual("AQMBAAYBAXQBbA17IjAiOjEsIjEiOjJ9hAEAA2FiY4YBAwFsBG51bGwA", System.Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void TextInsert_AttributeKeysThatAreIndexes_FormatsInJavaScriptKeyOrder()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.GetText("t").Insert(0, "abc", new Dictionary<string, YValue> { ["b"] = true, ["1"] = "x", ["0"] = "y" });

        Assert.AreEqual("AQcBAAYBAXQBMAMieSKGAQABMQMieCKGAQEBYgR0cnVlhAECA2FiY4YBBQEwBG51bGyGAQYBMQRudWxshgEHAWIEbnVsbAA=", System.Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ArrayInsert_ByteArrayChangedAfterwards_DocumentKeepsTheInsertedBytes()
    {
        var array = new YDoc().GetArray("a");
        var bytes = new byte[] { 1, 2 };
        array.Insert(0, bytes);

        bytes[0] = 9;

        Assert.AreEqual("[1,2]", Json(((byte[])array[0].ToObject()).Select(b => (object)(int)b)));
    }

    [TestMethod]
    public void FindMarker_SecondLookupInAShortArray_AdjustsTheMarkerAsYjs()
    {
        // 50 values in one item: both lookups end at that item, index 0. Yjs reuses the marker when
        // the distance is below length / 80 = 0.625 (a float division), so it keeps one marker.
        var array = new YDoc().GetArray("a");
        array.Insert(0, Enumerable.Range(0, 50).Cast<object>().ToList().Select(YValue.FromObject).ToArray());

        array[10].ToObject();
        array[20].ToObject();

        var markers = (YArrayBase.ArraySearchMarkerCollection)typeof(YArrayBase)
            .GetField("_searchMarkers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .GetValue(array);
        Assert.AreEqual(1, markers.Count);
    }

    [TestMethod]
    public void XmlToString_ValuesFormatsEmbedsAndHooks_WrittenAsYjsWritesThem()
    {
        var doc = new YDoc();
        var fragment = doc.GetXmlFragment("x");
        var element = new YXmlElement("P");
        fragment.Insert(0, element);
        var values = new Dictionary<string, object>
        {
            ["a"] = true,
            ["b"] = 1.5,
            ["c"] = null,
            ["d"] = new Dictionary<string, object> { ["x"] = 1 },
            ["e"] = new List<object> { 1, null, new List<object> { 2, 3 } },
            ["g"] = 1e21,
            ["h"] = 1e-7,
            ["i"] = 123456789012345680000d,
            ["j"] = 0.000001,
            ["k"] = -1.5,
            ["l"] = "v",
        };
        foreach (var (key, value) in values)
        {
            element.SetAttribute(key, YValue.FromObject(value));
        }

        var text = new YXmlText();
        fragment.Insert(1, text, new YXmlHook("h"));
        text.Insert(0, "ab", new Dictionary<string, YValue> { ["color"] = "red", ["bold"] = true, ["link"] = YValue.FromObject(new Dictionary<string, object> { ["href"] = "u", ["a"] = 1 }), ["l"] = YValue.FromObject(new List<object> { 1, 2 }) });
        text.InsertEmbed(2, YValue.FromObject(new Dictionary<string, object> { ["img"] = 1 }));

        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            Assert.AreEqual(
                """<p a="true" b="1.5" c="null" d="[object Object]" e="1,,2,3" g="1e+21" h="1e-7" i="123456789012345680000" j="0.000001" k="-1.5" l="v"></p><bold><color 0="r" 1="e" 2="d"><l 0="1" 1="2"><link a="1" href="u">ab</link></l></color></bold>[object Object][object Object]""",
                fragment.ToString());
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [TestMethod]
    public void XmlGetAttributes_Snapshot_KeepsNullValues()
    {
        var doc = new YDoc();
        var element = new YXmlElement("p");
        doc.GetXmlFragment("x").Insert(0, element);
        element.SetAttribute("n", YValue.Null);
        element.SetAttribute("s", "v");

        Assert.AreEqual("""{"n":null,"s":"v"}""", Json(element.GetAttributes(doc.CreateSnapshot())));
    }

    [TestMethod]
    public void TextApplyDelta_InsertOfASharedType_EmbedsItAsYjs()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");

        text.ApplyDelta(
        [
            new Delta.Insert("a"),
            new Delta.Insert(YValue.FromObject(new YArray())) { Attributes = new Dictionary<string, YValue> { ["b"] = 1 } },
            new Delta.Insert("c"),
        ]);

        Assert.AreEqual("AQUBAAQBAXQBYYYBAAFiATGHAQEAhgECAWIEbnVsbIQBAwFjAA== 3", $"{System.Convert.ToBase64String(doc.EncodeStateAsUpdate())} {text.Length}");
    }
}
