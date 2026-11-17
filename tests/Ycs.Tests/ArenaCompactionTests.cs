using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Compaction of a document's character arena: when it runs, what it moves, what it keeps. They run with the default page
/// sizes and without forced compaction whatever the run mode.
/// </summary>
[TestClass]
public class ArenaCompactionTests
{
    public TestContext TestContext { get; set; }

    private static (int First, int Max, bool Copy, StructStore.CompactionMode Compaction) _mode;

    [ClassInitialize]
    public static void SaveMode(TestContext context) => _mode = (Arena<char>.FirstPageForTests, Arena<char>.MaxPageForTests, ItemRef.CopyOnMergeForTests, StructStore.CompactionForTests);

    [TestInitialize]
    public void NoMode()
    {
        Arena<char>.FirstPageForTests = 0;
        Arena<char>.MaxPageForTests = 0;
        ItemRef.CopyOnMergeForTests = false;
        StructStore.CompactionForTests = StructStore.CompactionMode.None;
    }

    [TestCleanup]
    public void RestoreMode() => (Arena<char>.FirstPageForTests, Arena<char>.MaxPageForTests, ItemRef.CopyOnMergeForTests, StructStore.CompactionForTests) = _mode;

    private static YDoc NewDoc(bool gc = true) => new(new YDocOptions { ClientId = 1, GarbageCollection = gc }) { RunExtensionsDisabledForTests = true };

    private static Arena<char> Chars(YDoc doc) => doc.Store.Slab.Chars;

    // Many short runs on many pages, then most of them deleted: the arena holds more garbage than text.
    private static string TypeAndDeleteMost(YDoc doc)
    {
        var text = doc.GetText("t");
        var expected = new System.Text.StringBuilder();
        for (var i = 0; i < 400; i++)
        {
            var piece = new string((char)('a' + (i % 26)), 100);
            text.Insert(text.Length, piece);
            expected.Append(piece);
            text.Insert(text.Length, "|");
            expected.Append('|');
        }

        for (var i = 0; i < 400; i++)
        {
            var at = i * 2 + 1 - i; // each piece's first 99 characters go: the arena keeps one live character in a hundred
            text.Remove(at, 99);
            expected.Remove(at, 99);
        }

        return expected.ToString();
    }

    [TestMethod]
    public void EndOfBatch_DeadOutweighsLiveAndFillsAPage_Compacts()
    {
        var doc = NewDoc();
        var expected = TypeAndDeleteMost(doc);

        var chars = Chars(doc);
        Assert.AreEqual((expected, true), (doc.GetText("t").ToString(), chars.Live < chars.Dead && chars.Dead < 80_000));
        Assert.IsTrue(chars.PageCount < 6, $"{chars.PageCount} pages held");
    }

    [TestMethod]
    public void Compaction_LiveRuns_KeepTheirTextAndTheStateOfBothFormats()
    {
        var doc = NewDoc();
        var expected = TypeAndDeleteMost(doc);
        var before = (doc.EncodeStateAsUpdate(format: UpdateFormat.V1), doc.EncodeStateAsUpdate(format: UpdateFormat.V2));
        var pages = Chars(doc).PageCount;

        doc.Store.Slab.CompactChars();

        var after = (doc.EncodeStateAsUpdate(format: UpdateFormat.V1), doc.EncodeStateAsUpdate(format: UpdateFormat.V2));
        Assert.IsTrue(before.Item1.SequenceEqual(after.Item1) && before.Item2.SequenceEqual(after.Item2));
        Assert.AreEqual(expected, doc.GetText("t").ToString());
        Assert.IsTrue(Chars(doc).PageCount <= pages);
        doc.Store.CheckForTests();
    }

    [TestMethod]
    public void Compaction_Called_DropsTheTailsAndCountsTheLiveCharacters()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, new string('a', 300));
        text.Insert(0, "b");
        text.Insert(150, "c");
        var live = text.Length;

        doc.Store.Slab.CompactChars();

        Assert.AreEqual((0, (long)live), (Chars(doc).TailCount, Chars(doc).Live));
        doc.Store.CheckForTests();
    }

    [TestMethod]
    public void Compaction_DeletedButNotCollectedItems_KeepTheirRangeAndEncoding()
    {
        var doc = NewDoc(gc: false);
        var text = doc.GetText("t");
        text.Insert(0, new string('x', 600));
        text.Insert(300, "|");
        text.Remove(0, 300);
        text.Insert(0, new string('y', 5000));
        text.Remove(0, 5000);
        var before = doc.EncodeStateAsUpdate();

        doc.Store.Slab.CompactChars();

        Assert.IsTrue(before.SequenceEqual(doc.EncodeStateAsUpdate()));
        Assert.IsTrue(Chars(doc).Live >= 5601, $"{Chars(doc).Live} live");
        doc.Store.CheckForTests();
    }

    [TestMethod]
    public void Compaction_CollectedItems_LeaveNoRange()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, new string('x', 600));
        text.Remove(0, 600);

        doc.Store.Slab.CompactChars();

        Assert.AreEqual((0L, 0), (Chars(doc).Live, Chars(doc).PageCount - Chars(doc).PageCount));
        doc.Store.CheckForTests();
    }

    [TestMethod]
    public void Compaction_NoArena_DoesNothing()
    {
        var doc = NewDoc();

        doc.Store.Slab.CompactChars();

        Assert.IsNull(doc.Store.Slab.CharsOrNull);
    }

    [TestMethod]
    public void Typing_AfterACompaction_ContinuesInRunsTheCompactionMoved()
    {
        var doc = NewDoc();
        var expected = TypeAndDeleteMost(doc);
        doc.Store.Slab.CompactChars();
        var text = doc.GetText("t");

        for (var i = 0; i < 300; i++)
        {
            text.Insert(5, "z");
            text.Insert(text.Length, "q");
            expected = expected.Insert(5, "z") + "q";
        }

        Assert.AreEqual(expected, text.ToString());
        doc.Store.CheckForTests();
    }

    [TestMethod]
    public void WorthCompacting_DeadBelowALargestPage_IsFalse()
    {
        var arena = new Arena<char>(4, 8);
        var a = arena.Append(['a', 'b', 'c', 'd']);
        arena.MarkDead(a, 4);

        Assert.IsFalse(arena.WorthCompacting);
        arena.MarkDead(arena.Append(['e', 'f', 'g', 'h']), 4);
        Assert.IsTrue(arena.WorthCompacting);
    }

    [TestMethod]
    public void TypingOneRun_ThenCompaction_HoldsAtMostOneAndAHalfTimesTheText()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        doc.Transact(_ =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                text.Insert(i, "x");
            }
        });
        var chars = Chars(doc);
        long Held() => Enumerable.Range(0, 64).Sum(page => (long)chars.PageCapacity(page));
        var typed = Held(); // the batch's end compacted already

        doc.Store.Slab.CompactChars();

        TestContext.WriteLine($"100,000 characters typed in one transaction: {typed} held, {Held()} after compaction, live {chars.Live}, dead {chars.Dead}");
        Assert.IsTrue(Held() <= 150_000, $"{Held()} characters held");
    }
}
