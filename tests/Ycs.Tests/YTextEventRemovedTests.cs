using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// <see cref="YTextEvent.Removed"/>: the runs the transaction removed, with their positions in the old text, readable
/// while the document still holds the text and an <see cref="InvalidOperationException"/> after, with or without garbage collection.
/// </summary>
[TestClass]
public class YTextEventRemovedTests
{
    private static string Runs(YTextEvent e)
    {
        var result = new List<string>();
        foreach (var run in e.Removed)
        {
            result.Add($"{run.Start}:{run.Length}:{run.Text}");
        }

        return string.Join(" ", result);
    }

    private static string RunsOf(YText text, Action edit)
    {
        var runs = "";
        using var _ = text.Observe(e => runs = Runs(e));
        edit();
        return runs;
    }

    private static YText Typed(YDoc? doc = null)
    {
        var text = (doc ?? new YDoc(new YDocOptions { ClientId = 1 })).GetText("t");
        text.Insert(0, "hello world");
        return text;
    }

    [TestMethod]
    public void Removed_OneDelete_YieldsItsRun()
    {
        var text = Typed();

        Assert.AreEqual("2:3:llo", RunsOf(text, () => text.Remove(2, 3)));
    }

    [TestMethod]
    public void Removed_NothingRemoved_YieldsNothing()
    {
        var text = Typed();

        Assert.AreEqual(string.Empty, RunsOf(text, () => text.Insert(2, "xx")));
    }

    [TestMethod]
    public void Removed_SeveralDeletesAndInsertsInOneTransaction_AscendAndAddUpToTheDeltaDeletes()
    {
        var text = Typed();
        var old = text.ToString();
        var report = "";
        text.Observe(e =>
        {
            var starts = new List<int>();
            var total = 0;
            var matches = true;
            foreach (var run in e.Removed)
            {
                starts.Add(run.Start);
                total += run.Length;
                matches &= old.Substring(run.Start, run.Length) == run.Text.ToString();
            }

            var deletes = e.Delta.OfType<Delta.Delete>().Sum(d => d.Length);
            report = $"{string.Join(",", starts)}|{total == deletes}|{matches}";
        });

        text.Doc.Transact(_ =>
        {
            text.Insert(0, "AA");
            text.Remove(3, 2);
            text.Insert(5, "BB");
            text.Remove(9, 1);
            text.Remove(12, 2);
        });

        Assert.AreEqual("1,7|True|True", report);
    }

    [TestMethod]
    public void Removed_DeleteAcrossTheItemsOfTwoClients_YieldsOneRunPerItem()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        a.GetText("t").Insert(0, "abcd");
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        b.GetText("t").Insert(4, "wxyz");
        a.ApplyUpdate(b.EncodeStateAsUpdate());
        var text = a.GetText("t");

