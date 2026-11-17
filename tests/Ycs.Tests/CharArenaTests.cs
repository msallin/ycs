using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The range of a String slot in the slab's character arena, the only holder of the text, as each write path keeps it (the
/// store's checks, which every transaction of every suite runs, find a range outside a held page; these tests state what the
/// counters and offsets are). They run with the default page sizes and merge rule and without forced compaction whatever the run mode.
/// </summary>
[TestClass]
public class CharArenaTests
{
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

    private static YDoc NewDoc(long client = 1) => new(new YDocOptions { ClientId = client }) { RunExtensionsDisabledForTests = true };

    private static ItemRef At(YDoc doc, long client, long clock) => doc.Store.Find(new ItemId(client, clock));

    private static long Payload(YDoc doc, ItemRef item) => doc.Store.Slab.Read(item.Handle).Payload;

    private static string Range(YDoc doc, ItemRef item) => doc.Store.Slab.Chars.Slice(Payload(doc, item), item.Length).ToString();

    private static (long Live, long Dead, int Tails) Counts(YDoc doc)
    {
        var chars = doc.Store.Slab.Chars;
        return (chars.Live, chars.Dead, chars.TailCount);
    }

    [TestMethod]
    public void Insert_Text_RangeHoldsTheText()
    {
        var doc = NewDoc();
        doc.GetText("t").Insert(0, "hello");

        Assert.AreEqual(("hello", (5L, 0L, 0)), (Range(doc, At(doc, 1, 0)), Counts(doc)));
    }

    [TestMethod]
    public void Typing_ExtendingARun_KeepsTheOffsetAndCountsNothingDead()
    {
        using var first256 = StaticScope.Set(ref Arena<char>.FirstPageForTests, 256);
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, "a");
        var first = Payload(doc, At(doc, 1, 0));

        for (var i = 1; i < 100; i++)
        {
            text.Insert(i, "b");
        }

