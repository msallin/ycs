#nullable enable

using System.Buffers;

namespace Ycs;

/// <summary>
/// Mirrors Yjs 13.6.32's <c>Snapshot</c> (src/utils/Snapshot.js): an immutable pair of a state
/// vector and a delete set that together pin down "the document as of this point in time".
/// <br/>
/// Snapshots are only meaningful against a document created with <c>GarbageCollection = false</c>
/// (<see cref="YDocOptions.GarbageCollection"/>) - a garbage-collected document may already have thrown away the
/// content of deleted items a snapshot would need to restore. <see cref="RestoreDocument"/> and
/// <see cref="YDocSnapshotExtensions.CreateDocFromSnapshot"/> both enforce this the same way Yjs
/// does, by throwing if <c>originDoc.GarbageCollection</c> is <c>true</c>.
/// </summary>
public sealed partial class Snapshot : IEquatable<Snapshot>
{
    internal readonly DeleteSet DeleteSet;
    internal readonly IDictionary<long, long> StateVector;

    internal Snapshot(DeleteSet ds, IDictionary<long, long> stateMap)
    {
        DeleteSet = ds;
        StateVector = stateMap;
    }

    /// <summary>
    /// Yjs parity: <c>createSnapshot(ds, sm)</c>. Internal (rather than public, like the rest of
    /// this factory-method group) purely because its <c>ds</c> parameter is the internal
    /// <see cref="Ycs.DeleteSet"/> type - callers outside this assembly cannot construct one to
    /// pass in anyway. The existing internal constructor already serves this purpose for in-assembly
    /// callers; this is the Yjs-named alias for parity/documentation.
    /// </summary>
    internal static Snapshot CreateSnapshot(DeleteSet ds, IDictionary<long, long> sm) => new Snapshot(ds, sm);

    /// <summary>
    /// Yjs parity: <c>emptySnapshot</c>.
    /// </summary>
    public static readonly Snapshot Empty = new Snapshot(new DeleteSet(), new Dictionary<long, long>());

    /// <summary>
    /// Yjs' <c>createDocFromSnapshot</c> (<see cref="YDoc.CreateDocFromSnapshot"/>): encodes, as a v2 update, exactly
    /// the structs/deletes described by <paramref name="stateVector"/>/<paramref name="ds"/>,
    /// splitting structs in <paramref name="originDoc"/> as needed so the encoded ranges land on
    /// exact struct boundaries.
    /// </summary>
    internal static byte[] EncodeRestoreUpdateV2(YDoc originDoc, DeleteSet ds, IDictionary<long, long> stateVector)
    {
        var encoder = UpdateEncoderV2.Rent();
        try
        {
            return EncodeRestoreUpdateV2(encoder, originDoc, ds, stateVector);
        }
        finally
        {
            encoder.Return();
        }
    }

    private static byte[] EncodeRestoreUpdateV2(UpdateEncoderV2 encoder, YDoc originDoc, DeleteSet ds, IDictionary<long, long> stateVector)
    {
        originDoc.MaterializeRunExtensions();

        // The snapshot may come from another peer or another document; Yjs fails on it in findIndexSS.
        foreach (var (client, clock) in stateVector)
        {
            if (clock > originDoc.Store.GetState(client))
            {
                throw new YUpdateException($"The snapshot holds client {client} up to clock {clock}, beyond what the document has of it.");
            }
        }

        originDoc.Transact(tr =>
        {
            int size = stateVector.Count(kvp => kvp.Value /* clock */ > 0);
            encoder.RestWriter.WriteVarUint((uint)size);

            // Splitting the structs before writing them to the encoder.
            foreach (var kvp in stateVector)
            {
                var client = kvp.Key;
                var clock = kvp.Value;

                if (clock == 0)
                {
                    continue;
                }

                if (clock < originDoc.Store.GetState(client))
                {
                    tr.Doc.Store.GetItemCleanStart(tr, new ItemId(client, clock));
                }

                var structs = originDoc.Store.Clients[client];
                var lastStructIndex = structs.FindIndex(clock - 1);

                // Write # encoded structs.
                encoder.RestWriter.WriteVarUint((uint)(lastStructIndex + 1));
                encoder.WriteClient(client);

                // First clock written is 0.
                encoder.RestWriter.WriteVarUint(0);

                for (int i = 0; i <= lastStructIndex; i++)
                {
                    structs[i].Write(encoder, 0);
                }
            }

            ds.Write(encoder);
        });

        return encoder.ToArray();
    }