        Assert.AreEqual("2:2:cd 4:2:wx", RunsOf(text, () => text.Remove(2, 4)));
    }

    [TestMethod]
    public void Removed_DeletedEmbedAndNestedType_HaveLengthOneAndNoText()
    {
        var text = Typed();
        text.InsertEmbed(3, (YValue)5);
        text.InsertEmbed(8, new YMap());

        Assert.AreEqual("2:1:l 3:1: 4:4:lo w 8:1: 9:1:o", RunsOf(text, () => text.Remove(2, 8)));
    }

    [TestMethod]
    public void Removed_FormattedRun_YieldsTheCharactersOnly()
    {
        var text = Typed();
        text.Format(2, 5, ("bold", true));

        Assert.AreEqual("2:5:llo w", RunsOf(text, () => text.Remove(2, 5)));
    }

    [TestMethod]
    public void Removed_InsertedAndDeletedInOneTransaction_IsAbsent()
    {
        var text = Typed();

        Assert.AreEqual("0:1:h", RunsOf(text, () => text.Doc.Transact(_ =>
        {
            text.Insert(3, "temp");
            text.Remove(3, 4);
            text.Remove(0, 1);
        })));
    }

    [TestMethod]
    public void Removed_RemoteTransaction_YieldsTheSameRunsAsTheLocalOne()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        var local = Typed(a);
        var b = new YDoc(new YDocOptions { ClientId = 2 });
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        var remote = b.GetText("t");
        var seen = "";
        remote.Observe(e => seen = Runs(e));

        var localRuns = RunsOf(local, () => local.Remove(2, 3));
        b.ApplyUpdate(a.EncodeStateAsUpdate(b.EncodeStateVector()));

        Assert.AreEqual((localRuns, localRuns), (seen, "2:3:llo"));
    }

    [TestMethod]
    public void Removed_InADeepObserverAndAfterTransaction_IsReadable()
    {
        var text = Typed();
        var deep = "";
        var after = "";
        YTextEvent? event_ = null;
        text.ObserveDeep((events, _) => deep = Runs((YTextEvent)events[0]));
        text.Observe(e => event_ = e);
        text.Doc.AfterTransaction += (_, _) => after = Runs(event_!);

        text.Remove(0, 5);

        Assert.AreEqual(("0:5:hello", "0:5:hello"), (deep, after));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Removed_AfterTheTransaction_Throws(bool garbageCollection)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = garbageCollection });
        var text = Typed(doc);
        YTextEvent? kept = null;
        text.Observe(e => kept = e);

        text.Remove(0, 5);

        Assert.Throws<InvalidOperationException>(() => Count(kept!));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Removed_InAfterTransactionCleanup_Throws(bool garbageCollection)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = garbageCollection });
        var text = Typed(doc);
        YTextEvent? kept = null;
        text.Observe(e => kept = e);
        string? message = null;
        doc.AfterTransactionCleanup += (_, _) => message = Assert.Throws<InvalidOperationException>(() => Count(kept!)).Message;

        text.Remove(0, 5);

        StringAssert.Contains(message, "Copy what you keep");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Removed_InAnUpdateHandler_Throws(bool garbageCollection)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = garbageCollection });
        var text = Typed(doc);
        YTextEvent? kept = null;
        text.Observe(e => kept = e);
        var threw = false;
        doc.Updated += (_, _) => threw = Assert.Throws<InvalidOperationException>(() => Count(kept!)) != null;

        text.Remove(0, 5);

        Assert.IsTrue(threw);
    }

    [TestMethod]
    public void Removed_TransactionReusedForTheNextEdit_ReadsTheNewEventOnly()
    {
        var text = Typed();
        YTextEvent? kept = null;
        text.Observe(e => kept = e);
        text.Remove(0, 1);
        var first = kept;
        var second = "";
        text.Observe(e => second = Runs(e));

        text.Remove(0, 1);

        Assert.AreEqual(("0:1:e", true), (second, Assert.Throws<InvalidOperationException>(() => Count(first!)) != null));
    }

    [TestMethod]
    public void Removed_ObserverEditsTheTextFirst_SeesTheEventsDeletesOnly()
    {
        var text = Typed();
        var runs = "";
        var edited = false;
        text.Observe(e =>
        {
            if (!edited)
            {
                edited = true;
                text.Insert(0, "zz");
                runs = Runs(e);
            }
        });

        text.Remove(5, 6);

        Assert.AreEqual("5:6: world", runs);
    }

    [TestMethod]
    public void Removed_Enumerated_AllocatesNothing()
    {
        var text = Typed();
        var allocated = -1L;
        var count = 0;
        text.Observe(e =>
        {
            count += Count(e);
            var before = GC.GetAllocatedBytesForCurrentThread();
            count += Count(e);
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        text.Remove(2, 3);

        Assert.AreEqual((0L, 2), (allocated, count));
    }

    private static int Count(YTextEvent e)
    {
        var n = 0;
        foreach (var run in e.Removed)
        {
            n += run.Length;
        }

        return n == 0 ? 0 : 1;
    }
}
