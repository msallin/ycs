// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs
{
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

        [DataTestMethod]
        [DataRow(Yjs13UpdateV1, false, DisplayName = "yjs 13.6.32, v1 update")]
        [DataRow(Yjs13UpdateV2, true, DisplayName = "yjs 13.6.32, v2 update")]
        [DataRow(Yjs14UpdateV1, false, DisplayName = "yjs 14.0.0-16, v1 update")]
        [DataRow(Yjs14UpdateV2, true, DisplayName = "yjs 14.0.0-16, v2 update")]
        public void ReadsUpdateProducedByYjs(string base64Update, bool v2)
        {
            var doc = Apply(base64Update, v2);

            var map = doc.GetMap("map");
            Assert.AreEqual("hello \u2713 \u043c\u0438\u0440", map.Get("str"));
            Assert.AreEqual(42, Convert.ToInt32(map.Get("int")));
            Assert.AreEqual(3.5, Convert.ToDouble(map.Get("float")));
            Assert.AreEqual(true, map.Get("bool"));
            Assert.IsNull(map.Get("null"));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 250 }, (byte[])map.Get("bin"));

            var json = (IDictionary<string, object>)map.Get("json");
            Assert.AreEqual(1L, Convert.ToInt64(json["a"]));
            CollectionAssert.AreEqual(new[] { "x", "y" }, ((IList<object>)json["b"]).ToArray());

            Assert.AreEqual("value", ((YMap)map.Get("nested")).Get("deep"));

            // 'array' had its second element deleted in a later transaction.
            var array = doc.GetArray("array").ToArray();
            Assert.AreEqual(4, array.Count);
            Assert.AreEqual("a", array[0]);
            Assert.AreEqual(false, array[1]);
            CollectionAssert.AreEqual(new[] { "i0", "i1" }, ((YArray)array[3]).ToArray().ToArray());

            Assert.AreEqual("plain bold tail", doc.GetText("text").ToString());
            var delta = doc.GetText("text").ToDelta();
            Assert.AreEqual(true, delta[1].Attributes["bold"], "formatting attribute lost");
            Assert.IsTrue(delta.Any(d => d.Insert is IDictionary<string, object> embed && (string)embed["image"] == "x.png"),
                "embedded object lost");
        }

        [DataTestMethod]
        [DataRow(Yjs13UpdateV1, false, DisplayName = "v1 re-encode")]
        [DataRow(Yjs13UpdateV2, true, DisplayName = "v2 re-encode")]
        public void ReEncodesToAnUpdateYjsCanRead(string base64Update, bool v2)
        {
            var doc = Apply(base64Update, v2);
            var reEncoded = v2 ? doc.EncodeStateAsUpdateV2() : doc.EncodeStateAsUpdateV1();

            // Round-trips through Ycs itself...
            var copy = new YDoc();
            if (v2)
            {
                copy.ApplyUpdateV2(reEncoded);
            }
            else
            {
                copy.ApplyUpdateV1(reEncoded);
            }

            Assert.AreEqual("plain bold tail", copy.GetText("text").ToString());
            Assert.AreEqual("value", ((YMap)copy.GetMap("map").Get("nested")).Get("deep"));

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
            var sv = doc.EncodeStateVectorV1();

            // One client, and a diff against its own state vector carries no structs.
            var diff = doc.EncodeStateAsUpdateV1(sv);
            var empty = new YDoc();
            empty.ApplyUpdateV1(diff);
            Assert.AreEqual(0, empty.GetArray("array").Length);
        }

        [TestMethod]
        public void XmlTypesFailWithAClearMessage()
        {
            // A y-prosemirror-shaped document: Y.XmlFragment > Y.XmlElement > Y.XmlText.
            // Ycs does not implement those types; the failure must say so rather than
            // surfacing as an opaque error deep inside the struct reader.
            const string XmlUpdateV1 = "AQP6w//4BAAHAQN4bWwDAXAHAPrD//gEAAYEAPrD//gEAQJoaQA=";

            var ex = Assert.ThrowsException<NotSupportedException>(
                () => Apply(XmlUpdateV1, v2: false));
            StringAssert.Contains(ex.Message, "XML");
        }

        private static YDoc Apply(string base64Update, bool v2)
        {
            var doc = new YDoc();
            var update = Convert.FromBase64String(base64Update);
            if (v2)
            {
                doc.ApplyUpdateV2(update);
            }
            else
            {
                doc.ApplyUpdateV1(update);
            }

            return doc;
        }
    }
}