    public bool Equals(Snapshot? other)
    {
        if (other is null)
        {
            return false;
        }

        var ds1 = DeleteSet.Clients;
        var ds2 = other.DeleteSet.Clients;
        var sv1 = StateVector;
        var sv2 = other.StateVector;

        if (sv1.Count != sv2.Count || ds1.Count != ds2.Count)
        {
            return false;
        }

        foreach (var kvp in sv1)
        {
            if (!sv2.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
            {
                return false;
            }
        }

        foreach (var kvp in ds1)
        {
            var client = kvp.Key;
            var dsItems1 = kvp.Value;

            if (!ds2.TryGetValue(client, out var dsItems2))
            {
                return false;
            }

            if (dsItems1.Count != dsItems2.Count)
            {
                return false;
            }

            for (int i = 0; i < dsItems1.Count; i++)
            {
                var dsItem1 = dsItems1[i];
                var dsItem2 = dsItems2[i];
                if (dsItem1.Clock != dsItem2.Clock || dsItem1.Length != dsItem2.Length)
                {
                    return false;
                }
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is Snapshot other && Equals(other);

    public static bool operator ==(Snapshot? left, Snapshot? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(Snapshot? left, Snapshot? right) => !(left == right);

    // Order-independent over the clients, as Equals; order-dependent within a client's ranges.
    // The per-client hashes are summed modulo 2^32 on purpose.
    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var kvp in StateVector)
        {
            hash = unchecked(hash + HashCode.Combine(kvp.Key, kvp.Value));
        }

        foreach (var kvp in DeleteSet.Clients)
        {
            var client = new HashCode();
            client.Add(kvp.Key);
            foreach (var item in kvp.Value)
            {
                client.Add(item.Clock);
                client.Add(item.Length);
            }

            hash = unchecked(hash + client.ToHashCode());
        }

        return hash;
    }

    /// <summary>Encodes the snapshot (Yjs' <c>encodeSnapshot</c>, <c>encodeSnapshotV2</c>).</summary>
    public byte[] Encode(UpdateFormat format = UpdateFormat.V1)
    {
        IDSEncoder encoder = format.IsV2() ? new DSEncoderV2() : new DSEncoderV1();
        DeleteSet.Write(encoder);
        EncodingUtils.WriteStateVector(encoder, StateVector);
        return encoder.ToArray();
    }

    /// <summary>Writes the bytes <see cref="Encode(UpdateFormat)"/> returns into <paramref name="destination"/>; they are built in an array first.</summary>
    public void Encode(IBufferWriter<byte> destination, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Encode(format));
    }

    /// <summary>Decodes a snapshot (Yjs' <c>decodeSnapshot</c>, <c>decodeSnapshotV2</c>); the span is read in place and not kept.</summary>
    /// <exception cref="YUpdateException">The bytes are not a snapshot.</exception>
    public static Snapshot Decode(ReadOnlySpan<byte> bytes, UpdateFormat format = UpdateFormat.V1)
    {
        try
        {
            if (format.IsV2())
            {
                var decoder = new DSDecoderV2(0, bytes.Length);
                return Decode(ref decoder, bytes);
            }

            var decoderV1 = new DSDecoderV1(0, bytes.Length);
            return Decode(ref decoderV1, bytes);
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }
    }

    private static Snapshot Decode<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
        where TDecoder : struct, IDSDecoder
    {
        var ds = DeleteSet.Read(ref decoder, input);
        return new Snapshot(ds, EncodingUtils.ReadStateVector(ref decoder, input));
    }

    /// <summary>
    /// Yjs' <c>snapshotContainsUpdate</c>: whether the update in <paramref name="format"/> holds nothing beyond this
    /// snapshot. The span is read in place and not kept.
    /// </summary>
    public bool ContainsUpdate(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        try
        {
            limits ??= UpdateLimits.Default;
            if (format.IsV2())
            {
                var decoder = new UpdateDecoderV2(update, limits.MaxStructsPerUpdate, limits.MaxDepth, new List<string>());
                return ContainsUpdate(ref decoder, update);
            }

            var decoderV1 = new UpdateDecoderV1(update, limits.MaxStructsPerUpdate, limits.MaxDepth);
            return ContainsUpdate(ref decoderV1, update);
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }
    }

    /// <summary>
    /// Unlike Yjs' version, which reads the struct headers lazily and stops at the first client the
    /// snapshot does not cover, this reads all structs first.
    /// </summary>
    private bool ContainsUpdate<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> update)
        where TDecoder : struct, IUpdateDecoder
    {
        var table = StagingTable.Rent();
        try
        {
            var cursor = EncodingUtils.StructCursor.Begin(ref decoder, update, table, reserve: true);
            while (cursor.NextStaged(ref decoder, update) >= 0)
            {
            }

            for (var client = 0; client < table.ClientCount; client++)
            {
                var (start, count) = table.RangeOf(client);
                if (count == 0)
                {
                    continue;
                }

                var last = table.At(start + count - 1);
                var endClock = last.Clock + last.Length;

                if (!StateVector.TryGetValue(table.ClientAt(client), out var svClock) || svClock < endClock)
                {
                    return false;
                }
            }
        }
        finally
        {
            StagingTable.Return(table);
        }

        var updateDs = DeleteSet.Read(ref decoder, update);
        var merged = new DeleteSet(new List<DeleteSet> { DeleteSet, updateDs });
        return DeleteSetsEqual(DeleteSet, merged);
    }

    /// <summary>
    /// Yjs parity: <c>equalDeleteSets(ds1, ds2)</c> (utils/DeleteSet.js) - not otherwise exposed on
    /// the internal <see cref="Ycs.DeleteSet"/> type, so ported here for
    /// <see cref="ContainsUpdate{TDecoder}(ref TDecoder, ReadOnlySpan{byte})"/>'s use. Structurally the same comparison
    /// <see cref="Equals(Snapshot)"/> already does for its two delete sets.
    /// </summary>
    private static bool DeleteSetsEqual(DeleteSet a, DeleteSet b)
    {
        var ac = a.Clients;
        var bc = b.Clients;

        if (ac.Count != bc.Count)
        {
            return false;
        }

        foreach (var kvp in ac)
        {
            if (!bc.TryGetValue(kvp.Key, out var bItems) || bItems.Count != kvp.Value.Count)
            {
                return false;
            }

            for (int i = 0; i < kvp.Value.Count; i++)
            {
                if (kvp.Value[i].Clock != bItems[i].Clock || kvp.Value[i].Length != bItems[i].Length)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
