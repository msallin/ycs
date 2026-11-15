using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// Operations whose cost grew with what they touch (the second review's P2 to P4): each scenario took
/// seconds before and takes milliseconds now; the ceilings only catch a quadratic path coming back.
/// </summary>
[TestClass]
public class ScalingTests
{
    [TestMethod]
    public void AddSortedAndMerged_RandomRanges_EqualsMergingTheSets()
    {
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var a = RandomSet(random);
            var b = RandomSet(random);
            var expected = new DeleteSet(new List<DeleteSet> { a, b });

            a.AddSortedAndMerged(b);

            Assert.AreEqual(Describe(expected), Describe(a));
        }
    }

    private static DeleteSet RandomSet(Random random)
    {
        var set = new DeleteSet();
        for (var i = random.Next(12); i > 0; i--)
        {
            set.Add(random.Next(3), random.Next(60), 1 + random.Next(6));
        }

        set.SortAndMergeDeleteSet();
        return set;
    }

    private static string Describe(DeleteSet set) => string.Join(" ", set.Clients.OrderBy(c => c.Key)
        .Select(c => c.Key + ":" + string.Join(",", c.Value.Select(r => r.Clock + "+" + r.Length))));

    [TestMethod]
    public void UndoManager_ManyEditsInOneCapture_IsNotQuadratic()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var text = doc.GetText("t");
        text.Insert(0, new string('a', 20_000));
        _ = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = int.MaxValue });
        var random = new Random(1);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 20_000; i++)
        {
            text.Insert(random.Next(text.Length + 1), "bb");
            text.Delete(random.Next(text.Length), 1);
        }

        Assert.IsLessThan(3_000, stopwatch.ElapsedMilliseconds);
    }

    [TestMethod]
    public void DeepObserver_ManyChangedMaps_SortsWithoutWalkingTheSiblings()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var array = doc.GetArray("a");
        var maps = Enumerable.Range(0, 16_000).Select(_ => new YMap()).ToList();
        array.Insert(0, maps.Cast<object>().ToList());
        var events = 0;
        array.DeepEventHandler += (_, e) => events += e.Events.Count;

        var stopwatch = Stopwatch.StartNew();
        doc.Transact(_ => maps.ForEach(map => map.Set("k", 1)));

        Assert.IsLessThan(500, stopwatch.ElapsedMilliseconds);
    }

    [TestMethod]
    public void Get_EveryIndexOfALegacyJsonItem_IsConstantPerIndex()
    {
        // One ContentJSON item (content 2) of 40,000 values "1" in the root array "a".
        var update = new List<byte> { 1, 1, 9, 0, 2, 1, 1, (byte)'a', 0xC0, 0xB8, 0x02 };
        for (var i = 0; i < 40_000; i++)
        {
            update.Add(1);
            update.Add((byte)'1');
        }

        update.Add(0);
        var doc = new YDoc();
        doc.ApplyUpdate(update.ToArray());
        var array = doc.GetArray("a");

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < array.Length; i++)
        {
            _ = array.Get(i);
        }

        Assert.IsLessThan(500, stopwatch.ElapsedMilliseconds);
    }

    [TestMethod]
    public void Get_UndefinedInALegacyJsonItem_IsNull()
    {
        // ["undefined", "1"]
        byte[] update = [1, 1, 9, 0, 2, 1, 1, (byte)'a', 2, 9, .. "undefined"u8.ToArray(), 1, (byte)'1', 0];
        var doc = new YDoc();
        doc.ApplyUpdate(update);

        Assert.AreEqual((null, 1), (doc.GetArray("a").Get(0), doc.GetArray("a").Get(1)));
    }
}
