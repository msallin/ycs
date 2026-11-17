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
    public TestContext TestContext { get; set; } = null!;

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
    [TestCategory("Slow")]
    public void UndoManager_ManyEditsInOneCapture_IsNotQuadratic()
    {
#if DEBUG
        Assert.Inconclusive("Timed with -c Release (a Debug build checks every marker lookup against Yjs' literal walk).");
#endif
        if (Slab.CompactEveryTimeForTests)
        {
            Assert.Inconclusive("Compacting on every call is quadratic by construction.");
        }

        ScalingProbe.AssertNotQuadratic(
            n =>
            {
                var doc = new YDoc(new YDocOptions { ClientId = 1 });
                var text = doc.GetText("t");
                text.Insert(0, new string('a', n));
                _ = new UndoManager(text, new UndoManagerOptions { CaptureTimeout = TimeSpan.MaxValue });
                var random = new Random(1);
                return () =>
                {
                    for (var i = 0; i < n; i++)
                    {
                        text.Insert(random.Next(text.Length + 1), "bb");
                        text.Remove(random.Next(text.Length), 1);
                    }
                };
            },
            20_000,
            TestContext);
    }

    [TestMethod]
    public void DeepObserver_ManyChangedMaps_SortsWithoutWalkingTheSiblings()
    {
        ScalingProbe.AssertNotQuadratic(
            n =>
            {
                var doc = new YDoc(new YDocOptions { ClientId = 1 });
                var array = doc.GetArray("a");
                var maps = Enumerable.Range(0, n).Select(_ => new YMap()).ToList();
                array.Insert(0, maps.Cast<object>().ToList().Select(YValue.FromObject).ToArray());
                var events = 0;
                array.ObserveDeep((deepEvents, _) => events += deepEvents.Count);
                return () => doc.Transact(_ => maps.ForEach(map => map["k"] = 1));
            },
            64_000,
            TestContext);
    }

    [TestMethod]
    public void Get_EveryIndexOfALegacyJsonItem_IsConstantPerIndex()
    {
        ScalingProbe.AssertNotQuadratic(
            n =>
            {
                // One ContentJSON item (content 2) of n values "1" in the root array "a".
                var update = new List<byte> { 1, 1, 9, 0, 2, 1, 1, (byte)'a' };
                var length = new Lib0Writer();
                length.WriteVarUint((ulong)n);
                update.AddRange(length.ToArray());
                for (var i = 0; i < n; i++)
                {
                    update.Add(1);
                    update.Add((byte)'1');
                }

                update.Add(0);
                var doc = new YDoc();
                doc.ApplyUpdate(update.ToArray());
                var array = doc.GetArray("a");
                return () =>
                {
                    for (var i = 0; i < array.Length; i++)
                    {
                        _ = array[i].ToObject();
                    }
                };
            },
            40_000,
            TestContext);
    }

    [TestMethod]
    public void Get_UndefinedInALegacyJsonItem_IsNull()
    {
        // ["undefined", "1"]
        byte[] update = [1, 1, 9, 0, 2, 1, 1, (byte)'a', 2, 9, .. "undefined"u8.ToArray(), 1, (byte)'1', 0];
        var doc = new YDoc();
        doc.ApplyUpdate(update);

        Assert.AreEqual((null, 1), (doc.GetArray("a")[0].ToObject(), doc.GetArray("a")[1].ToObject()));
    }
}
