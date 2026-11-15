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
    public void EncodeStateAsUpdate_DefaultSubdocument_WritesEmptyOptions()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.GetMap("m").Set("s", new YDoc(new YDocOptions { Guid = "g1" }));

        Assert.AreEqual(
            ("AQEBACkBAW0BcwJnMXYAAA==", "AAABAQAAASkIBG1zZzFBAAIBAQAAAQEAdgAA"),
            (S(doc.EncodeStateAsUpdate()), S(doc.EncodeStateAsUpdateV2())));
    }

    [TestMethod]
    public void EncodeStateAsUpdate_SubdocumentWithOptions_WritesGcAutoLoadMetaInYjsOrder()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });

        doc.GetMap("m").Set("s", new YDoc(new YDocOptions
        {
            Guid = "g2",
            Gc = false,
            AutoLoad = true,
            Meta = new Dictionary<string, object> { ["a"] = 1 },
        }));

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
        var subdoc = (YDoc)doc.GetMap("m").Get("s");

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

        doc.GetMap("m").Set("s", subdoc);

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
        doc.GetMap("m").Set("a", new YDoc(new YDocOptions { Guid = "a" }));
        doc.GetMap("m").Set("b", new YDoc(new YDocOptions { Guid = "b" }));
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
        doc.GetMap("m").Set("s", subdoc);
        var events = Events(doc);

        subdoc.Destroy();
        var replacement = (YDoc)doc.GetMap("m").Get("s");
        var shouldLoad = replacement.ShouldLoad;
        replacement.Load();

        Assert.AreEqual(
            ("al|al|;||al", false, true, "al"),
            (string.Join(";", events), shouldLoad, replacement.AutoLoad, replacement.Guid));
    }

    [TestMethod]
    public void Destroy_DeletedSubdocument_LeavesAFreshDocumentInTheContent()
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var map = doc.GetMap("m");
        var subdoc = new YDoc(new YDocOptions { Guid = "del" });
        map.Set("s", subdoc);
        var item = subdoc._item;

        map.Delete("s");
        var content = (ContentDoc)item.Content;

        Assert.AreEqual(("del", false), (content.Doc?.Guid, ReferenceEquals(content.Doc, subdoc)));
    }

    [TestMethod]
    public void Set_SubdocumentInsertedTwice_DoesNotThrow()
    {
        var doc = new YDoc();
        var subdoc = new YDoc();
        doc.GetMap("m").Set("a", subdoc);

        doc.GetMap("m").Set("b", subdoc);

        Assert.AreSame(subdoc, doc.GetMap("m").Get("b"));
    }

    [TestMethod]
    public void Destroy_NestedSubdocuments_EventsAndStateAsYjs()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 5 });
        var a = new YDoc(new YDocOptions { Guid = "A" });
        doc.GetMap("m").Set("a", a);
        var b = new YDoc(new YDocOptions { Guid = "B" });
        a.GetMap("m").Set("b", b);
        var events = new List<string>();
        doc.SubdocsChanged += (_, e) => events.Add("d:" + Describe(e));
        a.SubdocsChanged += (_, e) => events.Add("a:" + Describe(e));

        doc.Destroy();
        var replacement = (YDoc)doc.GetMap("m").Get("a");

        Assert.AreEqual(
            ("a:B|B|;d:A|A|;a:B|B|", 5L, "A", false, false, "B", "AQEFACkBAW0BYQFBdgAA"),
            (string.Join(";", events), b.ClientId, replacement.Guid, ReferenceEquals(replacement, a), replacement.ShouldLoad, string.Join(",", a.GetSubdocGuids()), S(doc.EncodeStateAsUpdate())));

        static string Describe((ISet<YDoc> Loaded, ISet<YDoc> Added, ISet<YDoc> Removed) e) =>
            $"{string.Join(",", e.Added.Select(d => d.Guid))}|{string.Join(",", e.Removed.Select(d => d.Guid))}|{string.Join(",", e.Loaded.Select(d => d.Guid))}";
    }
}
