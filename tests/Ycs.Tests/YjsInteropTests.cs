// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Byte-level compatibility with real Yjs. The updates below were produced by
/// 'interop/js/gen.mjs' running against the published yjs packages; re-generate them
/// with 'interop/run.sh' when bumping the tested Yjs version.
/// </summary>
[TestClass]
public class YjsInteropTests
{
    // yjs 13.6.32 - Y.encodeStateAsUpdate / Y.encodeStateAsUpdateV2
    private const string Yjs13UpdateV1 = "ART60JjOBAAoAQNtYXADc3RyAXcQaGVsbG8g4pyTINC80LjRgCgBA21hcANpbnQBfSooAQNtYXAFZmxvYXQBfEBgAAAoAQNtYXAEYm9vbAF4KAEDbWFwBG51bGwBfigBA21hcARqc29uAXYCAWF9AQFidQJ3AXh3AXkjAQNtYXADYmluBAECA/onAQNtYXAGbmVzdGVkASgA+tCYzgQHBGRlZXABdwV2YWx1ZQgBBWFycmF5AXcBYYH60JjOBAkBiPrQmM4ECgJ5dgEBa3cBdof60JjOBAwACAD60JjOBA0CdwJpMHcCaTEEAQR0ZXh0BnBsYWluIIb60JjOBBUEYm9sZAR0cnVlhPrQmM4EFgRib2xkhvrQmM4EGgRib2xkBG51bGzE+tCYzgQa+tCYzgQbBSB0YWlshfrQmM4EGxF7ImltYWdlIjoieC5wbmcifQH60JjOBAEKAQ==";
    private const string Yjs13UpdateV2 = "AAIAAgb6obGcCQoKDgQCBAIQAggAAgE2HSgFIwAnACgACACBAIgAhwAIAAQAhgCEAIYAxACFcFxtYXBzdHJtYXBpbnRtYXBmbG9hdG1hcGJvb2xtYXBudWxsbWFwanNvbm1hcGJpbm1hcG5lc3RlZGRlZXBhcnJheXRleHRwbGFpbiBib2xkYm9sZGJvbGQgdGFpbEMDBQMEAwQDBEMBBgQFBAZEAQUJAQcAAAEAAAABAgEABEEHQgABFAB3EGhlbGxvIOKckyDQvNC40YB9KnxAYAAAeH52AgFhfQEBYnUCdwF4dwF5BAECA/p3BXZhbHVldwFheXYBAWt3AXZ3AmkwdwJpMXh+dgEFaW1hZ2V3BXgucG5nAfrQmM4EAQoA";

    // yjs 14.0.0-16 (beta) - same document, same wire format
    private const string Yjs14UpdateV1 = "ARTI/4exDwAoAQNtYXADc3RyAXcQaGVsbG8g4pyTINC80LjRgCgBA21hcANpbnQBfSooAQNtYXAFZmxvYXQBfEBgAAAoAQNtYXAEYm9vbAF4KAEDbWFwBG51bGwBfigBA21hcARqc29uAXYCAWF9AQFidQJ3AXh3AXkjAQNtYXADYmluBAECA/onAQNtYXAGbmVzdGVkASgAyP+HsQ8HBGRlZXABdwV2YWx1ZQgBBWFycmF5AXcBYYHI/4exDwkBiMj/h7EPCgJ5dgEBa3cBdofI/4exDwwACADI/4exDw0CdwJpMHcCaTEEAQR0ZXh0BnBsYWluIIbI/4exDxUEYm9sZAR0cnVlhMj/h7EPFgRib2xkhsj/h7EPGgRib2xkBG51bGzEyP+HsQ8ayP+HsQ8bBSB0YWlshcj/h7EPGxF7ImltYWdlIjoieC5wbmcifQHI/4exDwEKAQ==";
    private const string Yjs14UpdateV2 = "AAIAAgbI/4/iHgoKDgQCBAIQAggAAgE2HSgFIwAnACgACACBAIgAhwAIAAQAhgCEAIYAxACFcFxtYXBzdHJtYXBpbnRtYXBmbG9hdG1hcGJvb2xtYXBudWxsbWFwanNvbm1hcGJpbm1hcG5lc3RlZGRlZXBhcnJheXRleHRwbGFpbiBib2xkYm9sZGJvbGQgdGFpbEMDBQMEAwQDBEMBBgQFBAZEAQUJAQcAAAEAAAABAgEABEEHQgABFAB3EGhlbGxvIOKckyDQvNC40YB9KnxAYAAAeH52AgFhfQEBYnUCdwF4dwF5BAECA/p3BXZhbHVldwFheXYBAWt3AXZ3AmkwdwJpMXh+dgEFaW1hZ2V3BXgucG5nAcj/h7EPAQoA";

