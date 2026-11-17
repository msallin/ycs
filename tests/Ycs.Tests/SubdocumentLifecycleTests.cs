using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The subdocument lifecycle as Yjs 13.6.32 has it (tests/doc.tests.js testSubdocLoadEdgeCases, testSubdocLoadEdgeCasesAutoload,
/// testSubdocsUndo and the events of the scenarios around them). Events read as "added|removed|loaded" guids.
/// </summary>
[TestClass]
public class SubdocumentLifecycleTests
{
    private static string Describe(SubdocsEventArgs e) =>
        $"{string.Join(",", e.Added.Select(d => d.Guid))}|{string.Join(",", e.Removed.Select(d => d.Guid))}|{string.Join(",", e.Loaded.Select(d => d.Guid))}";

    private static List<string> Events(YDoc doc)
    {
        var events = new List<string>();
        doc.SubdocsChanged += (_, e) => events.Add(Describe(e));
        return events;
    }

    // The last SubdocsEventArgs a document raised.
    private sealed class Last
    {
        public SubdocsEventArgs Args { get; private set; }

        public Last(YDoc doc) => doc.SubdocsChanged += (_, e) => Args = e;
    }

    private static YDoc SubdocAt0(YDoc doc) => doc.GetArray("a")[0].TryGetDocument(out var d) ? d! : null;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Insert_NewSubdocument_IsAddedAndLoadedWhetherOrNotItAutoLoads(bool autoLoad)
    {
        var doc = new YDoc();
        var last = new Last(doc);
        var subdoc = new YDoc(new YDocOptions { AutoLoad = autoLoad });

        doc.GetArray("a").Add([subdoc]);

        Assert.AreEqual((true, autoLoad, true, true), (subdoc.ShouldLoad, subdoc.AutoLoad, last.Args.Loaded.Contains(subdoc), last.Args.Added.Contains(subdoc)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Destroy_InsertedSubdocument_ReplacesItWithANotLoadedOneThatIsAdded(bool autoLoad)
    {
        var doc = new YDoc();
        var subdoc = new YDoc(new YDocOptions { AutoLoad = autoLoad });
        doc.GetArray("a").Add([subdoc]);
        var last = new Last(doc);

        subdoc.Destroy();
        var replacement = SubdocAt0(doc);

        Assert.AreEqual((false, true, false), (ReferenceEquals(subdoc, replacement), last.Args.Added.Contains(replacement), last.Args.Loaded.Contains(replacement)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Load_ReplacementOfADestroyedSubdocument_IsLoadedAndNotAddedAgain(bool autoLoad)
    {
        var doc = new YDoc();
        var subdoc = new YDoc(new YDocOptions { AutoLoad = autoLoad });
        doc.GetArray("a").Add([subdoc]);
        var last = new Last(doc);
        subdoc.Destroy();
        var replacement = SubdocAt0(doc);

        replacement.Load();

        Assert.AreEqual((false, true), (last.Args.Added.Contains(replacement), last.Args.Loaded.Contains(replacement)));
    }

    [TestMethod]
    public void ApplyUpdate_RemoteSubdocumentWithoutAutoLoad_IsAddedButNotLoaded()
    {
        var doc = new YDoc();
        doc.GetArray("a").Add([new YDoc()]);
        var remote = new YDoc();
        var last = new Last(remote);

        remote.ApplyUpdate(doc.EncodeStateAsUpdate());
        var subdoc = SubdocAt0(remote);

        Assert.AreEqual((false, false, true, false), (subdoc.ShouldLoad, subdoc.AutoLoad, last.Args.Added.Contains(subdoc), last.Args.Loaded.Contains(subdoc)));
    }

    [TestMethod]
    public void Load_RemoteSubdocumentWithoutAutoLoad_IsLoadedAndNotAddedAgain()
    {
        var doc = new YDoc();
        doc.GetArray("a").Add([new YDoc()]);
        var remote = new YDoc();
        var last = new Last(remote);
        remote.ApplyUpdate(doc.EncodeStateAsUpdate());
        var subdoc = SubdocAt0(remote);

        subdoc.Load();

        Assert.AreEqual((true, false, true), (subdoc.ShouldLoad, last.Args.Added.Contains(subdoc), last.Args.Loaded.Contains(subdoc)));
    }

    [TestMethod]
    public void ApplyUpdate_RemoteSubdocumentWithAutoLoad_IsAddedAndLoaded()
    {
        var doc = new YDoc();
        doc.GetArray("a").Add([new YDoc(new YDocOptions { Guid = "au", AutoLoad = true })]);
        var remote = new YDoc();
        var events = Events(remote);

        remote.ApplyUpdate(doc.EncodeStateAsUpdate());
        var subdoc = SubdocAt0(remote);

        Assert.AreEqual(("au||au", true, true), (string.Join(";", events), subdoc.ShouldLoad, subdoc.AutoLoad));
    }

    [TestMethod]
    public void Load_SubdocumentInsertedWithShouldLoadFalse_RaisesLoadedOnly()
    {
        var doc = new YDoc();
        var events = Events(doc);
        var subdoc = new YDoc(new YDocOptions { Guid = "s", ShouldLoad = false });
        doc.GetArray("a").Add([subdoc]);
        var before = subdoc.ShouldLoad;

        subdoc.Load();

        Assert.AreEqual(("s||;||s", false, true), (string.Join(";", events), before, subdoc.ShouldLoad));
    }

    [TestMethod]
    public void Undo_InsertionOfASubdocument_RaisesRemovedAndRedoRaisesAdded()
    {
        var doc = new YDoc();
        var array = doc.GetArray("a");
        var undoManager = new UndoManager(array);
        array.Add([new YDoc(new YDocOptions { Guid = "u" })]);
        var events = Events(doc);

        undoManager.Undo();
        var afterUndo = events.ToList();
        undoManager.Redo();

        Assert.AreEqual(("|u|;|u|", "|u|;|u|;u||", "u"), (string.Join(";", afterUndo), string.Join(";", events), string.Join(",", doc.GetSubdocGuids())));
    }

    [TestMethod]
    public void UndoRedo_InsertionOfASubdocument_KeepsTheElement()
    {
        // testSubdocsUndo; Yjs inserts into an XML fragment, whose children Ycs types as YType, so a YArray holds it.
        var doc = new YDoc();
        var array = doc.GetArray("a");
        var undoManager = new UndoManager(array);
        array.Add([new YDoc()]);

        undoManager.Undo();
        undoManager.Redo();

        Assert.AreEqual(1, array.Count);
    }

    [TestMethod]
    public void Transact_SubdocumentDeletedAndANewOneWithTheSameGuidSet_RaisesBothThenTheDestroyOfTheOld()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        map["s"] = new YDoc(new YDocOptions { Guid = "g" });
        var events = Events(doc);

        doc.Transact(_ =>
        {
            map.Remove("s");
            map["s"] = new YDoc(new YDocOptions { Guid = "g" });
        });

        Assert.AreEqual(("g|g|g;|g|", "g"), (string.Join(";", events), string.Join(",", doc.GetSubdocGuids())));
    }

    [TestMethod]
    public void Transact_SubdocumentAddedAndDeletedInOneTransaction_RaisesItAsLoadedOnly()
    {
        var doc = new YDoc();
        var map = doc.GetMap("m");
        var events = Events(doc);

        doc.Transact(_ =>
        {
            map["s"] = new YDoc(new YDocOptions { Guid = "x" });
            map.Remove("s");
        });

        Assert.AreEqual(("||x", string.Empty), (string.Join(";", events), string.Join(",", doc.GetSubdocGuids())));
    }
}
