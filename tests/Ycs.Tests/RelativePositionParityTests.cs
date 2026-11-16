// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Covers what <see cref="RelativePositionTests"/> (pre-existing, left untouched) does not: byte-
/// level compatibility with real Yjs 13.6.32's encoded relative positions, the JSON form, and
/// <see cref="RelativePosition"/>/<see cref="AbsolutePosition"/> now being public.
/// <br/>
/// The base64 fixtures were produced by real Yjs 13.6.32 (yjs13, pinned client id 1) against a doc
/// built like this (see the generating script recorded next to <see cref="UpdateV1"/>):
/// <code>
///   const doc = new Y.Doc({ gc: false }); doc.clientID = 1;
///   doc.transact(() => {
///     doc.getText('text').insert(0, 'hello world');
///     doc.getArray('array').insert(0, ['a', 'b', 'c']);
///   });
///   doc.getText('text').delete(2, 3);   // 'hello world' -> 'he world' (length 8)
///   doc.getArray('array').delete(1, 1); // ['a','b','c'] -> ['a','c']   (length 2)
/// </code>
/// </summary>
[TestClass]
public class RelativePositionParityTests
{
    // Generated with (interop/js, `node -e "..."`, yjs13 = 13.6.32), against the doc built above:
    //   for (const [type, idx, assoc] of [[text,0,0],[text,0,-1],[text,5,0],[text,5,-1],
    //                                      [text,text.length,0],[text,text.length,-1],
    //                                      [array,1,0],[array,1,-1],[array,array.length,0]]) {
    //     const rpos = Y.createRelativePositionFromTypeIndex(type, idx, assoc)
    //     console.log(b64(Y.encodeRelativePosition(rpos)))
    //   }
    private const string UpdateV1 = "AQYBAAQBBHRleHQCaGWEAQEDbGxvhAEEBiB3b3JsZAgBBWFycmF5AXcBYYgBCwF3AWKIAQwBdwFjAQECAgMMAQ==";

    private const string TextRight0 = "AAEAAA==";
    private const string TextLeft0 = "AQR0ZXh0QQ==";
    private const string TextRight5 = "AAEIAA==";
    private const string TextLeft5 = "AAEHQQ==";
    private const string TextEndRight = "AQR0ZXh0AA==";
    private const string TextEndLeft = "AAEKQQ==";
    private const string ArrayRight1 = "AAENAA==";
    private const string ArrayLeft1 = "AAELQQ==";
    private const string ArrayEndRight = "AQVhcnJheQA=";

    private static byte[] B(string base64) => Convert.FromBase64String(base64);

    private static YDoc BuildDoc()
    {
        var doc = new YDoc();
        doc.ApplyUpdateV1(B(UpdateV1));
        return doc;
    }

    [TestMethod]
    [DataRow(TextRight0, "text", 0, 0, DisplayName = "text, index 0, right-assoc")]
    [DataRow(TextLeft0, "text", 0, -1, DisplayName = "text, index 0, left-assoc")]
    [DataRow(TextRight5, "text", 5, 0, DisplayName = "text, index 5, right-assoc")]
    [DataRow(TextLeft5, "text", 5, -1, DisplayName = "text, index 5, left-assoc")]
    [DataRow(TextEndRight, "text", 8, 0, DisplayName = "text, end index, right-assoc")]
    [DataRow(TextEndLeft, "text", 8, -1, DisplayName = "text, end index, left-assoc")]
    [DataRow(ArrayRight1, "array", 1, 0, DisplayName = "array, index 1, right-assoc")]
    [DataRow(ArrayLeft1, "array", 1, -1, DisplayName = "array, index 1, left-assoc")]
    [DataRow(ArrayEndRight, "array", 2, 0, DisplayName = "array, end index, right-assoc")]
    public void DecodesYjsRelativePositionAndResolvesToTheOriginalIndex(string base64, string typeName, int expectedIndex, int expectedAssoc)
    {
        var doc = BuildDoc();
        var type = typeName == "text" ? (YType)doc.GetText("text") : doc.GetArray("array");

        var rpos = RelativePosition.DecodeRelativePosition(B(base64));
        Assert.AreEqual(expectedAssoc, rpos.Assoc);

        var abs = AbsolutePosition.CreateAbsolutePositionFromRelativePosition(rpos, doc);
        Assert.IsNotNull(abs);
        Assert.AreSame(type, abs.Type);
        Assert.AreEqual(expectedIndex, abs.Index);
        Assert.AreEqual(expectedAssoc, abs.Assoc);
    }

    [TestMethod]
    [DataRow(TextRight0, DisplayName = "text, index 0, right-assoc")]
    [DataRow(TextLeft0, DisplayName = "text, index 0, left-assoc")]
    [DataRow(TextRight5, DisplayName = "text, index 5, right-assoc")]
    [DataRow(TextLeft5, DisplayName = "text, index 5, left-assoc")]
    [DataRow(TextEndRight, DisplayName = "text, end index, right-assoc")]
    [DataRow(TextEndLeft, DisplayName = "text, end index, left-assoc")]
    [DataRow(ArrayRight1, DisplayName = "array, index 1, right-assoc")]
    [DataRow(ArrayLeft1, DisplayName = "array, index 1, left-assoc")]
    [DataRow(ArrayEndRight, DisplayName = "array, end index, right-assoc")]
    public void RoundTripsYjsRelativePositionBytes(string base64)
    {
        var rpos = RelativePosition.DecodeRelativePosition(B(base64));
        CollectionAssert.AreEqual(B(base64), RelativePosition.EncodeRelativePosition(rpos));
    }

