using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Relative positions into the text "abcdef" at index 3 after deletions. The expected indexes are the ones Yjs 13.6.32
/// resolves (<c>createAbsolutePositionFromRelativePosition</c>). An association After sits on "d", Before on "c".
/// </summary>
[TestClass]
public class RelativePositionDeletionTests
{
    private static (YDoc Doc, YText Text) Abcdef(bool gc, long client = 1)
    {
        var doc = new YDoc(new YDocOptions { ClientId = client, GarbageCollection = gc });
        var text = doc.GetText("t");
        text.Insert(0, "abcdef");
        return (doc, text);
    }

    private static (bool Resolved, int Index) Resolve(RelativePosition rpos, YDoc doc, bool follow = true) =>
        (rpos.TryResolve(doc, out var abs, follow), abs.Index);

    [TestMethod]
    [DataRow(Assoc.After, 3, 1, 3, true)]
    [DataRow(Assoc.After, 3, 1, 3, false)]
    [DataRow(Assoc.Before, 2, 1, 2, true)]
    [DataRow(Assoc.Before, 2, 1, 2, false)]
    [DataRow(Assoc.After, 2, 1, 2, true)]
    [DataRow(Assoc.Before, 3, 1, 3, true)]
    public void TryResolve_DeleteOfTheCharacterNearIt_ResolvesToTheNeighbouringLiveIndex(Assoc assoc, int from, int count, int expected, bool gc)
    {
        var (doc, text) = Abcdef(gc);
        var rpos = RelativePosition.FromIndex(text, 3, assoc);

        text.Remove(from, count);

        Assert.AreEqual((true, expected), Resolve(rpos, doc));
    }

    [TestMethod]
    [DataRow(Assoc.After, true)]
    [DataRow(Assoc.After, false)]
    [DataRow(Assoc.Before, true)]
    [DataRow(Assoc.Before, false)]
    public void TryResolve_EverythingToTheLeftDeleted_ResolvesToTheStart(Assoc assoc, bool gc)
    {
        var (doc, text) = Abcdef(gc);
        var rpos = RelativePosition.FromIndex(text, 3, assoc);

        text.Remove(0, 3);

        Assert.AreEqual((true, 0), Resolve(rpos, doc));
    }

    [TestMethod]
    [DataRow(Assoc.After, true)]
    [DataRow(Assoc.After, false)]
    [DataRow(Assoc.Before, true)]
    [DataRow(Assoc.Before, false)]
    public void TryResolve_EverythingToTheRightDeleted_ResolvesToTheEnd(Assoc assoc, bool gc)
    {
        var (doc, text) = Abcdef(gc);
        var rpos = RelativePosition.FromIndex(text, 3, assoc);

        text.Remove(3, 3);

        Assert.AreEqual((true, 3), Resolve(rpos, doc));
    }

    [TestMethod]
    [DataRow(Assoc.After, true)]
    [DataRow(Assoc.After, false)]
    [DataRow(Assoc.Before, true)]
    [DataRow(Assoc.Before, false)]
    public void TryResolve_WholeTextDeleted_ResolvesToTheStart(Assoc assoc, bool gc)
    {
        var (doc, text) = Abcdef(gc);
        var rpos = RelativePosition.FromIndex(text, 3, assoc);

        text.Remove(0, text.Length);

        Assert.AreEqual((true, 0), Resolve(rpos, doc));
    }

    [TestMethod]
    [DataRow(Assoc.After, 3, true)]
    [DataRow(Assoc.After, 3, false)]
    [DataRow(Assoc.Before, 2, true)]
    [DataRow(Assoc.Before, 2, false)]
    public void TryResolve_RemoteDeleteOfTheCharacterItSitsOn_GivesTheSameIndexOnBothSides(Assoc assoc, int deleteAt, bool gc)
    {
        var (local, text) = Abcdef(gc);
        var remote = new YDoc(new YDocOptions { ClientId = 2, GarbageCollection = gc });
        remote.ApplyUpdate(local.EncodeStateAsUpdate());
        var encoded = RelativePosition.FromIndex(text, 3, assoc).Encode();

        remote.GetText("t").Remove(deleteAt, 1);
        local.ApplyUpdate(remote.EncodeStateAsUpdate(local.EncodeStateVector()));
        var decoded = RelativePosition.Decode(encoded);

        Assert.AreEqual(((true, deleteAt), (true, deleteAt)), (Resolve(decoded, local), Resolve(decoded, remote)));
    }

    [TestMethod]
    [DataRow(Assoc.After, true)]
    [DataRow(Assoc.After, false)]
    [DataRow(Assoc.Before, true)]
    [DataRow(Assoc.Before, false)]
    public void TryResolve_RemoteDeleteOfEverythingToTheLeft_GivesTheStartOnBothSides(Assoc assoc, bool gc)
    {
        var (local, text) = Abcdef(gc);
        var remote = new YDoc(new YDocOptions { ClientId = 2, GarbageCollection = gc });
        remote.ApplyUpdate(local.EncodeStateAsUpdate());
        var encoded = RelativePosition.FromIndex(text, 3, assoc).Encode();

        remote.GetText("t").Remove(0, 3);
        local.ApplyUpdate(remote.EncodeStateAsUpdate(local.EncodeStateVector()));
        var decoded = RelativePosition.Decode(encoded);

        Assert.AreEqual(((true, 0), (true, 0)), (Resolve(decoded, local), Resolve(decoded, remote)));
    }

    [TestMethod]
    [DataRow(Assoc.After)]
    [DataRow(Assoc.Before)]
    public void TryResolve_NestedTextDeletedAndCollected_DoesNotResolve(Assoc assoc)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var nested = new YText("abcdef");
        array.Add([nested]);
        var rpos = RelativePosition.FromIndex(nested, 3, assoc);

        array.RemoveAt(0);

        Assert.AreEqual(false, rpos.TryResolve(doc, out _));
    }

    [TestMethod]
    [DataRow(Assoc.After)]
    [DataRow(Assoc.Before)]
    public void TryResolve_NestedTextDeletedWithoutCollection_ResolvesToTheStartOfTheDeletedText(Assoc assoc)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false });
        var array = doc.GetArray("a");
        var nested = new YText("abcdef");
        array.Add([nested]);
        var rpos = RelativePosition.FromIndex(nested, 3, assoc);

        array.RemoveAt(0);

        Assert.AreEqual((true, 0, true), (rpos.TryResolve(doc, out var abs), abs.Index, ReferenceEquals(abs.Type, nested)));
    }

    [TestMethod]
    public void TryResolve_UndoneDeletion_FollowsTheRedoneItemUnlessToldNotTo()
    {
        // Yjs 13.6.32 testRelativePositionWithUndo: 0, 1, 6 (not following), 6 and 6 on a copy of the document.
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "hello world");
        var rpos = RelativePosition.FromIndex(text, 1);
        var um = new UndoManager(text);
        text.Remove(0, 6);
        var deleted = Resolve(rpos, doc);
        um.Undo();
        var copy = new YDoc();
        copy.ApplyUpdate(doc.EncodeStateAsUpdate());

        Assert.AreEqual(
            (0, 1, 6, 6, 6),
            (deleted.Index, Resolve(rpos, doc).Index, Resolve(rpos, doc, follow: false).Index, Resolve(rpos, copy).Index, Resolve(rpos, copy, follow: false).Index));
    }
}
