using System.Globalization;
using System.Diagnostics;
using System.Text;

namespace Ycs;

/// <summary>
/// The operations of <see cref="YUpdate"/>: merging, diffing against a state vector, extracting a state vector or
/// clock-range metadata, converting between the v1 and v2 formats, describing. The decoders read arrays, so an input
/// is copied into a buffer of the shared array pool for the call.
/// Ported from Yjs' <c>src/utils/updates.js</c> (Yjs 13.6.32). Every one builds on <see cref="LazyStructReader{TDecoder}"/> /
/// <see cref="LazyStructWriter"/>, which stream structs (including 'Skip' placeholders) without a document.
/// Every function takes the <see cref="UpdateLimits"/> of its caller (<see cref="UpdateLimits.Default"/> when null).
/// </summary>
internal static class UpdateOperations
{
    private static long MaxStructs(UpdateLimits? limits) => (limits ?? UpdateLimits.Default).MaxStructsPerUpdate;

    /// <summary>A reader of <paramref name="update"/> whose values may be nested as deeply as <paramref name="limits"/> allow.</summary>
    private static Lib0Reader Reader(ArraySegment<byte> update, UpdateLimits? limits) =>
        new(update.Array!, update.Offset, update.Count) { MaxDepth = (limits ?? UpdateLimits.Default).MaxDepth };

    private static UpdateDecoderV1 DecoderV1(ArraySegment<byte> update, UpdateLimits? limits) => new(Reader(update, limits), MaxStructs(limits));

    private static UpdateDecoderV2 DecoderV2(ArraySegment<byte> update, UpdateLimits? limits) => new(Reader(update, limits), MaxStructs(limits));

    private static IUpdateEncoder Encoder(bool v2) => v2 ? UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();

    #region MergeUpdates

    /// <summary>
    /// Merges updates into one. The result is associative and idempotent, and can be fed updates that an earlier merge
    /// produced (which may hold 'Skip' gaps). As in Yjs, not where one update holds a GC struct and another an item
    /// for the same clock: the struct kept then depends on the order and the grouping (Yjs under V8's sort).
    /// </summary>
    public static byte[] MergeUpdates(ReadOnlySpan<ReadOnlyMemory<byte>> updates, bool v2, UpdateLimits? limits)
    {
        var copies = new PooledCopy[updates.Length];
        var segments = new ArraySegment<byte>[updates.Length];
        var copied = 0;
        try
        {
            for (; copied < updates.Length; copied++)
            {
                copies[copied] = new PooledCopy(updates[copied].Span);
                segments[copied] = copies[copied].Segment;
            }

            try
            {
                return MergeUpdatesCore(segments, v2, MaxStructs(limits), (limits ?? UpdateLimits.Default).MaxDepth, out _);
            }
            catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
            {
                throw YUpdateException.Wrap(e);
            }
        }
        finally
        {
            for (var i = 0; i < copied; i++)
            {
                copies[i].Dispose();
            }
        }
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
                var group = new ArraySegment<byte>[Math.Min(GroupSize, updates.Count - i)];
                for (var j = 0; j < group.Length; j++)
                {
                    group[j] = updates[i + j];
                }

                next.Add(MergeUpdatesCore(group, v2: true, maxStructs: long.MaxValue, maxDepth: int.MaxValue, out var written));

                // The last round merges a single group, and that is the result.
                structs = written >= 0 ? written : structs;
            }

