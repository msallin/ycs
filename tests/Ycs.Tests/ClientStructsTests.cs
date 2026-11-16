using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The start clocks and leaves of a client's structs (<see cref="ClientStructs"/>) against a linear scan of the structs.</summary>
[TestClass]
public class ClientStructsTests
{
    /// <summary>Lists made in <paramref name="body"/> have leaves of <paramref name="capacity"/> positions.</summary>
    internal static T WithLeafCapacity<T>(int capacity, Func<T> body)
    {
        var before = ClientStructs.LeafCapacityForTests;
        ClientStructs.LeafCapacityForTests = capacity;
        try
        {
            return body();
        }
        finally
        {
            ClientStructs.LeafCapacityForTests = before;
        }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(128)]
    public void FindIndex_RandomEditsSplitsAndMerges_EqualsLinearScan(int capacity) => WithLeafCapacity(capacity, () =>
    {
        RandomEdits();
        return 0;
    });

    private static void RandomEdits()
    {
        var random = new Random(11);
        for (var round = 0; round < 60; round++)
        {
            var doc = new YDoc(new YDocOptions { Gc = round % 2 == 0 });
            var text = doc.GetText("t");
            var array = doc.GetArray("a");
            var remote = new YDoc();
            for (var step = 0; step < 300; step++)
            {
                // Typing, deletes (splits; merges at the end of a transaction) and remote edits (other clients).
                switch (random.Next(6))
                {
                    case 0:
                        text.Insert(random.Next(text.Length + 1), new string((char)('a' + random.Next(26)), 1 + random.Next(3)));
                        break;
                    case 1 when text.Length > 0:
                        var at = random.Next(text.Length);
                        text.Delete(at, Math.Min(1 + random.Next(3), text.Length - at));
                        break;
                    case 2:
                        array.Insert(random.Next(array.Length + 1), new object[] { (long)step, (long)round });
                        break;
                    case 3 when array.Length > 0:
                        array.Delete(random.Next(array.Length), 1);
                        break;
                    case 4:
                        remote.ApplyUpdateV2(doc.EncodeStateAsUpdateV2(remote.EncodeStateVectorV2()));
                        remote.GetText("t").Insert(0, "r");
                        doc.ApplyUpdateV2(remote.EncodeStateAsUpdateV2(doc.EncodeStateVectorV2()));
                        break;
                    default:
                        doc.Transact(_ =>
                        {
                            for (var k = random.Next(8); k > 0; k--)
                            {
                                text.Insert(random.Next(text.Length + 1), "x");
                            }
                        });
                        break;
                }

                if (step % 25 == 0)
                {
                    CheckAgainstLinearScan(doc.Store, random);
                }
            }

            CheckAgainstLinearScan(doc.Store, random);
        }
    }

    /// <summary>
    /// A list that grows past one leaf gets a directory, and loses it when merges bring it back to one leaf: the
    /// structs, starts and every derived number equal a recount on the way.
    /// </summary>
    [TestMethod]
    public void RemoveMerged_DownToOneLeaf_DropsTheDirectory()
    {
        var outcome = WithLeafCapacity(4, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var array = doc.GetArray("a");
            array.Insert(0, new object[] { 0L, 1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L, 11L });
            for (var k = 0; k < 6; k++)
            {
                array.Delete(k, 1);
            }

            var structs = doc.Store.Clients[1];
            var split = (structs.Count, structs.Violations());
            doc.Transact(_ =>
            {
                while (array.Length > 0)
                {
                    array.Delete(0, 1);
                }
            });

            return (split, structs.Count, structs.Violations(), string.Join(",", structs.Select(s => s.Length)));
        });

        Assert.AreEqual(((12, (string)null), 1, (string)null, "12"), outcome);
    }

    [TestMethod]
    public void FindIndex_ClockOutsideTheList_ThrowsAsBefore()
    {
        var doc = new YDoc();
        doc.GetText("t").Insert(0, "abc");
        var structs = doc.Store.Clients[doc.ClientId];
        var hint = 0;

        var outcome = (
            Message(() => structs.FindIndex(3)),
            Message(() => structs.FindIndex(-1)),
            Message(() => structs.FindIndex(3, ref hint)));

        Assert.AreEqual(
            ("Clock 3 is not in the store (it ends at 3).", "Clock -1 is not in the store (it ends at 3).", "Clock 3 is not in the store (it ends at 3)."),
            outcome);
    }

    /// <summary>A list that does not start at clock 0 (as a plan's rest), with a directory: a clock below its first start is not in it.</summary>
    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public void FindIndex_ClockBelowTheFirstStartWithLeaves_ThrowsAsWithoutLeaves(int leaves) => WithLeafCapacity(2, () =>
    {
        var doc = new YDoc(new YDocOptions { Gc = false });
        var text = doc.GetText("t");
        for (var i = 0; i < 10 + (2 * leaves); i++)
        {
            text.Insert(0, "x");
        }

        // From clock 10: clocks far below the first start, as well as just below it.
        var rest = new ClientStructs(doc.Store.Clients[doc.ClientId].ToList(), 10, 2 * leaves);
        var hint = 1;

        var outcome = (Message(() => rest.FindIndex(9)), Message(() => rest.FindIndex(0)), Message(() => rest.FindIndex(0, ref hint)));

        Assert.AreEqual(("Clock 9 is not in the store.", "Clock 0 is not in the store.", "Clock 0 is not in the store."), outcome);
        return 0;
    });

    private static string Message(Func<int> find)
    {
        try
        {
            find();
            return null;
        }
        catch (InvalidOperationException e)
        {
            return e.Message;
        }
    }

    private static void CheckAgainstLinearScan(StructStore store, Random random)
    {
        foreach (var (client, structs) in store.Clients)
        {
            Assert.IsNull(structs.Violations(), $"client {client}");
            var hint = random.Next(structs.Count);
            for (var probe = 0; probe < 50; probe++)
            {
                var clock = random.NextInt64(structs.End);
                var expected = LinearScan(structs, clock);
                var outcome = (structs.FindIndex(clock), structs.FindIndex(clock, ref hint), hint);
                Assert.AreEqual((expected, expected, expected), outcome, $"client {client} clock {clock}");
            }
        }
    }

    private static int LinearScan(IReadOnlyList<AbstractStruct> structs, long clock)
    {
        for (var i = 0; i < structs.Count; i++)
        {
            if (structs[i].Id.Clock <= clock && clock < structs[i].Id.Clock + structs[i].Length)
            {
                return i;
            }
        }

        return -1;
    }
}
