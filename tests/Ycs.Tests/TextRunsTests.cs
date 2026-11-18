using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class TextRunsTests
{
    private static YDoc NewDoc() => new(new YDocOptions { GarbageCollection = false });

    private static List<(int Length, YAttributes? Attributes, YValue? Embed)> Mapped(IEnumerable<Delta> delta) =>
        delta.Cast<Delta.Insert>()
            .Select(op => op.Value.TryGetString(out var s) ? (s.Length, op.Attributes, (YValue?)null) : (1, op.Attributes, (YValue?)op.Value))
            .ToList();

    private static List<(int Length, YAttributes? Attributes, YValue? Embed)> Runs(IEnumerable<TextRun> runs) =>
        runs.Select(r => (r.Length, r.Attributes, r.Embed)).ToList();

    private static void AssertRunsMatchDelta(YText text, Snapshot? snapshot = null, Snapshot? previous = null, Func<YChangeKind, ItemId, YChange>? computeYChange = null)
    {
        var delta = snapshot == null && previous == null ? text.ToDelta() : text.ToDelta(snapshot, previous, computeYChange);
        var runs = text.ToRuns(snapshot, previous, computeYChange);

        CollectionAssert.AreEqual(Mapped(delta), Runs(runs));
    }

    [TestMethod]
    public void ToRuns_PlainText_IsOneRunOfItsLength()
    {
        var text = NewDoc().GetText();
        text.Insert(0, "hello world");

        var runs = text.ToRuns();

        Assert.AreEqual(new TextRun(11, null, null), runs.Single());
        Assert.IsFalse(runs.Single().IsEmbed);
    }

    [TestMethod]
    public void ToRuns_FormattedTextWithEmbeds_EqualsToDelta()
    {
        var text = NewDoc().GetText();
        text.Insert(0, "hello world");
        text.Format(0, 5, ("bold", true));
        text.InsertEmbed(3, (YValue)"image", [new("alt", "x")]);
        text.InsertEmbed(text.Length, YValue.Null);

        AssertRunsMatchDelta(text);
        Assert.IsTrue(text.ToRuns().Any(r => r.IsEmbed && r.Length == 1));
        Assert.AreEqual(text.Length, text.ToRuns().Sum(r => r.Length));
    }

    [TestMethod]
    public void ToRuns_TextNotInDocumentYet_EqualsToDelta()
    {
        var text = new YText();
        text.Insert(0, "abc");
        text.InsertEmbed(1, (YValue)7);

        AssertRunsMatchDelta(text);
    }

    [TestMethod]
    public void ToRuns_SnapshotNotInDocument_Throws()
    {
        var doc = NewDoc();
        var snapshot = doc.CreateSnapshot();

        Assert.ThrowsExactly<InvalidOperationException>(() => new YText().ToRuns(snapshot));
    }

    [TestMethod]
    public void ToRuns_PreviousAndCurrentSnapshots_EqualsToDeltaWithYChange()
    {
        var doc = NewDoc();
        var text = doc.GetText();
        text.Insert(0, "hello brave world");
        text.InsertEmbed(5, (YValue)"e");
        var previous = doc.CreateSnapshot();
        text.Remove(2, 4);
        text.Insert(text.Length, " again", [new("i", true)]);
        var current = doc.CreateSnapshot();

        AssertRunsMatchDelta(text, current, previous);
        AssertRunsMatchDelta(text, current, previous, (kind, id) => new YChange(kind, id.Client));
        AssertRunsMatchDelta(text, null, previous);
        AssertRunsMatchDelta(text, previous);
        Assert.IsTrue(text.ToRuns(current, previous).Any(r => r.Attributes?.ContainsKey("ychange") == true));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public void ToRuns_RandomEdits_EqualsToDelta(int seed)
    {
        var random = new Random(seed);
        var doc = NewDoc();
        var text = doc.GetText();
        var snapshots = new List<Snapshot> { doc.CreateSnapshot() };

        for (var step = 0; step < 60; step++)
        {
            var length = text.Length;
            switch (random.Next(5))
            {
                case 0:
                    text.Insert(random.Next(length + 1), new string((char)('a' + random.Next(26)), random.Next(1, 8)), random.Next(3) == 0 ? [new("b", true)] : null);
                    break;
                case 1 when length > 0:
                    text.Remove(random.Next(length), 1);
                    break;
                case 2 when length > 1:
                    var start = random.Next(length - 1);
                    text.Format(start, random.Next(1, length - start), ("c", random.Next(2) == 0 ? (YValue)1 : YValue.Null));
                    break;
                case 3:
                    text.InsertEmbed(random.Next(length + 1), (YValue)step);
                    break;
            }

            if (step % 10 == 9)
            {
                snapshots.Add(doc.CreateSnapshot());
            }
        }

        AssertRunsMatchDelta(text);
        for (var i = 1; i < snapshots.Count; i++)
        {
            AssertRunsMatchDelta(text, snapshots[i], snapshots[i - 1], (kind, id) => new YChange(kind, id.Client));
            AssertRunsMatchDelta(text, snapshots[i]);
        }
    }

    [TestMethod]
    public void ToRuns_LargeSingleRun_AllocatesNothingNearTheTextsSize()
    {
        var text = NewDoc().GetText();
        text.Insert(0, new string('x', 1 << 20));
        text.ToRuns();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var runs = text.ToRuns();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.AreEqual(1 << 20, runs.Single().Length);
        Assert.IsLessThan(4096, allocated);
    }

    [TestMethod]
    public void ToString_TextWithEmbeds_LeavesThemOut()
    {
        var text = NewDoc().GetText();
        text.Insert(0, "abcdef");
        text.InsertEmbed(2, (YValue)1);
        text.InsertEmbed(text.Length, (YValue)2);
        text.Remove(0, 1);

        Assert.AreEqual("bcdef", text.ToString());
        Assert.AreEqual(7, text.Length);
    }

    [TestMethod]
    public void ToString_OnlyEmbedsOrEmpty_IsEmpty()
    {
        var text = NewDoc().GetText();
        Assert.AreEqual(string.Empty, text.ToString());

        text.InsertEmbed(0, (YValue)1);

        Assert.AreEqual(string.Empty, text.ToString());
    }

    [TestMethod]
    public void ToString_UnchangedText_ReturnsTheSameInstance()
    {
        var text = NewDoc().GetText();
        text.Insert(0, "abc");
        text.InsertEmbed(1, (YValue)1);
        var first = text.ToString();

        Assert.AreSame(first, text.ToString());

        text.Insert(0, "z");

        Assert.AreEqual("zabc", text.ToString());
    }

    [TestMethod]
    public void ToString_AfterSnapshotReadsThatSplitItems_ReturnsTheSameInstance()
    {
        var doc = NewDoc();
        var text = doc.GetText();
        text.Insert(0, "hello brave world");
        var previous = doc.CreateSnapshot();
        text.Insert(6, "X");
        text.Remove(10, 3);
        var current = doc.CreateSnapshot();
        var first = text.ToString();

        text.ToRuns(current, previous);
        text.ToDelta(previous);
        text.ToDelta(current, previous);

        Assert.AreSame(first, text.ToString());

        text.Remove(0, 1);

        Assert.AreEqual(first[1..], text.ToString());
    }
}
