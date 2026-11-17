using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
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
        doc.ApplyUpdate(B(UpdateV1));
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

        var rpos = RelativePosition.Decode(B(base64));
        Assert.AreEqual(expectedAssoc, (int)rpos.Assoc);

        Assert.IsTrue(rpos.TryResolve(doc, out var abs));
        Assert.AreSame(type, abs.Type);
        Assert.AreEqual(expectedIndex, abs.Index);
        Assert.AreEqual(expectedAssoc, (int)abs.Assoc);
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
        var rpos = RelativePosition.Decode(B(base64));
        CollectionAssert.AreEqual(B(base64), rpos.Encode());
    }

    private static string JsonOf(RelativePosition position)
    {
        using var stream = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            position.WriteJson(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [TestMethod]
    [DataRow(TextRight0, """{"item":{"client":1,"clock":0},"assoc":0}""")]
    [DataRow(TextLeft0, """{"tname":"text","assoc":-1}""")]
    [DataRow(ArrayRight1, """{"item":{"client":1,"clock":13},"assoc":0}""")]
    public void WriteJson_DecodedFromYjs_IsTheObjectOfRelativePositionToJSON(string base64, string yjsJson)
    {
        // What Y.relativePositionToJSON(Y.decodeRelativePosition(bytes)) gives: the wire format carries one
        // of item/tname/type, so a decoded position has exactly one set.
        Assert.AreEqual(yjsJson, JsonOf(RelativePosition.Decode(B(base64))));
    }

    [TestMethod]
    public void WriteJson_PositionInRootText_NamesTheItemOnly()
    {
        // Yjs' createRelativePosition fills tname for a root type in addition to the item, which the bytes drop.
        var rpos = RelativePosition.FromIndex(BuildDoc().GetText("text"), 5);

        Assert.AreEqual("""{"item":{"client":1,"clock":8},"assoc":0}""", JsonOf(rpos));
    }

    [TestMethod]
    public void FromJson_OfWriteJson_IsEqualAndEncodesTheSameBytes()
    {
        foreach (var base64 in new[] { TextRight0, TextLeft0, TextRight5, TextLeft5, TextEndRight, TextEndLeft, ArrayRight1, ArrayLeft1, ArrayEndRight })
        {
            var rpos = RelativePosition.Decode(B(base64));
            using var json = JsonDocument.Parse(JsonOf(rpos));
            var roundTripped = RelativePosition.FromJson(json.RootElement);

            Assert.AreEqual(rpos, roundTripped, $"Mismatch for {base64}");
            CollectionAssert.AreEqual(B(base64), roundTripped.Encode());
        }
    }

    [TestMethod]
    public void Equals_StructuralAndHashAgrees()
    {
        var a = RelativePosition.Decode(B(TextRight0));
        var b = RelativePosition.Decode(B(TextRight0));
        var c = RelativePosition.Decode(B(TextRight5));

        Assert.AreEqual((true, true, false), (a == b, a.GetHashCode() == b.GetHashCode(), a == c));
    }

    [TestMethod]
    public void FromIndex_OfTheSamePlace_IsEqualToTheDecodedBytesOnceEncoded()
    {
        var text = BuildDoc().GetText("text");

        CollectionAssert.AreEqual(B(TextLeft5), RelativePosition.FromIndex(text, 5, Assoc.Before).Encode());
    }

    [TestMethod]
    public void EncodeToBufferWriter_WritesTheBytesOfEncode()
    {
        var rpos = RelativePosition.Decode(B(TextRight5));
        var buffer = new ArrayBufferWriter<byte>();

        rpos.Encode(buffer);

        CollectionAssert.AreEqual(rpos.Encode(), buffer.WrittenSpan.ToArray());
    }

    [TestMethod]
    public void DefaultPosition_LocatesNothing()
    {
        var doc = BuildDoc();

        Assert.IsFalse(default(RelativePosition).TryResolve(doc, out var absolute));
        Assert.AreEqual(default, absolute);
        Assert.ThrowsExactly<InvalidOperationException>(() => default(RelativePosition).Encode());
    }

    [TestMethod]
    public void Decode_EmptyBytes_ThrowsYUpdateException()
    {
        Assert.ThrowsExactly<YUpdateException>(() => RelativePosition.Decode([]));
    }

    [TestMethod]
    public void FollowUndoneDeletionsFalseStillResolvesWhenThereIsNoRedoHistory()
    {
        // Sanity coverage for the new 'followUndoneDeletions' parameter: with no undo/redo in play,
        // both modes must agree (the divergence only matters once 'Redone' chains exist).
        var doc = BuildDoc();
        var rpos = RelativePosition.Decode(B(TextRight5));

        Assert.IsTrue(rpos.TryResolve(doc, out var following, followUndoneDeletions: true));
        Assert.IsTrue(rpos.TryResolve(doc, out var notFollowing, followUndoneDeletions: false));

        Assert.AreEqual(following, notFollowing);
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
