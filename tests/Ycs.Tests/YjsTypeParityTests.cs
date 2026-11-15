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
}
