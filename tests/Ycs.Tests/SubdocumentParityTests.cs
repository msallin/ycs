using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>Subdocuments: bytes and events as Yjs 13.6.32 produces them (pinned inline).</summary>
[TestClass]
public class SubdocumentParityTests
{
    private static byte[] B(string base64) => Convert.FromBase64String(base64);

    private static string S(byte[] bytes) => Convert.ToBase64String(bytes);

    private static List<string> Events(YDoc doc)
    {
        var events = new List<string>();
        doc.SubdocsChanged += (_, e) => events.Add(
            $"{string.Join(",", e.Added.Select(d => d.Guid))}|{string.Join(",", e.Removed.Select(d => d.Guid))}|{string.Join(",", e.Loaded.Select(d => d.Guid))}");
        return events;
    }

    [TestMethod]
    public void Push_NewSubdocumentWithoutAutoLoad_IsAddedAndLoaded()
    {
        // Yjs 13.6.32: subdocs { added: ['doc-1'], removed: [], loaded: ['doc-1'] } (a new Y.Doc has shouldLoad true).
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var events = Events(doc);

        doc.GetArray("a").Add(new YDoc(new YDocOptions { Guid = "doc-1", AutoLoad = false }));

        CollectionAssert.AreEqual(new[] { "doc-1||doc-1" }, events);
    }

    [TestMethod]
    public void EncodeStateAsUpdate_DefaultSubdocument_WritesEmptyOptions()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.GetMap("m")["s"] = new YDoc(new YDocOptions { Guid = "g1" });