    [TestMethod]
    [DataRow(Yjs13UpdateV1, false, DisplayName = "yjs 13.6.32, v1 update")]
    [DataRow(Yjs13UpdateV2, true, DisplayName = "yjs 13.6.32, v2 update")]
    [DataRow(Yjs14UpdateV1, false, DisplayName = "yjs 14.0.0-16, v1 update")]
    [DataRow(Yjs14UpdateV2, true, DisplayName = "yjs 14.0.0-16, v2 update")]
    public void ReadsUpdateProducedByYjs(string base64Update, bool v2)
    {
        var doc = Apply(base64Update, v2);

        var map = doc.GetMap("map");
        Assert.AreEqual("hello \u2713 \u043c\u0438\u0440", map["str"].ToObject());
        Assert.AreEqual(42, Convert.ToInt32(map["int"].ToObject()));
        Assert.AreEqual(3.5, Convert.ToDouble(map["float"].ToObject()));
        Assert.AreEqual(true, map["bool"].ToObject());
        Assert.IsNull(map["null"].ToObject());
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 250 }, (byte[])map["bin"].ToObject());

        var json = (IDictionary<string, object>)map["json"].ToObject();
        Assert.AreEqual(1L, Convert.ToInt64(json["a"]));
        CollectionAssert.AreEqual(new[] { "x", "y" }, ((IList<object>)json["b"]).ToArray());

        Assert.AreEqual("value", ((YMap)map["nested"].ToObject())["deep"].ToObject());

        // 'array' had its second element deleted in a later transaction.
        var array = doc.GetArray("array").ToArray().Select(v => v.ToObject()).ToArray();
        Assert.AreEqual(4, array.Length);
        Assert.AreEqual("a", array[0]);
        Assert.AreEqual(false, array[1]);
        CollectionAssert.AreEqual(new[] { "i0", "i1" }, ((YArray)array[3]).Select(v => v.ToObject()).ToList());

        Assert.AreEqual("plain bold tail", doc.GetText("text").ToString());
        var delta = doc.GetText("text").ToDelta();
        Assert.AreEqual(true, delta[1].Attributes["bold"], "formatting attribute lost");
        Assert.IsTrue(delta.Any(d => d is Delta.Insert { Value.Kind: YValueKind.Object } && d.Inserted()["image"] == "x.png"),
            "embedded object lost");
    }

    [TestMethod]
    [DataRow(Yjs13UpdateV1, false, DisplayName = "v1 re-encode")]
    [DataRow(Yjs13UpdateV2, true, DisplayName = "v2 re-encode")]
    public void ReEncodesToAnUpdateYjsCanRead(string base64Update, bool v2)
    {
        var doc = Apply(base64Update, v2);
        var reEncoded = v2 ? doc.EncodeStateAsUpdate(format: UpdateFormat.V2) : doc.EncodeStateAsUpdate();

        // Round-trips through Ycs itself...
        var copy = new YDoc();
        if (v2)
        {
            copy.ApplyUpdate(reEncoded, format: UpdateFormat.V2);
        }
        else
        {
            copy.ApplyUpdate(reEncoded);
        }

        Assert.AreEqual("plain bold tail", copy.GetText("text").ToString());
        Assert.AreEqual("value", ((YMap)copy.GetMap("map")["nested"].ToObject())["deep"].ToObject());

        // ...and, for v1, is byte-identical to what Yjs itself emitted.
        if (!v2)
        {
            CollectionAssert.AreEqual(Convert.FromBase64String(base64Update), reEncoded,
                "v1 re-encoding drifted from the bytes Yjs produced");
        }
    }

    [TestMethod]
    public void StateVectorMatchesYjs()
    {
        var doc = Apply(Yjs13UpdateV1, v2: false);
        var sv = doc.EncodeStateVector();

        // One client, and a diff against its own state vector carries no structs.
        var diff = doc.EncodeStateAsUpdate(sv);
        var empty = new YDoc();
        empty.ApplyUpdate(diff);
        Assert.AreEqual(0, empty.GetArray("array").Length);
    }

    [TestMethod]
    public void ReadsTheXmlTypesProducedByYjs()
    {
        // A y-prosemirror-shaped document: Y.XmlFragment > Y.XmlElement("p") > Y.XmlText("hi").
        const string XmlUpdateV1 = "AQP6w//4BAAHAQN4bWwDAXAHAPrD//gEAAYEAPrD//gEAQJoaQA=";

        var doc = Apply(XmlUpdateV1, v2: false);
        Assert.AreEqual("<p>hi</p>", doc.GetXmlFragment("xml").ToString());
    }

    [TestMethod]
    public void ClientIdsDoNotCollideWhenDocumentsAreCreatedTogether()
    {
        // A clock-seeded Random hands the same client id to every document created in the same
        // tick, so two peers would share an id and silently corrupt the merge. NOTE: 'new Random()'
        // stopped being clock-seeded in .NET 6, so this test cannot reproduce the original failure
        // on the TFM it runs under - the hazard is real for the netstandard2.0 consumers (.NET
        // Framework, Unity). It stands as a guard on the invariant itself: ids must not repeat.
        var ids = new HashSet<long>();
        for (int i = 0; i < 512; i++)
        {
            ids.Add(new YDoc().ClientId);
        }

        Assert.AreEqual(512, ids.Count, "duplicate client ids");
    }

    [TestMethod]
    public void OverlappingDeleteRangesAreCoalesced()
    {
        // Yjs' sortAndMergeDeleteSet collapses ranges that overlap, not just ranges that touch
        // (src/utils/DeleteSet.js). Coalescing only exact neighbours leaves a delete set with
        // duplicate coverage, which is both larger on the wire and not what a JS peer produces.
        var ds = new DeleteSet();
        ds.Add(client: 1, clock: 0, length: 5);     // 0..5
        ds.Add(client: 1, clock: 3, length: 5);     // 3..8, overlaps
        ds.Add(client: 1, clock: 8, length: 2);     // 8..10, adjacent
        ds.Add(client: 1, clock: 2, length: 2);     // 2..4, fully contained
        ds.Add(client: 1, clock: 20, length: 1);    // 20..21, disjoint

        ds.SortAndMergeDeleteSet();

        var ranges = ds.Clients[1];
        Assert.AreEqual(2, ranges.Count, "overlapping ranges were not coalesced");
        Assert.AreEqual(0, ranges[0].Clock);
        Assert.AreEqual(10, ranges[0].Length);
        Assert.AreEqual(20, ranges[1].Clock);
        Assert.AreEqual(1, ranges[1].Length);
    }

    private static YDoc Apply(string base64Update, bool v2)
    {
        var doc = new YDoc();
        var update = Convert.FromBase64String(base64Update);
        if (v2)
        {
            doc.ApplyUpdate(update, format: UpdateFormat.V2);
        }
        else
        {
            doc.ApplyUpdate(update);
        }

        return doc;
    }

    [TestMethod]
    public void PendingStructsAreReReadWhenTheyIntegrate()
    {
        // A struct of client 1 at clock 1 arrives before clock 0; meanwhile the root is realized and an
        // update observer is attached. Yjs keeps pending structs encoded and reads them again, so the
        // struct lands in the real Y.Text (and encoding the transaction does not hit a stale placeholder).
        var doc = new YDoc();
        var updates = 0;
        doc.Updated += (_, _) => updates++;
        doc.ApplyUpdate(Convert.FromBase64String("AQEBAQQBBHRleHQBYgA="));
        var text = doc.GetText("text");
        doc.ApplyUpdate(Convert.FromBase64String("AQEBAAQBBW90aGVyAWEA"));

        Assert.AreEqual("b", text.ToString());
        Assert.AreEqual("a", doc.GetText("other").ToString());
        Assert.IsTrue(updates > 0);
    }

    [TestMethod]
    public void IndependentUpdateIntegratesWhileAnotherIsPending()
    {
        var doc = new YDoc();
        doc.ApplyUpdate(Convert.FromBase64String("AQEFGMQGDgYPAUEA")); // depends on structs we do not have
        doc.ApplyUpdate(Convert.FromBase64String("AQEHAAQBBHRleHQBZwA="));
        Assert.AreEqual("g", doc.GetText("text").ToString());
    }
}
