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
    internal DecodedUpdate(IReadOnlyList<AbstractStruct> structs, IReadOnlyDictionary<long, IReadOnlyList<DeletedRange>> deleteSet)
    {
        Structs = structs;
        DeleteSet = deleteSet;
    }

    public IReadOnlyList<AbstractStruct> Structs { get; }

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
/// </summary>
public static class UpdateOperations
{
    /// <summary>
    /// The most structs a v2 update given to these functions may hold (never fewer than one per byte
    /// of the update), process-wide: the v2 format can describe any number of structs in a few bytes,
    /// and merging, diffing or converting them takes time and memory per struct. An update beyond it
    /// is rejected with a <see cref="YUpdateException"/>. Default 2,097,152, as
    /// <see cref="YDocOptions.MaxStructsPerUpdate"/>; Yjs has no limit.
    /// </summary>
    public static long MaxStructsPerUpdate
    {
        get => Interlocked.Read(ref _maxStructsPerUpdate);
        set => Interlocked.Exchange(ref _maxStructsPerUpdate, Limits.CheckMaxStructs(value));
    }

    private static long _maxStructsPerUpdate = Limits.DefaultMaxStructsPerUpdate;

    private sealed class WriteState
    {
        public AbstractStruct Struct;
        public int Offset;
    }

    #region MergeUpdates