            updates = next;
        }

        return updates[0];
    }

    /// <param name="written">How many structs the result holds; -1 when the single update is returned as it is.</param>
    private static byte[] MergeUpdatesCore(ReadOnlySpan<ArraySegment<byte>> updates, bool v2, long maxStructs, int maxDepth, out long written)
    {
        written = -1;
        if (updates.Length == 1)
        {
            return updates[0].Array!;
        }

        return v2
            ? MergeCore<UpdateDecoderV2>(updates, maxStructs, maxDepth, out written)
            : MergeCore<UpdateDecoderV1>(updates, maxStructs, maxDepth, out written);
    }

    private static byte[] MergeCore<TDecoder>(ReadOnlySpan<ArraySegment<byte>> updates, long maxStructs, int maxDepth, out long written)
        where TDecoder : IUpdateDecoder
    {
        written = -1;
        var v2 = typeof(TDecoder) == typeof(UpdateDecoderV2);

        // Every reader, which stays to read its delete set; the list MergeStructs works on loses a reader once it is drained.
        var readers = new List<LazyStructReader<TDecoder>>(updates.Length);
        var lazyStructDecoders = new List<LazyStructReader<TDecoder>>(updates.Length);
        var tables = new List<StagingTable>(updates.Length + 1);
        IUpdateEncoder updateEncoder = v2 ? UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
        try
        {
            for (var i = 0; i < updates.Length; i++)
            {
                var update = updates[i];
                var reader = new Lib0Reader(update.Array!, update.Offset, update.Count) { MaxDepth = maxDepth };
                var decoder = v2
                    ? (TDecoder)(IUpdateDecoder)new UpdateDecoderV2(reader, maxStructs)
                    : (TDecoder)(IUpdateDecoder)new UpdateDecoderV1(reader, maxStructs);
                var table = StagingTable.Rent();
                tables.Add(table);
                var lazy = new LazyStructReader<TDecoder>(decoder, update, filterSkips: true, table) { Source = i };
                readers.Add(lazy);
                lazyStructDecoders.Add(lazy);
            }

            var pending = StagingTable.Rent();
            tables.Add(pending);
            var writer = new LazyStructWriter(updateEncoder);
            MergeStructs(lazyStructDecoders, updates, writer, pending);
            written = writer.TotalWritten;

            // Every decoder above was drained (Curr < 0) before the loop could exit, so
            // each one's stream is now positioned right at the start of its delete set.
            var deleteSets = new List<DeleteSet>(readers.Count);
            foreach (var lazy in readers)
            {
                deleteSets.Add(DeleteSet.Read(ref lazy.Decoder, updates[lazy.Source]));
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
    private sealed class PendingStruct(LazyStructWriter writer, StagingTable initial)
    {
        private StagingTable table = initial;

        public bool Has { get; private set; }
        public long Client { get; private set; }
        public long Clock { get; private set; }
        public int Length { get; private set; }
        public StagedKind Kind { get; private set; }
        public long End => Clock + Length;

        /// <summary>Takes <paramref name="reader"/>'s current struct, from <paramref name="cut"/> clocks in (Yjs' <c>splice</c>).</summary>
        /// <remarks>
        /// Whole, the struct moves: the reader's table is swapped for this one, so the reader must advance before it is read
        /// again. Cut, it is copied.
        /// </remarks>
        public void Set<TDecoder>(LazyStructReader<TDecoder> reader, int cut)
            where TDecoder : IUpdateDecoder
        {
            if (cut == 0)
            {
                table = reader.SwapTable(table);
            }
            else
            {
                table.Truncate();
                StructStaging.CopyFrom(table, reader.Table, reader.Curr, cut);
            }

            Load();
        }

        public void SetSkip(long client, long clock, int length)
        {
            table.Truncate();
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

    private static void MergeStructs<TDecoder>(List<LazyStructReader<TDecoder>> lazyStructDecoders, ReadOnlySpan<ArraySegment<byte>> inputs, LazyStructWriter lazyStructEncoder, StagingTable pendingTable)
        where TDecoder : IUpdateDecoder
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
                    currDecoder.Next(inputs[currDecoder.Source]);
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
                    currDecoder.Next(inputs[currDecoder.Source]);
                }
                else
                {
                    if (currWrite.End < currDecoder.Clock)
                    {
                        // Yjs' structure kept line by line: the Skip branches here and below are dead. The readers filter Skips,
                        // and the Skip made by SetSkip is replaced by the loop at the bottom before another round starts.
                        if (currWrite.Kind == StagedKind.Skip)
                        {
                            Debug.Fail("A pending Skip does not survive a round.");

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
                                Debug.Fail("A pending Skip does not survive a round.");

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
                            currDecoder.Next(inputs[currDecoder.Source]);
                        }
                    }
                }
            }
            else
            {
                currWrite.Set(currDecoder, 0);
                currDecoder.Next(inputs[currDecoder.Source]);
            }

            for (;
                 currDecoder.Curr >= 0 &&
                 currDecoder.Client == firstClient &&
                 currDecoder.Clock == currWrite.End &&
                 currDecoder.Kind != StagedKind.Skip;
                 currDecoder.Next(inputs[currDecoder.Source]))
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

    /// <summary>Extracts the part of <paramref name="update"/> that is missing relative to the encoded <paramref name="stateVector"/>.</summary>
    public static byte[] DiffUpdate(ReadOnlySpan<byte> update, ReadOnlySpan<byte> stateVector, bool v2, UpdateLimits? limits)
    {
        using var input = new PooledCopy(update);
        using var vector = new PooledCopy(stateVector);
        var segment = input.Segment;
        try
        {
            var encoder = Encoder(v2);
            try
            {
                var vectorReader = new Lib0Reader(vector.Segment.Array!, vector.Segment.Offset, vector.Segment.Count);
                if (v2)
                {
                    var decoder = DecoderV2(segment, limits);
                    DiffUpdateCore(decoder, segment, EncodingUtils.DecodeStateVector(vectorReader), encoder);
                }
                else
                {
                    var decoder = DecoderV1(segment, limits);
                    DiffUpdateCore(decoder, segment, EncodingUtils.DecodeStateVector(vectorReader), encoder);
                }

                return encoder.ToArray();
            }
            finally
            {
                ReturnEncoder(encoder);
            }
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }
    }

    /// <summary>
    /// What <see cref="EncodingUtils.EncodeStateAsUpdate(StructStore, ReadOnlySpan{byte}, bool)"/>
    /// adds while something is pending, as Yjs 13.6's <c>encodeStateAsUpdateV2</c>: the state, the
    /// pending deletions and the pending structs beyond <paramref name="targetStateVector"/>, merged.
    /// </summary>
    internal static byte[] MergeWithPending(byte[] state, StructStore store, IDictionary<long, long> targetStateVector, bool v2)
    {
        var updates = new ArraySegment<byte>[3];
        updates[0] = state;
        var count = 1;
        if (store.PendingDsCount != 0)
        {
            var encoder = v2 ? (IUpdateEncoder)UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
            try
            {
                encoder.RestWriter.WriteVarUint(0);
                store.PendingDs.ToDeleteSet().Write(encoder);
                updates[count++] = encoder.ToArray();
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
                var decoder = new UpdateDecoderV2(new Lib0Reader(pending) { MaxDepth = int.MaxValue }, store.Pending.Structs);
                DiffUpdateCore(decoder, pending, targetStateVector, encoder);
                updates[count++] = encoder.ToArray();
            }
            finally
            {
                EncodingUtils.ReturnEncoder(encoder);
            }
        }

        return MergeUpdatesCore(updates.AsSpan(0, count), v2, maxStructs: long.MaxValue, maxDepth: int.MaxValue, out _);
    }

    private static void DiffUpdateCore<TDecoder>(TDecoder decoder, ReadOnlySpan<byte> update, IDictionary<long, long> state, IUpdateEncoder encoder)
        where TDecoder : IUpdateDecoder
    {
        var table = StagingTable.Rent();
        LazyStructReader<TDecoder> reader;
        try
        {
            var lazyStructWriter = new LazyStructWriter(encoder);
            reader = new LazyStructReader<TDecoder>(decoder, update, filterSkips: false, table);

            while (reader.Curr >= 0)
            {
                var currClient = reader.Client;

                if (reader.Kind == StagedKind.Skip)
                {
                    // The first written struct shouldn't be a Skip.
                    reader.Next(update);
                    continue;
                }

                state.TryGetValue(currClient, out var svClock);

                if (reader.Clock + reader.Length > svClock)
                {
                    LazyStructWriter.WriteStruct(lazyStructWriter, table, reader.Curr, (int)Math.Max(svClock - reader.Clock, 0));
                    reader.Next(update);

                    while (reader.Curr >= 0 && reader.Client == currClient)
                    {
                        LazyStructWriter.WriteStruct(lazyStructWriter, table, reader.Curr, 0);
                        reader.Next(update);
                    }
                }
                else
                {
                    // Read until something new comes up.
                    while (reader.Curr >= 0 && reader.Client == currClient && reader.Clock + reader.Length <= svClock)
                    {
                        reader.Next(update);
                    }
                }
            }

            LazyStructWriter.Finish(lazyStructWriter);
        }
        finally
        {
            StagingTable.Return(table);
        }

        var ds = DeleteSet.Read(ref reader.Decoder, update);
        ds.Write(encoder);
    }

    #endregion

    #region EncodeStateVectorFromUpdate

    /// <summary>
    /// Computes the state vector implied by an update, without applying it to a document: far cheaper than applying the
    /// update to an empty document and encoding its state vector.
    /// </summary>
    public static byte[] EncodeStateVectorFromUpdate(ReadOnlySpan<byte> update, bool v2, UpdateLimits? limits)
    {
        using var input = new PooledCopy(update);
        var segment = input.Segment;
        try
        {
            return v2
                ? EncodeStateVectorFromUpdateCore(DecoderV2(segment, limits), segment)
                : EncodeStateVectorFromUpdateCore(DecoderV1(segment, limits), segment);
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }
    }

    private static byte[] EncodeStateVectorFromUpdateCore<TDecoder>(TDecoder decoder, ReadOnlySpan<byte> update)
        where TDecoder : IUpdateDecoder
    {
        var pairs = new List<(long Client, long Clock)>();
        var table = StagingTable.Rent();
        try
        {
            var reader = new LazyStructReader<TDecoder>(decoder, update, filterSkips: false, table);

            if (reader.Curr >= 0)
            {
                var currClient = reader.Client;
                var stopCounting = reader.Clock != 0; // Must start at 0.
                var currClock = stopCounting ? 0 : reader.Clock + reader.Length;

                for (; reader.Curr >= 0; reader.Next(update))
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

    /// <summary>For every client that contributed at least one struct to an update, the clock range it covers.</summary>
    public static UpdateMeta ParseUpdateMeta(ReadOnlySpan<byte> update, bool v2, UpdateLimits? limits)
    {
        using var input = new PooledCopy(update);
        var segment = input.Segment;
        try
        {
            return v2
                ? ParseUpdateMetaCore(DecoderV2(segment, limits), segment)
                : ParseUpdateMetaCore(DecoderV1(segment, limits), segment);
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }
    }

    private static UpdateMeta ParseUpdateMetaCore<TDecoder>(TDecoder decoder, ReadOnlySpan<byte> update)
        where TDecoder : IUpdateDecoder
    {
        var from = new Dictionary<long, long>();
        var to = new Dictionary<long, long>();

        var table = StagingTable.Rent();
        try
        {
            var reader = new LazyStructReader<TDecoder>(decoder, update, filterSkips: false, table);

            if (reader.Curr >= 0)
            {
                var currClient = reader.Client;
                var currClock = reader.Clock;

                // Write the beginning to 'from'.
                from[currClient] = currClock;

                for (; reader.Curr >= 0; reader.Next(update))
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
    public static byte[] ConvertUpdateFormat(ReadOnlySpan<byte> update, bool fromV2, bool toV2, UpdateLimits? limits)
    {
        using var input = new PooledCopy(update);
        var segment = input.Segment;
        try
        {
            var encoder = Encoder(toV2);
            try
            {
                if (fromV2)
                {
                    ConvertUpdateFormatCore(DecoderV2(segment, limits), segment, encoder);
                }
                else
                {
                    ConvertUpdateFormatCore(DecoderV1(segment, limits), segment, encoder);
                }

                return encoder.ToArray();
            }
            finally
            {
                ReturnEncoder(encoder);
            }
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }
    }

    private static void ConvertUpdateFormatCore<TDecoder>(TDecoder decoder, ReadOnlySpan<byte> update, IUpdateEncoder encoder)
        where TDecoder : IUpdateDecoder
    {
        var table = StagingTable.Rent();
        LazyStructReader<TDecoder> reader;
        try
        {
            reader = new LazyStructReader<TDecoder>(decoder, update, filterSkips: false, table);
            var writer = new LazyStructWriter(encoder);

            for (; reader.Curr >= 0; reader.Next(update))
            {
                LazyStructWriter.WriteStruct(writer, table, reader.Curr, 0);
            }

            LazyStructWriter.Finish(writer);
        }
        finally
        {
            StagingTable.Return(table);
        }

        var ds = DeleteSet.Read(ref reader.Decoder, update);
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

    /// <summary>Decodes an update into its structs (in order) and its delete set, without applying it to a document.</summary>
    public static DecodedUpdate DecodeUpdate(ReadOnlySpan<byte> update, bool v2, UpdateLimits? limits)
    {
        using var input = new PooledCopy(update);
        var segment = input.Segment;
        try
        {
            if (v2)
            {
                var decoder = DecoderV2(segment, limits);
                return DecodeUpdateCore(ref decoder, segment);
            }
            else
            {
                var decoder = DecoderV1(segment, limits);
                return DecodeUpdateCore(ref decoder, segment);
            }
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }
    }

    private static DecodedUpdate DecodeUpdateCore<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> update)
        where TDecoder : IUpdateDecoder
    {
        // The table is the result's: it keeps every struct and is not returned.
        var structs = new StagingTable();
        var ds = new DeleteSet();
        EncodingUtils.ReadUpdate(ref decoder, update, structs, ds, seenClients: null, spareLists: null);
        var deleteSet = new Dictionary<long, IReadOnlyList<DeletedRange>>();

        foreach (var kvp in ds.Clients)
        {
            deleteSet[kvp.Key] = kvp.Value.Select(item => new DeletedRange(item.Clock, item.Length)).ToList();
        }

        return new DecodedUpdate(structs, deleteSet);
    }

    #endregion

    #region Describe

    /// <summary>Describes an update (its structs and its delete set) as a plain string, for debugging.</summary>
    public static string DescribeUpdate(ReadOnlySpan<byte> update, bool v2, UpdateLimits? limits) =>
        DescribeDecodedUpdate(DecodeUpdate(update, v2, limits));

    private static string DescribeDecodedUpdate(DecodedUpdate decoded)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"Structs ({decoded.Staged.Count}):");
        for (var i = 0; i < decoded.Staged.Count; i++)
        {
            sb.Append("  ").AppendLine(DescribeStruct(decoded.Staged, i));
        }

        sb.AppendLine("DeleteSet:");
        foreach (var kvp in decoded.DeleteSet)
        {
            foreach (var range in kvp.Value)
            {
                sb.Append("  ").AppendLine(CultureInfo.InvariantCulture, $"client={kvp.Key} clock={range.Clock} length={range.Length}");
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
