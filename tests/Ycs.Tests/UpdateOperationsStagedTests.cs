using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

/// <summary>
/// What the staged update operations do at a surrogate pair and at a gap, on updates written by hand: the v2 string column is
/// one string cut by the lengths, so a pair may be cut between two structs and rejoined by the encoder.
/// </summary>
[TestClass]
public class UpdateOperationsStagedTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MergeUpdates_CutInsideASurrogatePair_RightPartStartsWithTheReplacement(bool v2)
    {
        // Client 1 holds a deleted range [0, 2) and, in another update, "a" + a pair [0, 3): the merge cuts the string at 2, inside the pair.
        var carrier = Update(v2, (2, 0, [Text("q\uD83D")]), (1, 10, [Text("\uDE00z")]));
        var deleted = Update(v2, (1, 0, [Deleted(2)]));
        var pair = Update(v2, (1, 0, [Text("a😀")]));

        var merged = Merge(v2, carrier, deleted, pair);

        var cut = Decode(v2, merged).Single(s => s.Id.Equals(new ItemId(1, 2)));
        Assert.AreEqual(("�", new ItemId(1, 1)), (cut.Text, cut.LeftOrigin));
    }

    [TestMethod]
    public void DiffUpdate_OffsetInsideASurrogatePair_WritesTheLowSurrogateAsItIs()
    {
        // The string column joins "q" + a high surrogate (client 2) to the low surrogate that starts what the state vector leaves
        // of client 1's string: a replacement written there would not join.
        var input = Update(true, (2, 0, [Text("q\uD83D")]), (3, 0, [Text("\uDE00y")]), (1, 0, [Text("ab😀")]));

        var diff = YUpdate.Diff(input, StateVector((3, 100), (1, 3)), format: UpdateFormat.V2);

        var texts = YUpdate.Decode(diff, format: UpdateFormat.V2).Structs.Select(s => (s.Id, s.Text, s.LeftOrigin)).ToList();
        CollectionAssert.AreEqual(
            new[] { (new ItemId(2, 0), "q\uD83D", (ItemId?)new ItemId(9, 0)), (new ItemId(1, 3), "\uDE00", (ItemId?)new ItemId(1, 2)) },
            texts);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MergeUpdates_AGapFollowedByAGc_KeepsTheSkipAndTheGc(bool v2)
    {
        var first = Update(v2, (1, 0, [Deleted(1)]));
        var gc = Update(v2, (1, 2, [Gc(2)]));

        var merged = Merge(v2, first, gc);

        CollectionAssert.AreEqual(
            new[] { (ItemKind.Deleted, 1L), (ItemKind.Skip, 1L), (ItemKind.GC, 2L) },
            Decode(v2, merged).Select(s => (s.Kind, s.Length)).ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LazyStructReader_ManyOriginClients_ForgetsTheClientsItStreamedPast(bool v2)
    {
        var structs = Enumerable.Range(0, 1000).Select(k => (Action<IUpdateEncoder, ItemId>)((e, id) =>
            WriteDeleted(e, id, new ItemId(100 + k, 0), 1))).ToArray();
        var bytes = Update(v2, (1, 0, structs));
        var reader = new LazyStructReader<IUpdateDecoder>(
            v2 ? new UpdateDecoderV2(new Lib0Reader(bytes), long.MaxValue) : new UpdateDecoderV1(new Lib0Reader(bytes), long.MaxValue),
            bytes,
            filterSkips: false,
            new StagingTable());
        var most = 0;
        var read = 0;

        for (; reader.Curr >= 0; reader.Next(bytes))
        {
            most = Math.Max(most, reader.Table.ClientCount);
            read++;
        }

        Assert.AreEqual((1000, true), (read, most <= 70));
    }

    [TestMethod]
    public void DecodedStruct_AnyContent_IsAReadOnlyViewAndByteArraysAreCopiedOnRead()
    {
        var values = DecodedStructTests.RichUpdates()
            .SelectMany(u => YUpdate.Decode(u, format: UpdateFormat.V1).Structs)
            .Where(s => s.Kind == ItemKind.Any)
            .Select(s => s.Content)
            .ToList();

        Assert.IsNotEmpty(values);
        Assert.IsTrue(values.All(c => c is not object?[] && c is not IList<object?>));
        var bytes = values.SelectMany(c => c).OfType<byte[]>().ToList();
        if (bytes.Count > 0)
        {
            var first = values.First(c => c.Any(v => v is byte[]));
            var at = first.ToList().FindIndex(v => v is byte[]);
            Assert.AreNotSame(first[at], first[at]);
        }
    }

    [TestMethod]
    public void Return_ManyLargeTables_KeepsAtMostTheRetainedBytes()
    {
        var tables = Enumerable.Range(0, 20).Select(_ => StagingTable.Rent()).ToList();
        foreach (var t in tables)
        {
            t.BeginClient(1, 0);
            t.Chars(t.Add(StagedKind.String, 50_000, new ItemId(2, 0)))[0] = 'x';
        }

        foreach (var t in tables)
        {
            StagingTable.Return(t);
        }

        var kept = new List<StagingTable>();
        for (var k = 0; k < 20; k++)
        {
            kept.Add(StagingTable.Rent());
        }

        Assert.IsTrue(kept.Sum(t => t.CapacityBytes) <= StagingTable.RetainedBytesCap);
    }

    private static byte[] Merge(bool v2, params byte[][] updates) =>
        v2 ? YUpdate.Merge([.. updates], format: UpdateFormat.V2) : YUpdate.Merge([.. updates], format: UpdateFormat.V1);

    private static IReadOnlyList<DecodedStruct> Decode(bool v2, byte[] update) =>
        (v2 ? YUpdate.Decode(update, format: UpdateFormat.V2) : YUpdate.Decode(update, format: UpdateFormat.V1)).Structs;

    private static byte[] StateVector(params (long Client, long Clock)[] entries)
    {
        var writer = new Lib0Writer();
        writer.WriteVarUint((uint)entries.Length);
        foreach (var (client, clock) in entries)
        {
            writer.WriteVarUint((ulong)client);
            writer.WriteVarUint((ulong)clock);
        }

        return writer.ToArray();
    }

    // The items have an origin and so no parent: a parent's name would be a string between the texts in the v2 string column.
    private static Action<IUpdateEncoder, ItemId> Text(string text) =>
        (encoder, id) =>
        {
            ItemRef.WriteItemHeader(encoder, (int)SlotKind.String, 0, id, new ItemId(9, 0), null, null, null, default);
            encoder.WriteString(text);
        };

    private static Action<IUpdateEncoder, ItemId> Deleted(int length) =>
        (encoder, id) => WriteDeleted(encoder, id, new ItemId(9, 0), length);

    private static void WriteDeleted(IUpdateEncoder encoder, ItemId id, ItemId origin, int length)
    {
        ItemRef.WriteItemHeader(encoder, (int)SlotKind.Deleted, 0, id, origin, null, null, null, default);
        encoder.WriteLength(length);
    }

    private static Action<IUpdateEncoder, ItemId> Gc(int length) => (encoder, _) =>
    {
        encoder.WriteInfo((byte)SlotKind.GC);
        encoder.WriteLength(length);
    };

    // One client section per entry, in the order given: (client, clock of its first struct, its structs), then an empty delete set.
    private static byte[] Update(bool v2, params (long Client, long Clock, Action<IUpdateEncoder, ItemId>[] Structs)[] sections)
    {
        var encoder = v2 ? (IUpdateEncoder)UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
        try
        {
            encoder.RestWriter.WriteVarUint((uint)sections.Length);
            foreach (var (client, clock, structs) in sections)
            {
                encoder.RestWriter.WriteVarUint((uint)structs.Length);
                encoder.WriteClient(client);
                encoder.RestWriter.WriteVarUint((uint)clock);
                foreach (var write in structs)
                {
                    write(encoder, new ItemId(client, clock));
                }
            }

            encoder.RestWriter.WriteVarUint(0);
            return encoder.ToArray();
        }
        finally
        {
            EncodingUtils.ReturnEncoder(encoder);
        }
    }
}