        Assert.AreEqual(
            ("AQEBACkBAW0BcwJnMXYAAA==", "AAABAQAAASkIBG1zZzFBAAIBAQAAAQEAdgAA"),
            (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdate(format: UpdateFormat.V2))));
    }

    [TestMethod]
    public void EncodeStateAsUpdate_SubdocumentWithOptions_WritesGcAutoLoadMetaInYjsOrder()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.GetMap("m")["s"] = new YDoc(new YDocOptions
        {
            Guid = "g2",
            GarbageCollection = false,
            AutoLoad = true,
            Meta = YValue.FromObject(new Dictionary<string, object> { ["a"] = 1 }),
        });

        Assert.AreEqual("AQEBACkBAW0BcwJnMnYDAmdjeQhhdXRvTG9hZHgEbWV0YXYBAWF9AQA=", S(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    [DataRow("AQEBACkBAW0BcwJnM3YBBG1ldGF1An0BdwF4AA==")] // meta: [1, 'x']
    [DataRow("AQEMACkBAW0BcwhtZXRhLXN0cnYBBG1ldGF3DWp1c3QgYSBzdHJpbmcA")] // meta: 'just a string'
    public void ApplyUpdate_SubdocumentWithMetaThatIsNotAnObject_IsReadAndReEncodedUnchanged(string update)
    {
        var doc = new YDoc();

        doc.ApplyUpdate(B(update));

        Assert.AreEqual(update, S(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ApplyUpdate_ItemOfYjsSubdocument_KeepsItsGuidWhenRelayed()
    {
        var doc = new YDoc();

        doc.ApplyUpdate(B("AQELACkBAW0BcwtteS1zdWItZ3VpZHYAAA=="));

        Assert.AreEqual("AQELACkBAW0BcwtteS1zdWItZ3VpZHYAAA==", S(doc.EncodeStateAsUpdate()));
    }

    [TestMethod]
    public void ApplyUpdate_OptionsWithGuid_TheOptionsGuidWins()
    {
        // Item guid "item-guid", options { gc: true, guid: 'opts-guid', autoLoad: false }.
        var doc = new YDoc();

        doc.ApplyUpdate(B("AQEBACkBAW0BcwlpdGVtLWd1aWR2AwJnY3gEZ3VpZHcJb3B0cy1ndWlkCGF1dG9Mb2FkeQA="));
        var subdoc = (YDoc)doc.GetMap("m")["s"].ToObject();

        Assert.AreEqual(
            ("opts-guid", false, "AQEBACkBAW0BcwlvcHRzLWd1aWR2AAA="),
            (subdoc.Guid, subdoc.ShouldLoad, S(doc.EncodeStateAsUpdate())));
    }

    [TestMethod]
    public void ApplyUpdate_OptionsWithShouldLoad_LoadsTheSubdocument()
    {
        var doc = new YDoc();
        var events = Events(doc);

        doc.ApplyUpdate(B("AQEBACkBAW0BcwJzbHYBCnNob3VsZExvYWR4AA=="));

        CollectionAssert.AreEqual(new[] { "sl||sl" }, events);
    }

    [TestMethod]
    public void Set_LocalSubdocument_IsLoadedAndTakesTheParentsClientId()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 77 });
        var events = Events(doc);
        var subdoc = new YDoc(new YDocOptions { Guid = "x" });

        doc.GetMap("m")["s"] = subdoc;

        Assert.AreEqual(("x||x", 77L), (string.Join(";", events), (long)subdoc.ClientId));
    }

    [TestMethod]
    public void NewDocument_ShouldLoadByDefault()
    {
        Assert.IsTrue(new YDoc().ShouldLoad);
    }

    [TestMethod]
    public void Destroy_ParentWithTwoSubdocuments_DestroysBoth()
    {
        var doc = new YDoc();
        doc.GetMap("m")["a"] = new YDoc(new YDocOptions { Guid = "a" });
        doc.GetMap("m")["b"] = new YDoc(new YDocOptions { Guid = "b" });
        var events = Events(doc);

        doc.Destroy();

        Assert.AreEqual(
            ("a|a|;b|b|", "a,b"),
            (string.Join(";", events), string.Join(",", doc.GetSubdocGuids().OrderBy(g => g, StringComparer.Ordinal))));
    }

    [TestMethod]
    public void Load_ReplacementOfDestroyedAutoLoadSubdocument_RaisesLoaded()
    {
        var doc = new YDoc();
        var subdoc = new YDoc(new YDocOptions { Guid = "al", AutoLoad = true });
        doc.GetMap("m")["s"] = subdoc;
        var events = Events(doc);

        subdoc.Destroy();
        var replacement = (YDoc)doc.GetMap("m")["s"].ToObject();
        var shouldLoad = replacement.ShouldLoad;
        replacement.Load();

        Assert.AreEqual(
            ("al|al|;||al", false, true, "al"),
            (string.Join(";", events), shouldLoad, replacement.AutoLoad, replacement.Guid));
    }

    [TestMethod]
    public void Destroy_DeletedSubdocument_LeavesAFreshDocumentInTheContent()
    {
        var doc = new YDoc(new YDocOptions { GarbageCollection = false });
        var map = doc.GetMap("m");
        var subdoc = new YDoc(new YDocOptions { Guid = "del" });
        map["s"] = subdoc;
        var item = subdoc._item;

        map.Remove("s");
        var content = (ContentDoc)item.Content;

        Assert.AreEqual(("del", false), (content.Doc?.Guid, ReferenceEquals(content.Doc, subdoc)));
    }

    [TestMethod]
    public void Set_SubdocumentInsertedTwice_Throws()
    {
        var doc = new YDoc();
        var subdoc = new YDoc();
        doc.GetMap("m")["a"] = subdoc;

        Assert.ThrowsExactly<ArgumentException>(() => doc.GetMap("m")["b"] = subdoc);
    }

    [TestMethod]
    public void Destroy_NestedSubdocuments_EventsAndStateAsYjs()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 5 });
        var a = new YDoc(new YDocOptions { Guid = "A" });
        doc.GetMap("m")["a"] = a;
        var b = new YDoc(new YDocOptions { Guid = "B" });
        a.GetMap("m")["b"] = b;
        var events = new List<string>();
        doc.SubdocsChanged += (_, e) => events.Add("d:" + Describe(e));
        a.SubdocsChanged += (_, e) => events.Add("a:" + Describe(e));

        doc.Destroy();
        var replacement = (YDoc)doc.GetMap("m")["a"].ToObject();

        Assert.AreEqual(
            ("a:B|B|;d:A|A|;a:B|B|", 5L, "A", false, false, "B", "AQEFACkBAW0BYQFBdgAA"),
            (string.Join(";", events), b.ClientId, replacement.Guid, ReferenceEquals(replacement, a), replacement.ShouldLoad, string.Join(",", a.GetSubdocGuids()), S(doc.EncodeStateAsUpdate())));

        static string Describe(SubdocsEventArgs e) =>
            $"{string.Join(",", e.Added.Select(d => d.Guid))}|{string.Join(",", e.Removed.Select(d => d.Guid))}|{string.Join(",", e.Loaded.Select(d => d.Guid))}";
    }

    private static readonly string[] UndoRedoOfSubdocumentNoAutoLoad =
    [
            "insert  +[g:true:7] -[] L[g:true:7] items [g:true:7] guids [g] AQEHAAkBAWEBZ3YAAA==",
            "undo true +[] -[g:true:7] L[] +[] -[g:true:7] L[] items [] guids [] AQEHAAkBAWEBZ3YAAQcBAAE=",
            "redo true +[g:false:7] -[] L[] items [g:false:7] guids [g] AQIHAAkBAWEBZ3YASQcAAWd2AAEHAQAB",
            "delete  +[] -[g:false:7] L[] +[] -[g:false:7] L[] items [] guids [] AQIHAAkBAWEBZ3YASQcAAWd2AAEHAQAC",
            "undo true +[g:false:7] -[] L[] items [g:false:7] guids [g] AQMHAAkBAWEBZ3YASQcAAWd2AEkHAQFndgABBwEAAg==",
            "redo true +[] -[g:false:7] L[] +[] -[g:false:7] L[] items [] guids [] AQMHAAkBAWEBZ3YASQcAAWd2AEkHAQFndgABBwEAAw==",
            "undo true +[g:false:7] -[] L[] items [g:false:7] guids [g] AQQHAAkBAWEBZ3YASQcAAWd2AEkHAQFndgBJBwIBZ3YAAQcBAAM=",
    ];

    private static readonly string[] UndoRedoOfSubdocumentAutoLoad =
    [
            "insert  +[g:true:7] -[] L[g:true:7] items [g:true:7] guids [g] AQEHAAkBAWEBZ3YBCGF1dG9Mb2FkeAA=",
            "undo true +[] -[g:true:7] L[] +[] -[g:true:7] L[] items [] guids [] AQEHAAkBAWEBZ3YBCGF1dG9Mb2FkeAEHAQAB",
            "redo true +[g:true:7] -[] L[g:true:7] items [g:true:7] guids [g] AQIHAAkBAWEBZ3YBCGF1dG9Mb2FkeEkHAAFndgEIYXV0b0xvYWR4AQcBAAE=",
            "delete  +[] -[g:true:7] L[] +[] -[g:true:7] L[] items [] guids [] AQIHAAkBAWEBZ3YBCGF1dG9Mb2FkeEkHAAFndgEIYXV0b0xvYWR4AQcBAAI=",
            "undo true +[g:true:7] -[] L[g:true:7] items [g:true:7] guids [g] AQMHAAkBAWEBZ3YBCGF1dG9Mb2FkeEkHAAFndgEIYXV0b0xvYWR4SQcBAWd2AQhhdXRvTG9hZHgBBwEAAg==",
            "redo true +[] -[g:true:7] L[] +[] -[g:true:7] L[] items [] guids [] AQMHAAkBAWEBZ3YBCGF1dG9Mb2FkeEkHAAFndgEIYXV0b0xvYWR4SQcBAWd2AQhhdXRvTG9hZHgBBwEAAw==",
            "undo true +[g:true:7] -[] L[g:true:7] items [g:true:7] guids [g] AQQHAAkBAWEBZ3YBCGF1dG9Mb2FkeEkHAAFndgEIYXV0b0xvYWR4SQcBAWd2AQhhdXRvTG9hZHhJBwIBZ3YBCGF1dG9Mb2FkeAEHAQAD",
    ];

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UndoRedo_InsertedAndDeletedSubdocument_EventsDocumentsAndBytesAsYjs(bool autoLoad)
    {
        // Yjs 13.6.32: an UndoManager (captureTimeout 0) on array 'a' of a document with client id 7;
        // insert new Y.Doc({ guid: 'g', autoLoad }), undo, redo, delete it, undo, redo, undo. Each line:
        // the step, what undo/redo returned, the 'subdocs' events, the array's documents, getSubdocGuids()
        // and encodeStateAsUpdate (documents as guid:shouldLoad:clientID).
        var doc = new YDoc(new YDocOptions { ClientId = 7 });
        var array = doc.GetArray("a");
        var undoManager = new UndoManager(array, new UndoManagerOptions { CaptureTimeout = TimeSpan.Zero });
        var events = new List<string>();
        static string Docs(IEnumerable<object> docs) =>
            string.Join(",", docs.Cast<YDoc>().Select(d => $"{d.Guid}:{(d.ShouldLoad ? "true" : "false")}:{d.ClientId}").Order(StringComparer.Ordinal));
        doc.SubdocsChanged += (_, e) => events.Add($"+[{Docs(e.Added)}] -[{Docs(e.Removed)}] L[{Docs(e.Loaded)}]");
        var lines = new List<string>();
        void Step(string name, Func<object> action)
        {
            events.Clear();
            var result = action();
            var returned = name is "undo" or "redo" ? (result != null ? "true" : "false") : string.Empty;
            lines.Add($"{name} {returned} {string.Join(" ", events)} items [{Docs(array.Select(v => v.ToObject()))}] guids [{string.Join(",", doc.GetSubdocGuids().Order(StringComparer.Ordinal))}] {S(doc.EncodeStateAsUpdate())}");
        }

        Step("insert", () =>
        {
            array.Insert(0, [new YDoc(new YDocOptions { Guid = "g", AutoLoad = autoLoad })]);
            return null;
        });
        Step("undo", undoManager.Undo);
        Step("redo", undoManager.Redo);
        Step("delete", () =>
        {
            array.RemoveRange(0, 1);
            return null;
        });
        Step("undo", undoManager.Undo);
        Step("redo", undoManager.Redo);
        Step("undo", undoManager.Undo);

        CollectionAssert.AreEqual(autoLoad ? UndoRedoOfSubdocumentAutoLoad : UndoRedoOfSubdocumentNoAutoLoad, lines);
    }
}
