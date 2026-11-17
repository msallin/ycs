using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// The structs an update cannot integrate yet are written from the staging table, as Yjs writes them from its decoded
/// structs; both writers are kept until the objects go, and must give the same bytes: the plan's table writer and the objects'.
/// </summary>
[TestClass]
public class PendingRestEncodingTests
{
    private static readonly Dictionary<string, YValue> Bold = new() { ["bold"] = true };

    [TestMethod]
    public void Rest_EveryContentKindInEveryOrder_IsTheBytesTheObjectsWrite()
    {
        var (compared, withRest) = (0, 0);

        foreach (var updates in Histories())
        {
            foreach (var order in Orders(updates.Count))
            {
                var doc = new YDoc(new YDocOptions { ClientId = 99 });
                foreach (var i in order)
                {
                    var (equal, hadRest, _) = Compare(doc, updates[i]);
                    compared++;
                    withRest += hadRest ? 1 : 0;
                    Assert.IsTrue(equal, $"order {string.Join(",", order)}, update {i}");
                    doc.ApplyUpdate(updates[i]);
                }
            }
        }

        Assert.IsTrue(compared > 100 && withRest > 50, $"{compared} updates compared, {withRest} with structs set aside");
    }

    [TestMethod]
    public void Rest_MergedUpdateWithASkip_IsTheBytesTheObjectsWrite()
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var updates = Capture(source);
        var text = source.GetText("t");
        text.Insert(0, "abc");
        text.Insert(3, "def");
        text.Insert(6, "ghi");
        var merged = UpdateOperations.MergeUpdates([new ArraySegment<byte>(updates[0]), new ArraySegment<byte>(updates[2])], false, UpdateLimits.Default);

        var (equal, hadRest, _) = Compare(new YDoc(), merged);

        Assert.AreEqual((true, true), (equal, hadRest));
    }

    [TestMethod]
    public void Rest_TwoStructsOfOneClientOnTheStack_KeepsOnlyTheLastAsYjsDoes()
    {
        // Client 2 holds A0 (waits for client 1) and A1; client 1 holds B0 (waits for A1). The walk takes A0, B0, then A1,
        // which is past the state of its client: the stack holds A0, B0, A1 at the dead wall, and A1 replaces what
        // A0 put aside. Only a hostile update has this shape; the objects and the table are written the same.
        var writer = new Lib0Writer();
        writer.WriteVarUint(2);
        writer.WriteVarUint(2);
        writer.WriteVarUint(2);
        writer.WriteVarUint(0);
        WriteString(writer, 1, 0, "x");
        WriteString(writer, 2, 0, "y");
        writer.WriteVarUint(1);
        writer.WriteVarUint(1);
        writer.WriteVarUint(0);
        WriteString(writer, 2, 1, "z");
        writer.WriteVarUint(0);
        var update = writer.ToArray();

        var (equal, hadRest, structs) = Compare(new YDoc(), update);

        Assert.AreEqual((true, true, 2L), (equal, hadRest, structs));
    }

    private static void WriteString(Lib0Writer writer, ulong originClient, ulong originClock, string text)
    {
        writer.WriteByte(0x84);
        writer.WriteVarUint(originClient);
        writer.WriteVarUint(originClock);
        writer.WriteVarString(text);
    }

    private static (bool Equal, bool HadRest, long Structs) Compare(YDoc doc, byte[] update)
    {
        using var lease = UpdateReader.Read(update, 0, update.Length, v2: false, long.MaxValue);
        var table = lease.Staging;
        var plan = new StructStore.IntegrationPlan(doc.Store);
        plan.Plan(table, int.MaxValue);
        doc.Store.ForgetReservations();
        if (plan.Rest == null)
        {
            return (true, false, 0);
        }

        var (staged, structs) = plan.EncodeRest();
        var rest = new Dictionary<long, List<AbstractStruct>>();
        foreach (var (client, (start, count)) in plan.Rest)
        {
            rest[table.ClientAt(client)] = lease.Structs[table.ClientAt(client)].GetRange(start - table.RangeOf(client).Start, count);
        }

        var encoder = UpdateEncoderV2.Rent();
        try
        {
            StructStore.WriteRest(encoder, rest);
            encoder.RestWriter.WriteVarUint(0);
            return (staged.AsSpan().SequenceEqual(encoder.ToArray()), true, structs);
        }
        finally
        {
            encoder.Return();
        }
    }

    private static List<byte[]> Capture(YDoc source)
    {
        var updates = new List<byte[]>();
        source.Updated += (_, e) => updates.Add(e.Update.ToArray());
        return updates;
    }

    /// <summary>Histories of several clients over every content kind, as the updates their transactions made, each applied to the next client.</summary>
    private static IEnumerable<List<byte[]>> Histories()
    {
        // One client: strings, formats, embeds, deletions (items and, with gc, GC structs), nested types of each kind.
        var one = new YDoc(new YDocOptions { ClientId = 1 });
        var updates = Capture(one);
        var text = one.GetText("text");
        text.Insert(0, "hello 👍 world");
        text.Format(1, 3, Bold);
        text.InsertEmbed(2, "embed");
        text.Remove(4, 2);
        var array = one.GetArray("array");
        array.Insert(0, 1, "two", new byte[] { 3 }, 4.5);
        var nested = new YMap();
        array.Insert(1, nested);
        nested["k"] = "v";
        nested["n"] = new YText();
        array.RemoveAt(1);
        one.GetMap("map")["a"] = new YXmlElement("div");
        one.GetMap("map")["a"] = 5;
        yield return updates.ToList();

        // Two clients that edit the same text and map concurrently, so that origins refer across clients.
        var (x, y) = (new YDoc(new YDocOptions { ClientId = 5, GarbageCollection = false }), new YDoc(new YDocOptions { ClientId = 6, GarbageCollection = false }));
        var history = new List<byte[]>();
        x.Updated += (_, e) => history.Add(e.Update.ToArray());
        y.Updated += (_, e) => history.Add(e.Update.ToArray());
        x.GetText("t").Insert(0, "abc");
        y.ApplyUpdate(history[^1]);
        y.GetText("t").Insert(1, "XY");
        x.GetText("t").Insert(1, "pq");
        x.ApplyUpdate(history[^1]);
        x.GetMap("m")["k"] = "x";
        y.GetMap("m")["k"] = "y";
        y.GetArray("a").Insert(0, new YText());
        x.GetText("t").Remove(0, 2);
        yield return history.ToList();
    }

    // Forward, reversed, every rotation, and some shuffles (a history of n updates has n! orders).
    private static IEnumerable<List<int>> Orders(int n)
    {
        var forward = Enumerable.Range(0, n).ToList();
        yield return forward;
        yield return Enumerable.Reverse(forward).ToList();
        for (var r = 1; r < n; r++)
        {
            yield return forward.Skip(r).Concat(forward.Take(r)).ToList();
        }

        var random = new Random(7);
        for (var k = 0; k < 30; k++)
        {
            yield return forward.OrderBy(_ => random.Next()).ToList();
        }
    }
}