    [TestMethod]
    public void RelativePositionToJsonMatchesYjsShape()
    {
        // Y.decodeRelativePosition(base64ToBuf(TextRight0)) then Y.relativePositionToJSON(...) gives
        // {"item":{"client":1,"clock":0},"assoc":0} - no 'tname', because the wire format only ever
        // carries one of item/tname/type (see RelativePosition.Write): a decoded position has
        // exactly one of them set, even though an in-memory position built straight from
        // createRelativePositionFromTypeIndex against a root-level type would have both 'item' and
        // 'tname' populated (createRelativePosition always fills 'tname' for a root type, in
        // addition to 'item' when one was found).
        var withItem = RelativePosition.DecodeRelativePosition(B(TextRight0));
        var json = withItem.RelativePositionToJSON();
        Assert.IsFalse(json.ContainsKey("tname"));
        Assert.IsFalse(json.ContainsKey("type"));
        var item = (IDictionary<string, object>)json["item"];
        Assert.AreEqual(1L, Convert.ToInt64(item["client"]));
        Assert.AreEqual(0L, Convert.ToInt64(item["clock"]));
        Assert.AreEqual(0, json["assoc"]);

        // Y.decodeRelativePosition(base64ToBuf(TextLeft0)) then toJSON gives
        // {"tname":"text","assoc":-1} - no 'item'/'type' key at all, matching Yjs only setting the
        // properties that are actually present.
        var tnameOnly = RelativePosition.DecodeRelativePosition(B(TextLeft0));
        var json2 = tnameOnly.RelativePositionToJSON();
        Assert.AreEqual("text", json2["tname"]);
        Assert.IsFalse(json2.ContainsKey("item"));
        Assert.IsFalse(json2.ContainsKey("type"));
        Assert.AreEqual(-1, json2["assoc"]);
    }

    [TestMethod]
    public void RelativePositionJsonRoundTrips()
    {
        foreach (var base64 in new[] { TextRight0, TextLeft0, TextRight5, TextLeft5, TextEndRight, TextEndLeft, ArrayRight1, ArrayLeft1, ArrayEndRight })
        {
            var rpos = RelativePosition.DecodeRelativePosition(B(base64));
            var json = rpos.RelativePositionToJSON();
            var roundTripped = RelativePosition.CreateRelativePositionFromJSON(json);

            Assert.IsTrue(RelativePosition.CompareRelativePositions(rpos, roundTripped), $"Mismatch for {base64}");
            CollectionAssert.AreEqual(B(base64), RelativePosition.EncodeRelativePosition(roundTripped));
        }
    }

    [TestMethod]
    public void CompareRelativePositionsIsNullSafeAndStructural()
    {
        Assert.IsTrue(RelativePosition.CompareRelativePositions(null, null));

        var a = RelativePosition.DecodeRelativePosition(B(TextRight0));
        var b = RelativePosition.DecodeRelativePosition(B(TextRight0));
        var c = RelativePosition.DecodeRelativePosition(B(TextRight5));

        Assert.IsFalse(RelativePosition.CompareRelativePositions(a, null));
        Assert.IsFalse(RelativePosition.CompareRelativePositions(null, a));
        Assert.IsTrue(RelativePosition.CompareRelativePositions(a, b));
        Assert.IsFalse(RelativePosition.CompareRelativePositions(a, c));
    }

    [TestMethod]
    public void CreateRelativePositionFromTypeIndexIsAnAliasForFromTypeIndex()
    {
        var doc = BuildDoc();
        var text = doc.GetText("text");

        var viaAlias = RelativePosition.CreateRelativePositionFromTypeIndex(text, 3, -1);
        var viaOriginal = RelativePosition.FromTypeIndex(text, 3, -1);

        Assert.IsTrue(RelativePosition.CompareRelativePositions(viaAlias, viaOriginal));
    }

    [TestMethod]
    public void CreateAbsolutePositionFromRelativePositionIsAnAliasForTryCreate()
    {
        var doc = BuildDoc();
        var rpos = RelativePosition.DecodeRelativePosition(B(TextRight5));

        var viaAlias = AbsolutePosition.CreateAbsolutePositionFromRelativePosition(rpos, doc);
        var viaOriginal = AbsolutePosition.TryCreateFromRelativePosition(rpos, doc);

        Assert.AreEqual(viaOriginal.Index, viaAlias.Index);
        Assert.AreEqual(viaOriginal.Assoc, viaAlias.Assoc);
        Assert.AreSame(viaOriginal.Type, viaAlias.Type);
    }

    [TestMethod]
    public void FollowUndoneDeletionsFalseStillResolvesWhenThereIsNoRedoHistory()
    {
        // Sanity coverage for the new 'followUndoneDeletions' parameter: with no undo/redo in play,
        // both modes must agree (the divergence only matters once 'Redone' chains exist).
        var doc = BuildDoc();
        var rpos = RelativePosition.DecodeRelativePosition(B(TextRight5));

        var following = AbsolutePosition.TryCreateFromRelativePosition(rpos, doc, followUndoneDeletions: true);
        var notFollowing = AbsolutePosition.TryCreateFromRelativePosition(rpos, doc, followUndoneDeletions: false);

        Assert.AreEqual(following.Index, notFollowing.Index);
        Assert.AreEqual(following.Assoc, notFollowing.Assoc);
        Assert.AreSame(following.Type, notFollowing.Type);
    }

    [TestMethod]
    public void RelativePositionIsPublicApi()
    {
        // Compile-time proof that RelativePosition/AbsolutePosition are usable from outside the
        // assembly's InternalsVisibleTo allowlist - if either type (or the members exercised
        // throughout this file) were still 'internal', this whole file would not compile in a
        // consumer without InternalsVisibleTo, which is exactly the gap this task closes.
        Assert.IsTrue(typeof(RelativePosition).IsPublic);
        Assert.IsTrue(typeof(AbsolutePosition).IsPublic);
    }
}