        var item = At(doc, 1, 0);
        Assert.AreEqual((100, first, 100L, 0L), (item.Length, Payload(doc, item), doc.Store.Slab.Chars.Live, doc.Store.Slab.Chars.Dead));
    }

    [TestMethod]
    public void Typing_RunOutgrowsItsRoom_CopiesWithSlackAndTheOldRangeIsDead()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, new string('a', 255));
        var first = Payload(doc, At(doc, 1, 0));

        text.Insert(255, "bb");

        var item = At(doc, 1, 0);
        Assert.AreEqual((257, true, 257L, 255L), (item.Length, Payload(doc, item) != first, doc.Store.Slab.Chars.Live, doc.Store.Slab.Chars.Dead));
    }

    [TestMethod]
    public void Split_InTheMiddle_RightPartRangeStartsAtPayloadPlusDiff()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abcd");

        text.Insert(2, "X");

        var left = At(doc, 1, 0);
        var right = At(doc, 1, 2);
        Assert.AreEqual((Payload(doc, left) + 2, "ab", "cd", (5L, 0L, 0)), (Payload(doc, right), Range(doc, left), Range(doc, right), Counts(doc)));
    }

    [TestMethod]
    public void Split_InsideASurrogatePair_PutsTheReplacementCharacterInPlaceOnBothSides()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "a😀b");

        text.Insert(2, "X");

        Assert.AreEqual(("a�", "�b", "a�X�b"), (Range(doc, At(doc, 1, 0)), Range(doc, At(doc, 1, 3)), text.ToString()));
    }

    [TestMethod]
    public void Merge_RangesAdjacent_TheRangeExtendsAndNothingIsDead()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        doc.Transact(_ =>
        {
            text.Insert(0, "ab");
            text.Insert(2, "cd");
        });

        var item = At(doc, 1, 0);
        Assert.AreEqual((1, "abcd", (4L, 0L, 0)), (doc.Store.Clients[1].Count, Range(doc, item), Counts(doc)));
    }

    [TestMethod]
    public void Merge_RangesNotAdjacent_CopiesWithSlackAndBothOldRangesAreDead()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "ab");
        var peer = NewDoc(2);
        peer.GetText("o").Insert(0, "zz");
        doc.ApplyUpdate(peer.EncodeStateAsUpdate());

        text.Insert(2, "cd");

        Assert.AreEqual((1, "abcd", (6L, 4L, 1)), (doc.Store.Clients[1].Count, Range(doc, At(doc, 1, 0)), Counts(doc)));
    }

    [TestMethod]
    public void Merge_CopyOnMergeSwitch_CopiesAdjacentRanges()
    {
        ItemRef.CopyOnMergeForTests = true;
        var doc = NewDoc();
        var text = doc.GetText("t");
        doc.Transact(_ =>
        {
            text.Insert(0, "ab");
            text.Insert(2, "cd");
        });

        Assert.AreEqual((1, "abcd", (4L, 4L)), (doc.Store.Clients[1].Count, Range(doc, At(doc, 1, 0)), (doc.Store.Slab.Chars.Live, doc.Store.Slab.Chars.Dead)));
    }

    [TestMethod]
    public void Split_RunWithAReservation_TheRightPartKeepsItAndNothingIsDeadBut255()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, new string('a', 255));
        text.Insert(255, "bb");

        text.Insert(100, "X");

        Assert.AreEqual((258L, 255L, 1), Counts(doc));
    }

    [TestMethod]
    public void Gc_DeletedItem_RangeDiesAndTheSlotHoldsNoOffset()
    {
        var doc = NewDoc();
        var text = doc.GetText("t");
        text.Insert(0, "abc");

        text.Remove(0, 3);

        var item = At(doc, 1, 0);
        Assert.AreEqual((SlotKind.Deleted, 0L, (0L, 3L, 0)), (item.Kind, Payload(doc, item), Counts(doc)));
    }

    [TestMethod]
    public void Gc_ParentCollected_RangeOfANestedTextDies()
    {
        var doc = NewDoc();
        var map = doc.GetMap("m");
        var inner = new YText();
        map["t"] = inner;
        inner.Insert(0, "abc");

        map.Remove("t");

        Assert.AreEqual((0L, 3L), (doc.Store.Slab.Chars.Live, doc.Store.Slab.Chars.Dead));
    }

    [TestMethod]
    public void Gc_Disabled_DeletedItemKeepsItsRange()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1, GarbageCollection = false }) { RunExtensionsDisabledForTests = true };
        var text = doc.GetText("t");
        text.Insert(0, "abc");

        text.Remove(0, 3);

        Assert.AreEqual(("abc", (3L, 0L, 0)), (Range(doc, At(doc, 1, 0)), Counts(doc)));
    }

    [TestMethod]
    public void Undo_RedoneText_GetsARangeOfItsOwn()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        var undo = new UndoManager(text);
        text.Insert(0, "abc");
        undo.StopCapturing();
        text.Remove(0, 3);
        undo.StopCapturing();

        undo.Undo();

        var ranges = doc.Store.Clients[1].Where(s => s.Kind == SlotKind.String).Select(s => (Payload(doc, s), Range(doc, s))).ToList();
        Assert.AreEqual((true, 2), (ranges.All(r => r.Item2 == "abc"), ranges.Select(r => r.Item1).Distinct().Count()));
    }

    [TestMethod]
    public void Apply_ItemIntegratedFromAnOffset_CopiesOnlyTheRestAndLeavesNothingDead()
    {
        var a = new YDoc(new YDocOptions { ClientId = 1 });
        a.GetText("t").Insert(0, "ab");
        var b = NewDoc(2);
        b.ApplyUpdate(a.EncodeStateAsUpdate());
        a.GetText("t").Insert(2, "cd");

        b.ApplyUpdate(a.EncodeStateAsUpdate());

        var chars = b.Store.Slab.Chars;
        Assert.AreEqual(("abcd", 4L, 0L), (b.GetText("t").ToString(), chars.Live, chars.Dead));
    }

    [TestMethod]
    public void Apply_ItemIntegratedFromAnOffsetInsideAPair_StartsWithTheReplacementCharacter()
    {
        var held = new YDoc(new YDocOptions { ClientId = 1 });
        held.GetText("t").Insert(0, "a\uFFFD");
        var other = new YDoc(new YDocOptions { ClientId = 1 });
        other.GetText("t").Insert(0, "a\uD83D\uDE00b");
        var b = NewDoc(2);
        b.ApplyUpdate(held.EncodeStateAsUpdate());

        b.ApplyUpdate(other.EncodeStateAsUpdate());

        Assert.AreEqual(("a\uFFFD\uFFFDb", "a\uFFFD\uFFFDb"), (b.GetText("t").ToString(), Range(b, At(b, 1, 0))));
    }

    [TestMethod]
    public void Insert_RunLongerThanAPage_GetsAPageOfItsOwnAndExtendsInIt()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, new string('a', 40_000));
        var first = Payload(doc, At(doc, 1, 0));

        text.Insert(40_000, "bc");

        var item = At(doc, 1, 0);
        Assert.AreEqual((40_002, first, 40_002L, 0L), (item.Length, Payload(doc, item), doc.Store.Slab.Chars.Live, doc.Store.Slab.Chars.Dead));
    }

    [TestMethod]
    public void Store_ManyEditsOfManyClients_EveryRangeEqualsItsText()
    {
        var random = new Random(8);
        var docs = Enumerable.Range(1, 3).Select(i => NewDoc(i)).ToArray();
        var alphabet = new[] { "a", "bc", "😀", "xyz" };
        for (var step = 0; step < 600; step++)
        {
            var doc = docs[random.Next(docs.Length)];
            var text = doc.GetText("t");
            if (text.Length > 0 && random.Next(3) == 0)
            {
                var at = random.Next(text.Length);
                text.Remove(at, Math.Min(random.Next(1, 4), text.Length - at));
            }
            else
            {
                text.Insert(random.Next(text.Length + 1), alphabet[random.Next(alphabet.Length)]);
            }

            if (step % 7 == 0)
            {
                var from = docs[random.Next(docs.Length)];
                doc.ApplyUpdate(from.EncodeStateAsUpdate());
            }
        }

        for (var round = 0; round < 2; round++)
        {
            foreach (var doc in docs)
            {
                foreach (var other in docs)
                {
                    doc.ApplyUpdate(other.EncodeStateAsUpdate());
                }
            }
        }

        Assert.AreEqual(1, docs.Select(d => d.GetText("t").ToString()).Distinct().Count());
    }
}
