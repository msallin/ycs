using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Ports of the Yjs 13.6.32 y-text formatting tests (tests/y-text.tests.js), with the deltas Yjs expects.</summary>
[TestClass]
public class YTextFormattingYjsTests : YTestBase
{
    private static string Json(IEnumerable<Delta> delta) => CanonicalJson.Write(delta.ToList());

    private static string Json(string json) => CanonicalJson.Write(CanonicalJson.FromJson(JsonElement.Parse(json)));

    private static YAttributes Bold => new Dictionary<string, YValue> { ["bold"] = true };

    // A delta as Yjs' JSON writes it: {insert}, {retain} or {delete}, and attributes.
    private static List<Delta> ParseDelta(string json)
    {
        var delta = new List<Delta>();
        foreach (var op in YValue.Parse(json).EnumerateArray())
        {
            YAttributes attributes = op.TryGetMember("attributes", out var a) ? YAttributes.From(a.EnumerateObject()) : null;
            if (op.TryGetMember("insert", out var insert))
            {
                delta.Add(new Delta.Insert(insert) { Attributes = attributes });
            }
            else if (op.TryGetMember("retain", out var retain))
            {
                delta.Add(new Delta.Retain(retain.GetInt32()) { Attributes = attributes });
            }
            else
            {
                delta.Add(new Delta.Delete(op["delete"].GetInt32()));
            }
        }

        return delta;
    }

    [TestMethod]
    public void ApplyDelta_LineBreaksWithAndWithoutFormat_AreNotMerged()
    {
        // testNotMergeEmptyLinesFormat
        var text = new YDoc().GetText("test");
        const string content = """[{"insert":"Text"},{"insert":"\n","attributes":{"title":true}},{"insert":"\nText"},{"insert":"\n","attributes":{"title":true}}]""";

        text.ApplyDelta(ParseDelta(content));

        Assert.AreEqual(Json(content), Json(text.ToDelta()));
    }

    [TestMethod]
    public void ApplyDelta_DeleteBeforeAFormattedRetain_KeepsTheAttributes()
    {
        // testPreserveAttributesThroughDelete
        var text = new YDoc().GetText("test");
        text.ApplyDelta(ParseDelta("""[{"insert":"Text"},{"insert":"\n","attributes":{"title":true}},{"insert":"\n"}]"""));

        text.ApplyDelta(ParseDelta("""[{"retain":4},{"delete":1},{"retain":1,"attributes":{"title":true}}]"""));

        Assert.AreEqual(Json("""[{"insert":"Text"},{"insert":"\n","attributes":{"title":true}}]"""), Json(text.ToDelta()));
    }

    [TestMethod]
    public void ApplyDelta_SharedTypeEmbed_ReachesAPeerWithItsContentAndFiresTheObserver()
    {
        // testTypesAsEmbed
        Init(users: 2);
        var text0 = Texts[Users[0]];
        var text1 = Texts[Users[1]];
        text0.ApplyDelta([new Delta.Insert(new YMap { ["key"] = "val" })]);
        var local = ((Delta.Insert)text0.ToDelta()[0]).Value.TryGetSharedType<YMap>(out var localMap) ? localMap!.ToJsonString() : null;
        IReadOnlyList<Delta> fired = null;
        text1.Observe(e => fired = e.Delta);

        Connector.FlushAllMessages();

        var remote = text1.ToDelta();
        Assert.AreEqual(
            ("""{"key":"val"}""", 1, """{"key":"val"}""", 1),
            (local, fired?.Count, remote[0].Inserted().TryGetSharedType<YMap>(out var remoteMap) ? remoteMap!.ToJsonString() : null, remote.Count));
    }

    [TestMethod]
    public void Observe_RemoteFormatOfOneAttribute_DeltaOnlyContainsTheChangedAttribute()
    {
        // testFormattingDeltaUnnecessaryAttributeChange (issue 344)
        Init(users: 2);
        var text0 = Texts[Users[0]];
        var text1 = Texts[Users[1]];
        text0.Insert(0, "\n", new Dictionary<string, YValue> { ["PARAGRAPH_STYLES"] = "normal", ["LIST_STYLES"] = "bullet" });
        text0.Insert(1, "abc", new Dictionary<string, YValue> { ["PARAGRAPH_STYLES"] = "normal" });
        Connector.FlushAllMessages();
        var deltas = new List<string>();
        text0.Observe(e => deltas.Add(Json(e.Delta)));
        text1.Observe(e => deltas.Add(Json(e.Delta)));

        text1.Format(0, 1, new Dictionary<string, YValue> { ["LIST_STYLES"] = "number" });
        Connector.FlushAllMessages();

        const string expected = """[{"attributes":{"LIST_STYLES":"number"},"retain":1}]""";
        CollectionAssert.AreEqual(new[] { expected, expected }, deltas.Where(d => d != "[]").ToList());
    }

