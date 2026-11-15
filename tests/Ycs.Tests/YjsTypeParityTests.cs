// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable

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

    private static string Json(List<string> written) => $"[{string.Join(',', written)}]";

    private static (YDoc Doc, YText Text, List<string> Deltas) ObservedText(string content)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, content);
        var deltas = new List<string>();
        text.Observe((_, e) => deltas.Add(Json(e.Event.Delta)));
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
            text.InsertEmbed(0, new Dictionary<string, object> { ["img"] = 1 });
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
        a.GetText("t").Format(0, 4, new Dictionary<string, object> { ["bold"] = true });
        b.GetText("t").Format(1, 2, new Dictionary<string, object> { ["bold"] = true });
        var o = new YDoc(new YDocOptions { ClientId = 3 });
        o.ApplyUpdate(b.EncodeStateAsUpdate());
        var deltas = new List<string>();
        o.GetText("t").Observe((_, e) => deltas.Add(Json(e.Event.Delta)));
        var updates = 0;
        o.Update += (_, _) => updates++;

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
        o.GetText("t").Observe((_, e) => deltas.Add(Json(e.Event.Delta)));
        a.Transact(_ =>
        {
            t.InsertEmbed(1, new YMap(new Dictionary<string, object> { ["k"] = 1 }));
            t.Insert(3, "c");
        });

        o.ApplyUpdate(a.EncodeStateAsUpdate(sv));

        var delta = o.GetText("t").ToDelta();
        Assert.AreEqual(
            """[{"insert":"a"},{"insert":"<YMap>"},{"insert":"bc"}] [[{"retain":1},{"insert":"<YMap>"},{"retain":1},{"insert":"c"}]] 4 1""",
            $"{Json(delta)} {Json(deltas)} {o.GetText("t").Length} {((YMap)delta[1].Insert).Get("k")}");
    }

    [TestMethod]
    public void TextInsertEmbed_SharedType_IntegratesItAsYjsDoes()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var map = new YMap();

        text.InsertEmbed(1, map, new Dictionary<string, object> { ["bold"] = true });
        map.Set("x", 2);

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
        ChangesCollection changes = null;
        text.Observe((_, e) => changes = e.Event.Changes);

        doc.Transact(_ =>
        {
            text.Insert(1, "a");
            text.Delete(3, 1);
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
        var doc = new YDoc(new YDocOptions { Gc = false });
        doc.ApplyUpdate(System.Convert.FromBase64String("AQSBvMGWCwAEAQF0AmFihIG8wZYLAQFjRIG8wZYLAAF6xIG8wZYLAYG8wZYLAgFYAYG8wZYLAQAC"));
        var before = Snapshot.DecodeSnapshot(System.Convert.FromBase64String("AAGBvMGWCwQ="));
        var after = Snapshot.DecodeSnapshot(System.Convert.FromBase64String("AYG8wZYLAQACAYG8wZYLBQ=="));
        var text = doc.GetText("t");

        var delta = text.ToDelta(after, before, (type, id) => new YText.YTextChangeAttributes { Type = type, User = id.Client });

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
        array.Insert(0, new object[] { 1, 2 });
        array.Delete(1, 1);

        array.Push(new object[] { 3 });

        // Yjs: origin (1,1), no right origin.
        Assert.AreEqual("AQMBAAgBAWEBfQGBAQABiAEBAX0DAQEBAQE=", System.Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ArrayInsert_BeyondTheLength_ThrowsAndChangesNothing()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Insert(0, new object[] { 1, 2 });

        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(() => array.Insert(5, new object[] { 9 }));

        Assert.AreEqual("[1,2]", Json(array.ToArray()));
    }

    [TestMethod]
    public void ArrayNotInADocument_InsertAndDeleteOutOfRange_SpliceAsJavaScript()
    {
        var doc = new YDoc();
        var array = new YArray();
        array.Insert(3, new object[] { 1 });
        array.Insert(-1, new object[] { 0 });
        array.Delete(5, 3);

        doc.GetMap("m").Set("a", array);

        Assert.AreEqual("[0,1]", Json(array.ToArray()));
    }

    [TestMethod]
    public void ArraySlice_EmptyNegativeAndLongRanges_ReturnWhatYjsReturns()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        array.Insert(0, new object[] { 1, 2, 3 });

        Assert.AreEqual("[] [null,null,1,2,3] [2,3]", $"{Json(array.Slice(2, 1))} {Json(array.Slice(-5))} {Json(array.Slice(1, 10))}");
    }

    [TestMethod]
    public void MapGet_MissingKey_ReturnsNullAndTryGetTellsItApart()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map.Set("n", null);

        var result = $"{map.Get("missing") ?? "null"} {map.TryGet("missing", out _)} {map.TryGet("n", out var n)} {n ?? "null"}";

        Assert.AreEqual("null False True null", result);
    }

    [TestMethod]
    public void TextDelete_EmptyText_Throws()
    {
        var text = new YDoc().GetText("t");

        Assert.ThrowsExactly<System.InvalidOperationException>(() => text.Delete(0, 1));
    }

    [TestMethod]
    public void TextNotInADocument_ChangeThatFailsOnReplay_DropsItAndTheRest()
    {
        var doc = new YDoc();
        var text = new YText();
        text.Delete(0, 1);
        text.Insert(0, "a");

        doc.GetMap("m").Set("t", text);

        Assert.AreEqual(string.Empty, text.ToString());
    }

    [TestMethod]
    public void EventKeys_ReadAfterTheOldValueWasCollected_OldValueIsNull()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        YEvent kept = null;
        map.Observe((_, e) => kept = e.Event);
        string keys = null;
        doc.AfterTransactionCleanup += (_, _) => keys = string.Join(",", kept.Keys.Select(kv => $"{kv.Key} {kv.Value.Action} {kv.Value.OldValue ?? "null"}"));
        map.Set("k", 1);

        map.Set("k", 2);

        Assert.AreEqual("k Update null", keys);
    }

    [TestMethod]
    public void MapSnapshot_ValueCollectedSinceTheSnapshot_IsNullAsYjsUndefined()
    {
        // Yjs 13.6.32 (gc on): ymap.set('k', 1); s = snapshot(doc); ymap.delete('k');
        // typeMapGetSnapshot(ymap, 'k', s) and typeMapGetAllSnapshot(ymap, s) give undefined and { k: undefined }.
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map.Set("k", 1);
        var snapshot = doc.CreateSnapshot();
        map.Delete("k");

        var value = map.GetSnapshot("k", snapshot);
        var all = map.GetAllSnapshot(snapshot);

        Assert.AreEqual("null k=null", $"{value ?? "null"} {string.Join(",", all.Select(kv => $"{kv.Key}={kv.Value ?? "null"}"))}");
    }

    [TestMethod]
    public void TextFormat_ListEqualToObjectWithIndexKeys_AddsNoFormat()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "abc", new Dictionary<string, object> { ["l"] = new Dictionary<string, object> { ["0"] = 1, ["1"] = 2 } });

        text.Format(0, 3, new Dictionary<string, object> { ["l"] = new List<object> { 1, 2 } });

        Assert.AreEqual("AQMBAAYBAXQBbA17IjAiOjEsIjEiOjJ9hAEAA2FiY4YBAwFsBG51bGwA", System.Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void TextInsert_AttributeKeysThatAreIndexes_FormatsInJavaScriptKeyOrder()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.GetText("t").Insert(0, "abc", new Dictionary<string, object> { ["b"] = true, ["1"] = "x", ["0"] = "y" });

        Assert.AreEqual("AQcBAAYBAXQBMAMieSKGAQABMQMieCKGAQEBYgR0cnVlhAECA2FiY4YBBQEwBG51bGyGAQYBMQRudWxshgEHAWIEbnVsbAA=", System.Convert.ToBase64String(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ArrayInsert_ByteArrayChangedAfterwards_DocumentKeepsTheInsertedBytes()
    {
        var array = new YDoc().GetArray("a");
        var bytes = new byte[] { 1, 2 };
        array.Insert(0, new object[] { bytes });

        bytes[0] = 9;

        Assert.AreEqual("[1,2]", Json(((byte[])array.Get(0)).Select(b => (object)(int)b)));
    }

    [TestMethod]
    public void FindMarker_SecondLookupInAShortArray_AdjustsTheMarkerAsYjs()
    {
        // 50 values in one item: both lookups end at that item, index 0. Yjs reuses the marker when
        // the distance is below length / 80 = 0.625 (a float division), so it keeps one marker.
        var array = new YDoc().GetArray("a");
        array.Insert(0, Enumerable.Range(0, 50).Cast<object>().ToList());

        array.Get(10);
        array.Get(20);

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
        fragment.Insert(0, new object[] { element });
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
            element.SetAttribute(key, value);
        }

        var text = new YXmlText();
        fragment.Insert(1, new object[] { text, new YXmlHook("h") });
        text.Insert(0, "ab", new Dictionary<string, object>
        {
            ["color"] = "red",
            ["bold"] = true,
            ["link"] = new Dictionary<string, object> { ["href"] = "u", ["a"] = 1 },
            ["l"] = new List<object> { 1, 2 },
        });
        text.InsertEmbed(2, new Dictionary<string, object> { ["img"] = 1 });

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
        doc.GetXmlFragment("x").Insert(0, new object[] { element });
        element.SetAttribute("n", null);
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
            new Delta { Insert = "a" },
            new Delta { Insert = new YArray(), Attributes = new Dictionary<string, object> { ["b"] = 1 } },
            new Delta { Insert = "c" },
        ]);

        Assert.AreEqual("AQUBAAQBAXQBYYYBAAFiATGHAQEAhgECAWIEbnVsbIQBAwFjAA== 3", $"{System.Convert.ToBase64String(doc.EncodeStateAsUpdate())} {text.Length}");
    }
}
