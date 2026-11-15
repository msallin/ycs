using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>The start clocks of a client's structs (<see cref="ClientStructs"/>) against a linear scan of the structs.</summary>
[TestClass]
public class ClientStructsTests
{
    [TestMethod]
    public void FindIndex_RandomEditsSplitsAndMerges_EqualsLinearScan()
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