    [TestMethod]
    public void Observe_ConcurrentFormatsOfOverlappingRanges_DeltaRetainsThenClearsTheOverlap()
    {
        // testDeltaAfterConcurrentFormatting
        Init(users: 2);
        var text0 = Texts[Users[0]];
        var text1 = Texts[Users[1]];
        text0.Insert(0, "abcde");
        Connector.FlushAllMessages();
        text0.Format(0, 3, Bold);
        text1.Format(2, 2, Bold);
        var deltas = new List<string>();
        text1.Observe(e =>
        {
            if (e.Delta.Count > 0)
            {
                deltas.Add(Json(e.Delta));
            }
        });

        Connector.FlushAllMessages();

        CollectionAssert.AreEqual(new[] { """[{"attributes":{"bold":true},"retain":3},{"attributes":{"bold":null},"retain":2}]""" }, deltas);
    }

    [TestMethod]
    [DataRow(1L, 2L)]
    [DataRow(2L, 1L)]
    public void Format_ConcurrentFormatOfTheMiddleLineBreak_KeepsTheMiddleUrlOnBothDocuments(long client1, long client2)
    {
        // testFormattingBug (yjs pull 32): the second document formats before it has any content.
        var doc1 = new YDoc(new YDocOptions { ClientId = client1 });
        var doc2 = new YDoc(new YDocOptions { ClientId = client2 });
        var text1 = doc1.GetText("t");
        text1.Insert(0, "\n\n\n");
        text1.Format(0, 3, new Dictionary<string, YValue> { ["url"] = "http://example.com" });
        text1.Format(1, 1, new Dictionary<string, YValue> { ["url"] = "http://docs.yjs.dev" });
        doc2.GetText("t").Format(1, 1, new Dictionary<string, YValue> { ["url"] = "http://docs.yjs.dev" });

        doc2.ApplyUpdate(doc1.EncodeStateAsUpdate());

        const string expected = """[{"attributes":{"url":"http://example.com"},"insert":"\n"},{"attributes":{"url":"http://docs.yjs.dev"},"insert":"\n"},{"attributes":{"url":"http://example.com"},"insert":"\n"}]""";
        Assert.AreEqual((Json(expected), Json(expected)), (Json(text1.ToDelta()), Json(doc2.GetText("t").ToDelta())));
    }

