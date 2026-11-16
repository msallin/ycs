// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Text;

namespace Ycs;

/// <summary>
/// One entry of a decoded delete set: <c>[Clock, Clock + Length)</c> is deleted for the owning
/// client. See <see cref="DecodedUpdate.DeleteSet"/>.
/// </summary>
public readonly struct DeletedRange
{
    public DeletedRange(long clock, long length)
    {
        Clock = clock;
        Length = length;
    }

    public long Clock { get; }
    public long Length { get; }
}

/// <summary>
/// The structured view of an update: its structs (in the order they occur in the update, GC
/// and Skip included), plus its delete set. See <see cref="UpdateOperations.DecodeUpdate"/> /
/// <see cref="UpdateOperations.DecodeUpdateV2"/>.
/// </summary>
public sealed class DecodedUpdate
{
    internal DecodedUpdate(StagingTable structs, IReadOnlyDictionary<long, IReadOnlyList<DeletedRange>> deleteSet)
    {
        Staged = structs;
        DeleteSet = deleteSet;
    }

    /// <summary>The structs in the order of the update, held by a table of their own.</summary>
    internal StagingTable Staged { get; }

    /// <summary>One <see cref="DecodedStruct"/> per struct, in the order of the update, Skip and GC included.</summary>
    public IReadOnlyList<DecodedStruct> Structs => _decodedStructs ??= Detach(Staged);

    private static DecodedStruct[] Detach(StagingTable table)
    {
        var result = new DecodedStruct[table.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = StructStaging.Decoded(table, i);
        }

        return result;
    }

    private IReadOnlyList<DecodedStruct> _decodedStructs;

    public IReadOnlyDictionary<long, IReadOnlyList<DeletedRange>> DeleteSet { get; }
}

/// <summary>
/// The per-client clock ranges covered by an update: for every client that contributed at
/// least one struct, the clock of its first struct (<see cref="From"/>) and the clock right
/// after its last struct (<see cref="To"/>). See <see cref="UpdateOperations.ParseUpdateMeta"/>
/// / <see cref="UpdateOperations.ParseUpdateMetaV2"/>.
/// </summary>
public sealed class UpdateMeta
{
    internal UpdateMeta(IReadOnlyDictionary<long, long> from, IReadOnlyDictionary<long, long> to)
    {
        From = from;
        To = to;
    }

    public IReadOnlyDictionary<long, long> From { get; }

    public IReadOnlyDictionary<long, long> To { get; }
}

/// <summary>
/// Utilities for manipulating already-encoded update byte streams, without applying them to a
/// live <see cref="YDoc"/>: merging, diffing against a state vector, extracting a state vector
/// or clock-range metadata, converting between the v1 and v2 wire formats, and describing an
/// update for debugging.
/// <br/>
/// Ported from Yjs' <c>src/utils/updates.js</c> (Yjs 13.6.32). Every one of these builds on
/// <see cref="LazyStructReader"/> / <see cref="LazyStructWriter"/>, which stream structs
/// (including 'Skip' placeholders for clock ranges we don't have data for) in and out of an
/// update without needing a document.
/// <br/>
/// Every function takes an optional <see cref="UpdateLimits"/> (<see cref="UpdateLimits.Default"/>
/// when null) whose <see cref="UpdateLimits.MaxStructsPerUpdate"/> bounds each input. Merging, diffing
/// and the state vector stream; converting a v2 update to v1, decoding and describing it produce
/// output in proportion to its structs, which a v2 update describes in a few bytes.
/// </summary>
public static class UpdateOperations
{
    private static long MaxStructs(UpdateLimits limits) => (limits ?? UpdateLimits.Default).MaxStructsPerUpdate;

    /// <summary>A reader of <paramref name="update"/> whose values may be nested as deeply as <paramref name="limits"/> allow.</summary>
    private static Lib0Reader Reader(byte[] update, UpdateLimits limits) => new(update) { MaxDepth = (limits ?? UpdateLimits.Default).MaxDepth };

    #region MergeUpdates