    /// <summary>
    /// Merges several v1-format updates into one. The result is associative and idempotent:
    /// merging is safe to redo, and to do in any grouping, and it can be fed updates that were
    /// themselves produced by an earlier merge (which may still contain 'Skip' gaps for data
    /// that update didn't have).
    /// </summary>
    public static byte[] MergeUpdates(IList<byte[]> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        return YUpdateException.Decode(() => MergeUpdatesCore(updates, v2: false, MaxStructsPerUpdate));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="MergeUpdates"/>.
    /// </summary>
    public static byte[] MergeUpdatesV2(IList<byte[]> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        return YUpdateException.Decode(() => MergeUpdatesCore(updates, v2: true, MaxStructsPerUpdate));
    }

    /// <summary>
    /// Merges v2 updates this library wrote (pending structs), any number of them: in groups of a few,
    /// because <see cref="MergeUpdatesCore"/> re-sorts its inputs for every struct it writes.
    /// </summary>
    internal static byte[] MergeUpdatesTrusted(List<byte[]> updates)
    {
        const int GroupSize = 8;
        while (updates.Count > 1)
        {
            var next = new List<byte[]>((updates.Count + GroupSize - 1) / GroupSize);
            for (var i = 0; i < updates.Count; i += GroupSize)
            {
                var group = updates.GetRange(i, Math.Min(GroupSize, updates.Count - i));
                next.Add(MergeUpdatesCore(group, v2: true, maxStructs: long.MaxValue));
            }

            updates = next;
        }

        return updates[0];
    }

    private static byte[] MergeUpdatesCore(IList<byte[]> updates, bool v2, long maxStructs)
    {
        if (updates.Count == 1)
        {
            return updates[0];
        }

        var updateDecoders = new List<IUpdateDecoder>(updates.Count);
        var lazyStructDecoders = new List<LazyStructReader>(updates.Count);
        foreach (var update in updates)
        {
            var decoder = v2 ? (IUpdateDecoder)new UpdateDecoderV2(new Lib0Reader(update), maxStructs) : new UpdateDecoderV1(new Lib0Reader(update));
            updateDecoders.Add(decoder);
            lazyStructDecoders.Add(new LazyStructReader(decoder, filterSkips: true));
        }

        IUpdateEncoder updateEncoder = v2 ? UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
        try
        {
            MergeStructs(lazyStructDecoders, new LazyStructWriter(updateEncoder));

            // Every decoder above was drained (Curr == null) before the loop could exit, so
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

    private static void MergeStructs(List<LazyStructReader> lazyStructDecoders, LazyStructWriter lazyStructEncoder)
    {
        WriteState currWrite = null;

        // Note: we need to ensure that all lazyStructDecoders are fully consumed.
        // Note: should merge document updates whenever possible - even from different updates.
        // Note: should handle that some operations cannot be applied yet.
        while (true)
        {
            // Write higher clients first - sort by clientID & clock and remove decoders without content.
            lazyStructDecoders.RemoveAll(static dec => dec.Curr == null);
            StableSort.Sort(lazyStructDecoders, static (dec1, dec2) =>
            {
                if (dec1.Curr.Id.Client == dec2.Curr.Id.Client)
                {
                    var clockCompare = dec1.Curr.Id.Clock.CompareTo(dec2.Curr.Id.Clock);
                    if (clockCompare == 0)
                    {
                        // Remove references to Skip since the struct decoders must filter Skips anyway.
                        return dec1.Curr.GetType() == dec2.Curr.GetType() ? 0 : (dec1.Curr is Skip ? 1 : -1);
                    }

                    return clockCompare;
                }

                return dec2.Curr.Id.Client.CompareTo(dec1.Curr.Id.Client);
            });

            if (lazyStructDecoders.Count == 0)
            {
                break;
            }

            var currDecoder = lazyStructDecoders[0];

            // Write from currDecoder until the next operation is from another client or if
            // filler-struct, then we need to reorder the decoders and find the next operation to write.
            var firstClient = currDecoder.Curr.Id.Client;

            if (currWrite != null)
            {
                var curr = currDecoder.Curr;
                var iterated = false;

                // Iterate until we find something that we haven't written already.
                // Remember: first the high client-ids are written.
                while (curr != null &&
                       curr.Id.Clock + curr.Length <= currWrite.Struct.Id.Clock + currWrite.Struct.Length &&
                       curr.Id.Client >= currWrite.Struct.Id.Client)
                {
                    curr = currDecoder.Next();
                    iterated = true;
                }

                if (curr == null || // Current decoder is empty.
                    curr.Id.Client != firstClient || // Check whether there is another decoder that has updates from 'firstClient'.
                    (iterated && curr.Id.Clock > currWrite.Struct.Id.Clock + currWrite.Struct.Length)) // The above while loop was used and we are potentially missing updates.
                {
                    continue;
                }

                if (firstClient != currWrite.Struct.Id.Client)
                {
                    LazyStructWriter.WriteStruct(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                    currWrite = new WriteState { Struct = curr, Offset = 0 };
                    currDecoder.Next();
                }
                else
                {
                    if (currWrite.Struct.Id.Clock + currWrite.Struct.Length < curr.Id.Clock)
                    {
                        if (currWrite.Struct is Skip currSkip)
                        {
                            // Extend existing skip.
                            currSkip.SetLength((int)(curr.Id.Clock + curr.Length - currWrite.Struct.Id.Clock));
                        }
                        else
                        {
                            LazyStructWriter.WriteStruct(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                            var gapLength = curr.Id.Clock - currWrite.Struct.Id.Clock - currWrite.Struct.Length;
                            var skip = new Skip(new ID(firstClient, currWrite.Struct.Id.Clock + currWrite.Struct.Length), (int)gapLength);
                            currWrite = new WriteState { Struct = skip, Offset = 0 };
                        }
                    }
                    else
                    {
                        var diff = currWrite.Struct.Id.Clock + currWrite.Struct.Length - curr.Id.Clock;
                        if (diff > 0)
                        {
                            if (currWrite.Struct is Skip skipToTrim)
                            {
                                // Prefer to slice Skip because the other struct might contain more information.
                                skipToTrim.SetLength((int)(skipToTrim.Length - diff));
                            }
                            else
                            {
                                curr = SliceStruct(curr, (int)diff);
                            }
                        }

                        if (!currWrite.Struct.MergeWith(curr))
                        {
                            LazyStructWriter.WriteStruct(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                            currWrite = new WriteState { Struct = curr, Offset = 0 };
                            currDecoder.Next();
                        }
                    }
                }
            }
            else
            {
                currWrite = new WriteState { Struct = currDecoder.Curr, Offset = 0 };
                currDecoder.Next();
            }

            for (var next = currDecoder.Curr;
                 next != null &&
                 next.Id.Client == firstClient &&
                 next.Id.Clock == currWrite.Struct.Id.Clock + currWrite.Struct.Length &&
                 !(next is Skip);
                 next = currDecoder.Next())
            {
                LazyStructWriter.WriteStruct(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
                currWrite = new WriteState { Struct = next, Offset = 0 };
            }
        }

        if (currWrite != null)
        {
            LazyStructWriter.WriteStruct(lazyStructEncoder, currWrite.Struct, currWrite.Offset);
        }

        LazyStructWriter.Finish(lazyStructEncoder);
    }

    /// <summary>
    /// Slices any kind of struct and retrieves the right-hand part starting <paramref name="diff"/>
    /// clock ticks in. No side effects - only used by the lazy merge/diff algorithms above.
    /// </summary>
    private static AbstractStruct SliceStruct(AbstractStruct left, int diff)
    {
        switch (left)
        {
            case GC gc:
                return new GC(new ID(gc.Id.Client, gc.Id.Clock + diff), gc.Length - diff);
            case Skip skip:
                return new Skip(new ID(skip.Id.Client, skip.Id.Clock + diff), skip.Length - diff);
            case Item leftItem:
                var client = leftItem.Id.Client;
                var clock = leftItem.Id.Clock;
                return new Item(
                    new ID(client, clock + diff),
                    null,
                    new ID(client, clock + diff - 1),
                    null,
                    leftItem.RightOrigin,
                    leftItem.Parent,
                    leftItem.ParentSub,
                    leftItem.Content.Splice(diff));
            default:
                throw new InvalidOperationException($"Unexpected struct type: {left.GetType()}");
        }
    }

    #endregion

    #region DiffUpdate

    /// <summary>
    /// Extracts the part of a v1-format <paramref name="update"/> that is missing relative to
    /// <paramref name="stateVector"/> (an encoded state vector, e.g. from
    /// <see cref="EncodeStateVectorFromUpdate"/> or <see cref="YDoc.EncodeStateVectorV1"/>).
    /// </summary>
    public static byte[] DiffUpdate(byte[] update, byte[] stateVector)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(stateVector);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV1.Rent();
            try
            {
                DiffUpdateCore(new UpdateDecoderV1(new Lib0Reader(update)), stateVector, encoder);
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
    public static byte[] DiffUpdateV2(byte[] update, byte[] stateVector)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(stateVector);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV2.Rent();
            try
            {
                DiffUpdateCore(new UpdateDecoderV2(new Lib0Reader(update), MaxStructsPerUpdate), stateVector, encoder);
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
                DiffUpdateCore(new UpdateDecoderV2(new Lib0Reader(pending), maxStructs: long.MaxValue), targetStateVector, encoder);
                updates.Add(encoder.ToArray());
            }
            finally
            {
                EncodingUtils.ReturnEncoder(encoder);
            }
        }

        return MergeUpdatesCore(updates, v2, maxStructs: long.MaxValue);
    }

    private static void DiffUpdateCore(IUpdateDecoder decoder, IDictionary<long, long> state, IUpdateEncoder encoder)
    {
        var lazyStructWriter = new LazyStructWriter(encoder);
        var reader = new LazyStructReader(decoder, filterSkips: false);

        while (reader.Curr != null)
        {
            var curr = reader.Curr;
            var currClient = curr.Id.Client;

            if (curr is Skip)
            {
                // The first written struct shouldn't be a Skip.
                reader.Next();
                continue;
            }

            state.TryGetValue(currClient, out var svClock);

            if (curr.Id.Clock + curr.Length > svClock)
            {
                LazyStructWriter.WriteStruct(lazyStructWriter, curr, (int)Math.Max(svClock - curr.Id.Clock, 0));
                reader.Next();

                while (reader.Curr != null && reader.Curr.Id.Client == currClient)
                {
                    LazyStructWriter.WriteStruct(lazyStructWriter, reader.Curr, 0);
                    reader.Next();
                }
            }
            else
            {
                // Read until something new comes up.
                while (reader.Curr != null && reader.Curr.Id.Client == currClient && reader.Curr.Id.Clock + reader.Curr.Length <= svClock)
                {
                    reader.Next();
                }
            }
        }

        LazyStructWriter.Finish(lazyStructWriter);

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
    public static byte[] EncodeStateVectorFromUpdate(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => EncodeStateVectorFromUpdateCore(new UpdateDecoderV1(new Lib0Reader(update))));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="EncodeStateVectorFromUpdate"/>.
    /// </summary>
    public static byte[] EncodeStateVectorFromUpdateV2(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => EncodeStateVectorFromUpdateCore(new UpdateDecoderV2(new Lib0Reader(update), MaxStructsPerUpdate)));
    }

    private static byte[] EncodeStateVectorFromUpdateCore(IUpdateDecoder decoder)
    {
        var reader = new LazyStructReader(decoder, filterSkips: false);
        var pairs = new List<(long Client, long Clock)>();
        var curr = reader.Curr;

        if (curr != null)
        {
            var currClient = curr.Id.Client;
            var stopCounting = curr.Id.Clock != 0; // Must start at 0.
            var currClock = stopCounting ? 0 : curr.Id.Clock + curr.Length;

            for (; curr != null; curr = reader.Next())
            {
                if (currClient != curr.Id.Client)
                {
                    if (currClock != 0)
                    {
                        // We found a new client - remember what we have.
                        pairs.Add((currClient, currClock));
                    }

                    currClient = curr.Id.Client;
                    currClock = 0;
                    stopCounting = curr.Id.Clock != 0;
                }

                // We ignore Skips.
                if (curr is Skip)
                {
                    stopCounting = true;
                }

                if (!stopCounting)
                {
                    currClock = curr.Id.Clock + curr.Length;
                }
            }

            // Remember what we have.
            if (currClock != 0)
            {
                pairs.Add((currClient, currClock));
            }
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
    public static UpdateMeta ParseUpdateMeta(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => ParseUpdateMetaCore(new UpdateDecoderV1(new Lib0Reader(update))));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="ParseUpdateMeta"/>.
    /// </summary>
    public static UpdateMeta ParseUpdateMetaV2(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => ParseUpdateMetaCore(new UpdateDecoderV2(new Lib0Reader(update), MaxStructsPerUpdate)));
    }

    private static UpdateMeta ParseUpdateMetaCore(IUpdateDecoder decoder)
    {
        var from = new Dictionary<long, long>();
        var to = new Dictionary<long, long>();

        var reader = new LazyStructReader(decoder, filterSkips: false);
        var curr = reader.Curr;

        if (curr != null)
        {
            var currClient = curr.Id.Client;
            var currClock = curr.Id.Clock;

            // Write the beginning to 'from'.
            from[currClient] = currClock;

            for (; curr != null; curr = reader.Next())
            {
                if (currClient != curr.Id.Client)
                {
                    // We found a new client.
                    to[currClient] = currClock;
                    from[curr.Id.Client] = curr.Id.Clock;
                    currClient = curr.Id.Client;
                }

                currClock = curr.Id.Clock + curr.Length;
            }

            to[currClient] = currClock;
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
    public static byte[] ConvertUpdateFormatV1ToV2(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV2.Rent();
            try
            {
                ConvertUpdateFormatCore(new UpdateDecoderV1(new Lib0Reader(update)), encoder);
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
    public static byte[] ConvertUpdateFormatV2ToV1(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() =>
        {
            var encoder = UpdateEncoderV1.Rent();
            try
            {
                ConvertUpdateFormatCore(new UpdateDecoderV2(new Lib0Reader(update), MaxStructsPerUpdate), encoder);
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
        var reader = new LazyStructReader(decoder, filterSkips: false);
        var writer = new LazyStructWriter(encoder);

        for (var curr = reader.Curr; curr != null; curr = reader.Next())
        {
            LazyStructWriter.WriteStruct(writer, curr, 0);
        }

        LazyStructWriter.Finish(writer);

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
    public static DecodedUpdate DecodeUpdate(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => DecodeUpdateCore(new UpdateDecoderV1(new Lib0Reader(update))));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="DecodeUpdate"/>.
    /// </summary>
    public static DecodedUpdate DecodeUpdateV2(byte[] update)
    {
        ArgumentNullException.ThrowIfNull(update);
        return YUpdateException.Decode(() => DecodeUpdateCore(new UpdateDecoderV2(new Lib0Reader(update), MaxStructsPerUpdate)));
    }

    private static DecodedUpdate DecodeUpdateCore(IUpdateDecoder decoder)
    {
        var structs = new List<AbstractStruct>();
        var reader = new LazyStructReader(decoder, filterSkips: false);

        for (var curr = reader.Curr; curr != null; curr = reader.Next())
        {
            structs.Add(curr);
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
    public static string DescribeUpdate(byte[] update)
    {
        return DescribeDecodedUpdate(DecodeUpdate(update));
    }

    /// <summary>
    /// The v2-format counterpart of <see cref="DescribeUpdate"/>.
    /// </summary>
    public static string DescribeUpdateV2(byte[] update)
    {
        return DescribeDecodedUpdate(DecodeUpdateV2(update));
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

        sb.AppendLine($"Structs ({decoded.Structs.Count}):");
        foreach (var str in decoded.Structs)
        {
            sb.Append("  ").AppendLine(DescribeStruct(str));
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

    private static string DescribeStruct(AbstractStruct str)
    {
        switch (str)
        {
            case Skip skip:
                return $"Skip(client={skip.Id.Client}, clock={skip.Id.Clock}, length={skip.Length})";
            case GC gc:
                return $"GC(client={gc.Id.Client}, clock={gc.Id.Clock}, length={gc.Length})";
            case Item item:
                return $"Item(client={item.Id.Client}, clock={item.Id.Clock}, length={item.Length}, " +
                       $"deleted={item.Deleted}, content={item.Content?.GetType().Name})";
            default:
                return str.ToString();
        }
    }

    #endregion
}
