// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// Mirrors Yjs 13.6.32's <c>Snapshot</c> (src/utils/Snapshot.js): an immutable pair of a state
/// vector and a delete set that together pin down "the document as of this point in time".
/// <br/>
/// Snapshots are only meaningful against a document created with <c>Gc = false</c>
/// (<see cref="YDocOptions.Gc"/>) - a garbage-collected document may already have thrown away the
/// content of deleted items a snapshot would need to restore. <see cref="RestoreDocument"/> and
/// <see cref="YDocSnapshotExtensions.CreateDocFromSnapshot"/> both enforce this the same way Yjs
/// does, by throwing if <c>originDoc.Gc</c> is <c>true</c>.
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
    /// Yjs parity: <c>equalSnapshots(snap1, snap2)</c>. Null-safe wrapper around
    /// <see cref="Equals(Snapshot)"/> (which requires a non-null receiver).
    /// </summary>
    public static bool EqualSnapshots(Snapshot snap1, Snapshot snap2)
    {
        return ReferenceEquals(snap1, snap2) || (snap1 != null && snap1.Equals(snap2));
    }

    public YDoc RestoreDocument(YDoc originDoc, YDocOptions opts = null)
    {
        if (originDoc.Gc)
        {
            // We should try to restore a GC-ed document, because some of the restored items might have their content deleted.
            throw new InvalidOperationException("Garbage collection must be disabled in the origin document.");
        }

        var update = EncodeRestoreUpdateV2(originDoc, DeleteSet, StateVector);
        var newDoc = new YDoc(opts ?? originDoc.CloneOptionsWithNewGuid());
        newDoc.ApplyUpdateV2(update, transactionOrigin: "snapshot");
        return newDoc;
    }

    /// <summary>
    /// Shared body of Yjs' <c>restoreDocument</c>-shaped logic (used here by
    /// <see cref="RestoreDocument"/>) and <c>createDocFromSnapshot</c> (used by
    /// <see cref="YDocSnapshotExtensions.CreateDocFromSnapshot"/>): encodes, as a v2 update, exactly
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
                    tr.Doc.Store.GetItemCleanStart(tr, new ID(client, clock));
                }

                var structs = originDoc.Store.Clients[client];
                var lastStructIndex = StructStore.FindIndexSS(structs, clock - 1);

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

    public bool Equals(Snapshot other)
    {
        if (other == null)
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

    public byte[] EncodeSnapshotV2()
    {
        var encoder = new DSEncoderV2();
        DeleteSet.Write(encoder);
        EncodingUtils.WriteStateVector(encoder, StateVector);
        return encoder.ToArray();
    }

    /// <summary>
    /// Yjs parity: <c>encodeSnapshot(snapshot)</c> - the original ('v1') snapshot wire format.
    /// </summary>
    public byte[] EncodeSnapshot()
    {
        var encoder = new DSEncoderV1();
        DeleteSet.Write(encoder);
        EncodingUtils.WriteStateVector(encoder, StateVector);
        return encoder.ToArray();
    }

    /// <summary>
    /// Yjs parity: <c>decodeSnapshotV2(buf)</c>.
    /// </summary>
    internal static Snapshot DecodeSnapshotV2(Lib0Reader input)
    {
        var decoder = new DSDecoderV2(input);
        var ds = DeleteSet.Read(decoder);
        var sv = EncodingUtils.ReadStateVector(decoder);
        return new Snapshot(ds, sv);
    }

    public static Snapshot DecodeSnapshotV2(byte[] buf)
    {
        return DecodeSnapshotV2(new Lib0Reader(buf));
    }

    /// <summary>
    /// Yjs parity: <c>decodeSnapshot(buf)</c> - the original ('v1') snapshot wire format.
    /// </summary>
    internal static Snapshot DecodeSnapshot(Lib0Reader input)
    {
        var decoder = new DSDecoderV1(input);
        var ds = DeleteSet.Read(decoder);
        var sv = EncodingUtils.ReadStateVector(decoder);
        return new Snapshot(ds, sv);
    }

    public static Snapshot DecodeSnapshot(byte[] buf)
    {
        return DecodeSnapshot(new Lib0Reader(buf));
    }

    /// <summary>
    /// Yjs parity: <c>snapshotContainsUpdate(snapshot, update)</c> - checks an original ('v1')
    /// update against this snapshot.
    /// </summary>
    public bool SnapshotContainsUpdate(byte[] update, UpdateLimits limits = null) => SnapshotContainsUpdateCore(update, isV1: true, limits);

    /// <summary>
    /// Yjs parity: <c>snapshotContainsUpdateV2(snapshot, update)</c>.
    /// </summary>
    public bool SnapshotContainsUpdateV2(byte[] update, UpdateLimits limits = null) => SnapshotContainsUpdateCore(update, isV1: false, limits);

    /// <summary>
    /// Unlike Yjs' version, which reads the struct headers lazily and stops at the first client the
    /// snapshot does not cover, this reads all structs first.
    /// </summary>
    private bool SnapshotContainsUpdateCore(byte[] update, bool isV1, UpdateLimits limits)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => ContainsUpdate(update, isV1, (limits ?? UpdateLimits.Default).MaxStructsPerUpdate));
    }

    private bool ContainsUpdate(byte[] update, bool isV1, long maxStructs)
    {
        var stream = new Lib0Reader(update);
        IUpdateDecoder decoder = isV1
            ? (IUpdateDecoder)new UpdateDecoderV1(stream, maxStructs)
            : new UpdateDecoderV2(stream, maxStructs);

        var clientStructRefs = EncodingUtils.ReadClientStructRefs(decoder);

        foreach (var kvp in clientStructRefs)
        {
            var structs = kvp.Value;
            if (structs.Count == 0)
            {
                continue;
            }

            var last = structs[structs.Count - 1];
            var endClock = last.Id.Clock + last.Length;

            if (!StateVector.TryGetValue(kvp.Key, out var svClock) || svClock < endClock)
            {
                return false;
            }
        }

        var updateDs = DeleteSet.Read(decoder);
        var merged = new DeleteSet(new List<DeleteSet> { DeleteSet, updateDs });
        return DeleteSetsEqual(DeleteSet, merged);
    }

    /// <summary>
    /// Yjs parity: <c>equalDeleteSets(ds1, ds2)</c> (utils/DeleteSet.js) - not otherwise exposed on
    /// the internal <see cref="Ycs.DeleteSet"/> type, so ported here for
    /// <see cref="SnapshotContainsUpdateCore"/>'s use. Structurally the same comparison
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
