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

        var cut = Decode(v2, merged).Single(s => s.Id.Equals(new ID(1, 2)));
        Assert.AreEqual(("�", new ID(1, 1)), (cut.Text, cut.LeftOrigin));
    }

    [TestMethod]
    public void DiffUpdate_OffsetInsideASurrogatePair_WritesTheLowSurrogateAsItIs()
    {
        // The string column joins "q" + a high surrogate (client 2) to the low surrogate that starts what the state vector leaves
        // of client 1's string: a replacement written there would not join.
        var input = Update(true, (2, 0, [Text("q\uD83D")]), (3, 0, [Text("\uDE00y")]), (1, 0, [Text("ab😀")]));

        var diff = UpdateOperations.DiffUpdateV2(input, StateVector((3, 100), (1, 3)));

        var texts = UpdateOperations.DecodeUpdateV2(diff).Structs.Select(s => (s.Id, s.Text, s.LeftOrigin)).ToList();
        CollectionAssert.AreEqual(
            new[] { (new ID(2, 0), "q\uD83D", (ID?)new ID(9, 0)), (new ID(1, 3), "\uDE00", (ID?)new ID(1, 2)) },
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
            new[] { (ItemKind.Deleted, 1), (ItemKind.Skip, 1), (ItemKind.GC, 2) },
            Decode(v2, merged).Select(s => (s.Kind, s.Length)).ToArray());
    }

    private static byte[] Merge(bool v2, params byte[][] updates) =>
        v2 ? UpdateOperations.MergeUpdatesV2(updates) : UpdateOperations.MergeUpdates(updates);

    private static IReadOnlyList<DecodedStruct> Decode(bool v2, byte[] update) =>
        (v2 ? UpdateOperations.DecodeUpdateV2(update) : UpdateOperations.DecodeUpdate(update)).Structs;

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
    private static Action<IUpdateEncoder, ID> Text(string text) =>
        (encoder, id) => Item.WriteItem(encoder, 0, id, new ID(9, 0), null, null, new ContentString(text), null, default);

    private static Action<IUpdateEncoder, ID> Deleted(int length) =>
        (encoder, id) => Item.WriteItem(encoder, 0, id, new ID(9, 0), null, null, new ContentDeleted(length), null, default);

    private static Action<IUpdateEncoder, ID> Gc(int length) => (encoder, _) =>
    {
        encoder.WriteInfo(GC.StructGCRefNumber);
        encoder.WriteLength(length);
    };

    // One client section per entry, in the order given: (client, clock of its first struct, its structs), then an empty delete set.
    private static byte[] Update(bool v2, params (long Client, long Clock, Action<IUpdateEncoder, ID>[] Structs)[] sections)
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
                    write(encoder, new ID(client, clock));
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