    /// <summary>
    /// Merges several v1-format updates into one. The result is associative and idempotent:
    /// merging is safe to redo, and to do in any grouping, and it can be fed updates that were
    /// themselves produced by an earlier merge (which may still contain 'Skip' gaps for data
    /// that update didn't have). As in Yjs, not where one update holds a GC struct and another an item
    /// for the same clock: the struct kept then depends on the order and the grouping (Yjs under V8's sort).
    /// </summary>
    /// <param name="updates">The updates.</param>
    /// <param name="limits">Bounds each input (<see cref="UpdateLimits.MaxStructsPerUpdate"/>); <see cref="UpdateLimits.Default"/> when null.</param>
    public static byte[] MergeUpdates(IList<byte[]> updates, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(updates);

        return YUpdateException.Decode(() => MergeUpdatesCore(updates, v2: false, MaxStructs(limits), (limits ?? UpdateLimits.Default).MaxDepth, out _));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="MergeUpdates"/>.
    /// </summary>
    /// <inheritdoc cref="MergeUpdates"/>
    public static byte[] MergeUpdatesV2(IList<byte[]> updates, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(updates);

        return YUpdateException.Decode(() => MergeUpdatesCore(updates, v2: true, MaxStructs(limits), (limits ?? UpdateLimits.Default).MaxDepth, out _));
    }

    /// <summary>
    /// Merges v2 updates this library wrote (pending structs), any number of them: in groups of a few,
    /// because <see cref="MergeUpdatesCore"/> re-sorts its inputs for every struct it writes.
    /// <paramref name="structs"/> is how many structs the result holds (unchanged for a single update).
    /// </summary>
    internal static byte[] MergeUpdatesTrusted(List<byte[]> updates, ref long structs)
    {
        const int GroupSize = 8;
        while (updates.Count > 1)
        {
            var next = new List<byte[]>((updates.Count + GroupSize - 1) / GroupSize);
            for (var i = 0; i < updates.Count; i += GroupSize)
            {
                var group = updates.GetRange(i, Math.Min(GroupSize, updates.Count - i));
                next.Add(MergeUpdatesCore(group, v2: true, maxStructs: long.MaxValue, maxDepth: int.MaxValue, out var written));

                // The last round merges a single group, and that is the result.
                structs = written >= 0 ? written : structs;
            }

            updates = next;
        }

        return updates[0];
    }

    /// <param name="written">How many structs the result holds; -1 when the single update is returned as it is.</param>
    private static byte[] MergeUpdatesCore(IList<byte[]> updates, bool v2, long maxStructs, int maxDepth, out long written)
    {
        written = -1;
        if (updates.Count == 1)
        {
            return updates[0];
        }

        var updateDecoders = new List<IUpdateDecoder>(updates.Count);
        var lazyStructDecoders = new List<LazyStructReader>(updates.Count);
        var tables = new List<StagingTable>(updates.Count + 1);
        IUpdateEncoder updateEncoder = v2 ? UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
        try
        {
            foreach (var update in updates)
            {
                var reader = new Lib0Reader(update) { MaxDepth = maxDepth };
                var decoder = v2 ? (IUpdateDecoder)new UpdateDecoderV2(reader, maxStructs) : new UpdateDecoderV1(reader, maxStructs);
                updateDecoders.Add(decoder);
                var table = StagingTable.Rent();
                tables.Add(table);
                lazyStructDecoders.Add(new LazyStructReader(decoder, filterSkips: true, table));
            }

            var pending = StagingTable.Rent();
            tables.Add(pending);
            var writer = new LazyStructWriter(updateEncoder);
            MergeStructs(lazyStructDecoders, writer, pending);
            written = writer.TotalWritten;

            // Every decoder above was drained (Curr < 0) before the loop could exit, so
            // each one's stream is now positioned right at the start of its delete set.
            var deleteSets = new List<DeleteSet>(updateDecoders.Count);
            foreach (var decoder in updateDecoders)
            {
                deleteSets.Add(DeleteSet.Read(decoder));
            }

            new DeleteSet(deleteSets).Write(updateEncoder);
            return updateEncoder.ToArray();
        }
        finally
        {
            ReturnEncoder(updateEncoder);
            foreach (var table in tables)
            {
                StagingTable.Return(table);
            }
        }
    }

    private static void ReturnEncoder(IUpdateEncoder encoder)
    {
        if (encoder is UpdateEncoderV2 v2)
        {
            v2.Return();
        }
        else
        {
            ((UpdateEncoderV1)encoder).Return();
        }
    }

    /// <summary>
    /// The struct being written by <see cref="MergeStructs"/>, held in a one-struct table of its own: the readers hold
    /// the current struct only, and it can grow (a GC), be trimmed or extended (a Skip) before it is written.
    /// </summary>
    private sealed class PendingStruct(LazyStructWriter writer, StagingTable table)
    {
        public bool Has { get; private set; }
        public long Client { get; private set; }
        public long Clock { get; private set; }
        public int Length { get; private set; }
        public StagedKind Kind { get; private set; }
        public long End => Clock + Length;

        /// <summary>Takes <paramref name="reader"/>'s current struct, from <paramref name="cut"/> clocks in (Yjs' <c>splice</c>).</summary>
        public void Set(LazyStructReader reader, int cut)
        {
            table.Reset();
            StructStaging.CopyFrom(table, reader.Table, reader.Curr, cut);
            Load();
        }

        public void SetSkip(long client, long clock, int length)
        {
            table.Reset();
            table.BeginClient(client, clock);
            table.Add(StagedKind.Skip, length);
            Load();
        }

        public void SetLength(int length)
        {
            table.SetLength(0, length);
            Length = length;
        }

        public void Write() => LazyStructWriter.WriteStruct(writer, table, 0, 0);

        private void Load()
        {
            ref readonly var s = ref table.At(0);
            Has = true;
            Client = table.ClientAt(s.Client);
            Clock = s.Clock;
            Length = s.Length;
            Kind = s.Kind;
        }
    }

    // Item, GC and Skip merge only with their own kind (the classes of the structs Yjs merges).
    private static bool SameShape(StagedKind a, StagedKind b) =>
        a == b || a != StagedKind.GC && a != StagedKind.Skip && b != StagedKind.GC && b != StagedKind.Skip;

    private static void MergeStructs(List<LazyStructReader> lazyStructDecoders, LazyStructWriter lazyStructEncoder, StagingTable pendingTable)
    {
        var currWrite = new PendingStruct(lazyStructEncoder, pendingTable);

        // Note: we need to ensure that all lazyStructDecoders are fully consumed.
        // Note: should merge document updates whenever possible - even from different updates.
        // Note: should handle that some operations cannot be applied yet.
        while (true)
        {
            // Write higher clients first - sort by clientID & clock and remove decoders without content.
            lazyStructDecoders.RemoveAll(static dec => dec.Curr < 0);
            StableSort.Sort(lazyStructDecoders, static (dec1, dec2) =>
            {
                if (dec1.Client == dec2.Client)
                {
                    var clockCompare = dec1.Clock.CompareTo(dec2.Clock);
                    if (clockCompare == 0)
                    {
                        // Remove references to Skip since the struct decoders must filter Skips anyway.
                        return SameShape(dec1.Kind, dec2.Kind) ? 0 : (dec1.Kind == StagedKind.Skip ? 1 : -1);
                    }

                    return clockCompare;
                }

                return dec2.Client.CompareTo(dec1.Client);
            });

            if (lazyStructDecoders.Count == 0)
            {
                break;
            }

            var currDecoder = lazyStructDecoders[0];

            // Write from currDecoder until the next operation is from another client or if
            // filler-struct, then we need to reorder the decoders and find the next operation to write.
            var firstClient = currDecoder.Client;

            if (currWrite.Has)
            {
                var iterated = false;

                // Iterate until we find something that we haven't written already.
                // Remember: first the high client-ids are written.
                while (currDecoder.Curr >= 0 &&
                       currDecoder.Clock + currDecoder.Length <= currWrite.End &&
                       currDecoder.Client >= currWrite.Client)
                {
                    currDecoder.Next();
                    iterated = true;
                }

                if (currDecoder.Curr < 0 || // Current decoder is empty.
                    currDecoder.Client != firstClient || // Check whether there is another decoder that has updates from 'firstClient'.
                    (iterated && currDecoder.Clock > currWrite.End)) // The above while loop was used and we are potentially missing updates.
                {
                    continue;
                }

                if (firstClient != currWrite.Client)
                {
                    currWrite.Write();
                    currWrite.Set(currDecoder, 0);
                    currDecoder.Next();
                }
                else
                {
                    if (currWrite.End < currDecoder.Clock)
                    {
                        if (currWrite.Kind == StagedKind.Skip)
                        {
                            // Extend existing skip.
                            currWrite.SetLength((int)(currDecoder.Clock + currDecoder.Length - currWrite.Clock));
                        }
                        else
                        {
                            currWrite.Write();
                            var gapLength = currDecoder.Clock - currWrite.Clock - currWrite.Length;
                            currWrite.SetSkip(firstClient, currWrite.Clock + currWrite.Length, (int)gapLength);
                        }
                    }
                    else
                    {
                        var diff = currWrite.End - currDecoder.Clock;
                        var cut = 0;
                        if (diff > 0)
                        {
                            if (currWrite.Kind == StagedKind.Skip)
                            {
                                // Prefer to slice Skip because the other struct might contain more information.
                                currWrite.SetLength((int)(currWrite.Length - diff));
                            }
                            else
                            {
                                cut = (int)diff;
                            }
                        }

                        // Only a GC extends a GC; a decoded item has no neighbours (Yjs' Item#mergeWith needs this.right === right).
                        if (currWrite.Kind == StagedKind.GC && currDecoder.Kind == StagedKind.GC)
                        {
                            currWrite.SetLength(currWrite.Length + (currDecoder.Length - cut));
                        }
                        else
                        {
                            currWrite.Write();
                            currWrite.Set(currDecoder, cut);
                            currDecoder.Next();
                        }
                    }
                }
            }
            else
            {
                currWrite.Set(currDecoder, 0);
                currDecoder.Next();
            }

            for (;
                 currDecoder.Curr >= 0 &&
                 currDecoder.Client == firstClient &&
                 currDecoder.Clock == currWrite.End &&
                 currDecoder.Kind != StagedKind.Skip;
                 currDecoder.Next())
            {
                currWrite.Write();
                currWrite.Set(currDecoder, 0);
            }
        }

        if (currWrite.Has)
        {
            currWrite.Write();
        }

        LazyStructWriter.Finish(lazyStructEncoder);
    }

    #endregion

    #region DiffUpdate

    /// <summary>
    /// Extracts the part of a v1-format <paramref name="update"/> that is missing relative to
    /// <paramref name="stateVector"/> (an encoded state vector, e.g. from
    /// <see cref="EncodeStateVectorFromUpdate"/> or <see cref="YDoc.EncodeStateVectorV1"/>).
    /// </summary>
    public static byte[] DiffUpdate(byte[] update, byte[] stateVector, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(stateVector);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV1.Rent();
            try
            {
                DiffUpdateCore(new UpdateDecoderV1(Reader(update, limits), MaxStructs(limits)), stateVector, encoder);
                return encoder.ToArray();
            }
            finally
            {
                encoder.Return();
            }
        });
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="DiffUpdate"/>.
    /// </summary>
    public static byte[] DiffUpdateV2(byte[] update, byte[] stateVector, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(stateVector);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV2.Rent();
            try
            {
                DiffUpdateCore(new UpdateDecoderV2(Reader(update, limits), MaxStructs(limits)), stateVector, encoder);
                return encoder.ToArray();
            }
            finally
            {
                encoder.Return();
            }
        });
    }

    private static void DiffUpdateCore(IUpdateDecoder decoder, byte[] stateVector, IUpdateEncoder encoder) =>
        DiffUpdateCore(decoder, EncodingUtils.DecodeStateVector(new Lib0Reader(stateVector)), encoder);

    /// <summary>
    /// What <see cref="EncodingUtils.EncodeStateAsUpdate(StructStore, ReadOnlySpan{byte}, bool)"/>
    /// adds while something is pending, as Yjs 13.6's <c>encodeStateAsUpdateV2</c>: the state, the
    /// pending deletions and the pending structs beyond <paramref name="targetStateVector"/>, merged.
    /// </summary>
    internal static byte[] MergeWithPending(byte[] state, StructStore store, IDictionary<long, long> targetStateVector, bool v2)
    {
        var updates = new List<byte[]>(3) { state };
        if (store.PendingDs.Count != 0)
        {
            var encoder = v2 ? (IUpdateEncoder)UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
            try
            {
                encoder.RestWriter.WriteVarUint(0);
                store.PendingDs.ToDeleteSet().Write(encoder);
                updates.Add(encoder.ToArray());
            }
            finally
            {
                EncodingUtils.ReturnEncoder(encoder);
            }
        }

        if (store.Pending != null)
        {
            var encoder = v2 ? (IUpdateEncoder)UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
            try
            {
                var pending = store.Pending.Merge();
                // Its values were read under the limits of the updates that brought them.
                DiffUpdateCore(new UpdateDecoderV2(new Lib0Reader(pending) { MaxDepth = int.MaxValue }, store.Pending.Structs), targetStateVector, encoder);
                updates.Add(encoder.ToArray());
            }
            finally
            {
                EncodingUtils.ReturnEncoder(encoder);
            }
        }

        return MergeUpdatesCore(updates, v2, maxStructs: long.MaxValue, maxDepth: int.MaxValue, out _);
    }

    private static void DiffUpdateCore(IUpdateDecoder decoder, IDictionary<long, long> state, IUpdateEncoder encoder)
    {
        var table = StagingTable.Rent();
        try
        {
            var lazyStructWriter = new LazyStructWriter(encoder);
            var reader = new LazyStructReader(decoder, filterSkips: false, table);

            while (reader.Curr >= 0)
            {
                var currClient = reader.Client;

                if (reader.Kind == StagedKind.Skip)
                {
                    // The first written struct shouldn't be a Skip.
                    reader.Next();
                    continue;
                }

                state.TryGetValue(currClient, out var svClock);

                if (reader.Clock + reader.Length > svClock)
                {
                    LazyStructWriter.WriteStruct(lazyStructWriter, table, reader.Curr, (int)Math.Max(svClock - reader.Clock, 0));
                    reader.Next();

                    while (reader.Curr >= 0 && reader.Client == currClient)
                    {
                        LazyStructWriter.WriteStruct(lazyStructWriter, table, reader.Curr, 0);
                        reader.Next();
                    }
                }
                else
                {
                    // Read until something new comes up.
                    while (reader.Curr >= 0 && reader.Client == currClient && reader.Clock + reader.Length <= svClock)
                    {
                        reader.Next();
                    }
                }
            }

            LazyStructWriter.Finish(lazyStructWriter);
        }
        finally
        {
            StagingTable.Return(table);
        }

        var ds = DeleteSet.Read(decoder);
        ds.Write(encoder);
    }

    #endregion

    #region EncodeStateVectorFromUpdate

    /// <summary>
    /// Computes the state vector implied by a v1-format update, without applying it to a
    /// document. Equivalent to (but far cheaper than) applying the update to an empty document
    /// and calling <see cref="YDoc.EncodeStateVectorV1"/>.
    /// </summary>
    public static byte[] EncodeStateVectorFromUpdate(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => EncodeStateVectorFromUpdateCore(new UpdateDecoderV1(Reader(update, limits), MaxStructs(limits))));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="EncodeStateVectorFromUpdate"/>.
    /// </summary>
    public static byte[] EncodeStateVectorFromUpdateV2(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => EncodeStateVectorFromUpdateCore(new UpdateDecoderV2(Reader(update, limits), MaxStructs(limits))));
    }

    private static byte[] EncodeStateVectorFromUpdateCore(IUpdateDecoder decoder)
    {
        var pairs = new List<(long Client, long Clock)>();
        var table = StagingTable.Rent();
        try
        {
            var reader = new LazyStructReader(decoder, filterSkips: false, table);

            if (reader.Curr >= 0)
            {
                var currClient = reader.Client;
                var stopCounting = reader.Clock != 0; // Must start at 0.
                var currClock = stopCounting ? 0 : reader.Clock + reader.Length;

                for (; reader.Curr >= 0; reader.Next())
                {
                    if (currClient != reader.Client)
                    {
                        if (currClock != 0)
                        {
                            // We found a new client - remember what we have.
                            pairs.Add((currClient, currClock));
                        }

                        currClient = reader.Client;
                        currClock = 0;
                        stopCounting = reader.Clock != 0;
                    }

                    // We ignore Skips.
                    if (reader.Kind == StagedKind.Skip)
                    {
                        stopCounting = true;
                    }

                    if (!stopCounting)
                    {
                        currClock = reader.Clock + reader.Length;
                    }
                }

                // Remember what we have.
                if (currClock != 0)
                {
                    pairs.Add((currClient, currClock));
                }
            }
        }
        finally
        {
            StagingTable.Return(table);
        }

        var writer = new Lib0Writer();
        writer.WriteVarUint((uint)pairs.Count);
        foreach (var (client, clock) in pairs)
        {
            writer.WriteVarUint((ulong)client);
            writer.WriteVarUint((ulong)clock);
        }

        return writer.ToArray();
    }

    #endregion

    #region ParseUpdateMeta

    /// <summary>
    /// Returns, for every client that contributed at least one struct to a v1-format update,
    /// the clock range it covers.
    /// </summary>
    public static UpdateMeta ParseUpdateMeta(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => ParseUpdateMetaCore(new UpdateDecoderV1(Reader(update, limits), MaxStructs(limits))));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="ParseUpdateMeta"/>.
    /// </summary>
    public static UpdateMeta ParseUpdateMetaV2(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => ParseUpdateMetaCore(new UpdateDecoderV2(Reader(update, limits), MaxStructs(limits))));
    }

    private static UpdateMeta ParseUpdateMetaCore(IUpdateDecoder decoder)
    {
        var from = new Dictionary<long, long>();
        var to = new Dictionary<long, long>();

        var table = StagingTable.Rent();
        try
        {
            var reader = new LazyStructReader(decoder, filterSkips: false, table);

            if (reader.Curr >= 0)
            {
                var currClient = reader.Client;
                var currClock = reader.Clock;

                // Write the beginning to 'from'.
                from[currClient] = currClock;

                for (; reader.Curr >= 0; reader.Next())
                {
                    if (currClient != reader.Client)
                    {
                        // We found a new client.
                        to[currClient] = currClock;
                        from[reader.Client] = reader.Clock;
                        currClient = reader.Client;
                    }

                    currClock = reader.Clock + reader.Length;
                }

                to[currClient] = currClock;
            }
        }
        finally
        {
            StagingTable.Return(table);
        }

        return new UpdateMeta(from, to);
    }

    #endregion

    #region ConvertUpdateFormat

    /// <summary>
    /// Re-encodes a v1-format update in the v2 format. The two formats carry the exact same
    /// information; this is a pure re-encoding, but for a delete set whose ranges of a client are not
    /// in order or overlap (valid v1, which the v2 format cannot hold): that client's ranges are sorted
    /// and the overlapping ones merged, which deletes the same. (Yjs writes negative clock differences,
    /// which its own decoder reads as other ranges.)
    /// </summary>
    public static byte[] ConvertUpdateFormatV1ToV2(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV2.Rent();
            try
            {
                ConvertUpdateFormatCore(new UpdateDecoderV1(Reader(update, limits), MaxStructs(limits)), encoder);
                return encoder.ToArray();
            }
            finally
            {
                encoder.Return();
            }
        });
    }

    /// <summary>
    /// Re-encodes a v2-format update in the v1 format.
    /// </summary>
    public static byte[] ConvertUpdateFormatV2ToV1(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV1.Rent();
            try
            {
                ConvertUpdateFormatCore(new UpdateDecoderV2(Reader(update, limits), MaxStructs(limits)), encoder);
                return encoder.ToArray();
            }
            finally
            {
                encoder.Return();
            }
        });
    }

    private static void ConvertUpdateFormatCore(IUpdateDecoder decoder, IUpdateEncoder encoder)
    {
        var table = StagingTable.Rent();
        try
        {
            var reader = new LazyStructReader(decoder, filterSkips: false, table);
            var writer = new LazyStructWriter(encoder);

            for (; reader.Curr >= 0; reader.Next())
            {
                LazyStructWriter.WriteStruct(writer, table, reader.Curr, 0);
            }

            LazyStructWriter.Finish(writer);
        }
        finally
        {
            StagingTable.Return(table);
        }

        var ds = DeleteSet.Read(decoder);
        OrderRanges(ds);
        ds.Write(encoder);
    }

    /// <summary>
    /// Sorts the ranges of every client whose ranges are out of order or overlap, and merges the overlapping ones;
    /// adjacent ranges stay apart, as Yjs writes them. A v2 delete set (each range at or after the end of the one
    /// before) is unchanged.
    /// </summary>
    private static void OrderRanges(DeleteSet ds)
    {
        foreach (var ranges in ds.Clients.Values)
        {
            var ordered = true;
            for (var i = 1; i < ranges.Count && ordered; i++)
            {
                ordered = ranges[i].Clock >= ranges[i - 1].Clock + ranges[i - 1].Length;
            }

            if (ordered)
            {
                continue;
            }

            ranges.Sort((a, b) => a.Clock.CompareTo(b.Clock));
            var merged = 0;
            for (var i = 1; i < ranges.Count; i++)
            {
                var last = ranges[merged];
                var end = last.Clock + last.Length;
                if (ranges[i].Clock < end)
                {
                    ranges[merged] = new DeleteSet.DeleteItem(last.Clock, Math.Max(end, ranges[i].Clock + ranges[i].Length) - last.Clock);
                }
                else
                {
                    ranges[++merged] = ranges[i];
                }
            }

            ranges.RemoveRange(merged + 1, ranges.Count - merged - 1);
        }
    }

    #endregion

    #region DecodeUpdate

    /// <summary>
    /// Decodes a v1-format update into its structs (in order) and its delete set, without
    /// applying it to a document.
    /// </summary>
    public static DecodedUpdate DecodeUpdate(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => DecodeUpdateCore(new UpdateDecoderV1(Reader(update, limits), MaxStructs(limits))));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="DecodeUpdate"/>.
    /// </summary>
    public static DecodedUpdate DecodeUpdateV2(byte[] update, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => DecodeUpdateCore(new UpdateDecoderV2(Reader(update, limits), MaxStructs(limits))));
    }

    private static DecodedUpdate DecodeUpdateCore(IUpdateDecoder decoder)
    {
        // The table is the result's: it keeps every struct and is not returned.
        var structs = new StagingTable();
        var cursor = new EncodingUtils.StructCursor(decoder, staging: structs);
        while (cursor.NextStaged() >= 0)
        {
        }

        var ds = DeleteSet.Read(decoder);
        var deleteSet = new Dictionary<long, IReadOnlyList<DeletedRange>>();

        foreach (var kvp in ds.Clients)
        {
            deleteSet[kvp.Key] = kvp.Value.Select(item => new DeletedRange(item.Clock, item.Length)).ToList();
        }

        return new DecodedUpdate(structs, deleteSet);
    }

    #endregion

    #region Describe / log

    /// <summary>
    /// Describes a v1-format update (its structs and its delete set) as a human-readable
    /// string, for debugging. Unlike Yjs' <c>logUpdate</c>, this does not attempt to reproduce
    /// browser/Node console formatting - it just returns a plain string.
    /// </summary>
    public static string DescribeUpdate(byte[] update, UpdateLimits limits = null)
    {
        return DescribeDecodedUpdate(DecodeUpdate(update, limits));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="DescribeUpdate"/>.
    /// </summary>
    public static string DescribeUpdateV2(byte[] update, UpdateLimits limits = null)
    {
        return DescribeDecodedUpdate(DecodeUpdateV2(update, limits));
    }

    /// <summary>
    /// Writes <see cref="DescribeUpdate"/>'s output to <see cref="Console"/>.
    /// </summary>
    public static void LogUpdate(byte[] update)
    {
        Console.WriteLine(DescribeUpdate(update));
    }

    /// <summary>
    /// Writes <see cref="DescribeUpdateV2"/>'s output to <see cref="Console"/>.
    /// </summary>
    public static void LogUpdateV2(byte[] update)
    {
        Console.WriteLine(DescribeUpdateV2(update));
    }

    private static string DescribeDecodedUpdate(DecodedUpdate decoded)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Structs ({decoded.Staged.Count}):");
        for (var i = 0; i < decoded.Staged.Count; i++)
        {
            sb.Append("  ").AppendLine(DescribeStruct(decoded.Staged, i));
        }

        sb.AppendLine("DeleteSet:");
        foreach (var kvp in decoded.DeleteSet)
        {
            foreach (var range in kvp.Value)
            {
                sb.Append("  ").AppendLine($"client={kvp.Key} clock={range.Clock} length={range.Length}");
            }
        }

        return sb.ToString();
    }

    private static string DescribeStruct(StagingTable table, int i)
    {
        var id = table.IdOf(i);
        var length = table.At(i).Length;
        var kind = table.At(i).Kind;
        return kind switch
        {
            StagedKind.Skip => $"Skip(client={id.Client}, clock={id.Clock}, length={length})",
            StagedKind.GC => $"GC(client={id.Client}, clock={id.Clock}, length={length})",

            // A decoded item is never deleted: only a document's transaction marks one.
            _ => $"Item(client={id.Client}, clock={id.Clock}, length={length}, deleted=False, content=Content{kind})",
        };
    }

    #endregion
}