    [TestMethod]
    public void Format_RemovingTheMiddleOfABoldRange_LeavesNoRedundantFormatting()
    {
        // testDeleteFormatting
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "Attack ships on fire off the shoulder of Orion.");
        var peer = new YDoc(new YDocOptions { ClientId = 2 });
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());

        text.Format(13, 7, Bold);
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());
        text.Format(16, 4, new Dictionary<string, YValue> { ["bold"] = YValue.Null });
        peer.ApplyUpdate(doc.EncodeStateAsUpdate());

        const string expected = """[{"insert":"Attack ships "},{"attributes":{"bold":true},"insert":"on "},{"insert":"fire off the shoulder of Orion."}]""";
        Assert.AreEqual((Json(expected), Json(expected)), (Json(text.ToDelta()), Json(peer.GetText("t").ToDelta())));
    }

    [TestMethod]
    public void ApplyDelta_ReplacingATableCellLineAttributeByAList_YieldsTheDeltaOfYjs()
    {
        // testDeltaBug (issue 474)
        var text = new YDoc().GetText("t");
        text.ApplyDelta(ParseDelta(InitialDelta));

        text.ApplyDelta(ParseDelta("""[{"retain":12},{"insert":"-"}]"""));
        text.ApplyDelta(ParseDelta("""[{"retain":13},{"insert":" "}]"""));
        text.ApplyDelta(ParseDelta(AddingList));

        Assert.AreEqual(Json(ExpectedDelta), Json(text.ToDelta()));
    }

    private const string InitialDelta = """[{"attributes":{"block-id":"block-28eea923-9cbb-4b6f-a950-cf7fd82bc087"},"insert":"\n"},{"attributes":{"table-col":{"width":"150"}},"insert":"\n\n\n"},{"attributes":{"block-id":"block-9144be72-e528-4f91-b0b2-82d20408e9ea","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-6kv2ls","cell":"cell-apba4k"},"row":"row-6kv2ls","cell":"cell-apba4k","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-639adacb-1516-43ed-b272-937c55669a1c","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-6kv2ls","cell":"cell-a8qf0r"},"row":"row-6kv2ls","cell":"cell-a8qf0r","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-6302ca4a-73a3-4c25-8c1e-b542f048f1c6","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-6kv2ls","cell":"cell-oi9ikb"},"row":"row-6kv2ls","cell":"cell-oi9ikb","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-ceeddd05-330e-4f86-8017-4a3a060c4627","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-d1sv2g","cell":"cell-dt6ks2"},"row":"row-d1sv2g","cell":"cell-dt6ks2","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-37b19322-cb57-4e6f-8fad-0d1401cae53f","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-d1sv2g","cell":"cell-qah2ay"},"row":"row-d1sv2g","cell":"cell-qah2ay","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-468a69b5-9332-450b-9107-381d593de249","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-d1sv2g","cell":"cell-fpcz5a"},"row":"row-d1sv2g","cell":"cell-fpcz5a","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-26b1d252-9b2e-4808-9b29-04e76696aa3c","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-pflz90","cell":"cell-zrhylp"},"row":"row-pflz90","cell":"cell-zrhylp","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-6af97ba7-8cf9-497a-9365-7075b938837b","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-pflz90","cell":"cell-s1q9nt"},"row":"row-pflz90","cell":"cell-s1q9nt","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-107e273e-86bc-44fd-b0d7-41ab55aca484","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-pflz90","cell":"cell-20b0j9"},"row":"row-pflz90","cell":"cell-20b0j9","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-38161f9c-6f6d-44c5-b086-54cc6490f1e3"},"insert":"\n"},{"insert":"Content after table"},{"attributes":{"block-id":"block-15630542-ef45-412d-9415-88f0052238ce"},"insert":"\n"}]""";

    private const string AddingList = """[{"retain":12},{"delete":2},{"retain":1,"attributes":{"table-cell-line":null,"list":{"rowspan":"1","colspan":"1","row":"row-pflz90","cell":"cell-20b0j9","list":"bullet"}}}]""";

    private const string ExpectedDelta = """[{"attributes":{"block-id":"block-28eea923-9cbb-4b6f-a950-cf7fd82bc087"},"insert":"\n"},{"attributes":{"table-col":{"width":"150"}},"insert":"\n\n\n"},{"attributes":{"block-id":"block-9144be72-e528-4f91-b0b2-82d20408e9ea","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-6kv2ls","cell":"cell-apba4k"},"row":"row-6kv2ls","cell":"cell-apba4k","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-639adacb-1516-43ed-b272-937c55669a1c","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-6kv2ls","cell":"cell-a8qf0r"},"row":"row-6kv2ls","cell":"cell-a8qf0r","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-6302ca4a-73a3-4c25-8c1e-b542f048f1c6","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-6kv2ls","cell":"cell-oi9ikb"},"row":"row-6kv2ls","cell":"cell-oi9ikb","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-ceeddd05-330e-4f86-8017-4a3a060c4627","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-d1sv2g","cell":"cell-dt6ks2"},"row":"row-d1sv2g","cell":"cell-dt6ks2","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-37b19322-cb57-4e6f-8fad-0d1401cae53f","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-d1sv2g","cell":"cell-qah2ay"},"row":"row-d1sv2g","cell":"cell-qah2ay","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-468a69b5-9332-450b-9107-381d593de249","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-d1sv2g","cell":"cell-fpcz5a"},"row":"row-d1sv2g","cell":"cell-fpcz5a","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-26b1d252-9b2e-4808-9b29-04e76696aa3c","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-pflz90","cell":"cell-zrhylp"},"row":"row-pflz90","cell":"cell-zrhylp","rowspan":"1","colspan":"1"},"insert":"\n"},{"attributes":{"block-id":"block-6af97ba7-8cf9-497a-9365-7075b938837b","table-cell-line":{"rowspan":"1","colspan":"1","row":"row-pflz90","cell":"cell-s1q9nt"},"row":"row-pflz90","cell":"cell-s1q9nt","rowspan":"1","colspan":"1"},"insert":"\n"},{"insert":"\n","attributes":{"list":{"rowspan":"1","colspan":"1","row":"row-pflz90","cell":"cell-20b0j9","list":"bullet"},"block-id":"block-107e273e-86bc-44fd-b0d7-41ab55aca484","row":"row-pflz90","cell":"cell-20b0j9","rowspan":"1","colspan":"1"}},{"attributes":{"block-id":"block-38161f9c-6f6d-44c5-b086-54cc6490f1e3"},"insert":"\n"},{"insert":"Content after table"},{"attributes":{"block-id":"block-15630542-ef45-412d-9415-88f0052238ce"},"insert":"\n"}]""";
}
