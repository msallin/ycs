// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ycs
{
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
            if (updates == null)
            {
                throw new ArgumentNullException(nameof(updates));
            }

            return MergeUpdatesCore(updates, s => new UpdateDecoderV1(s), () => new UpdateEncoderV1());
        }

        /// <summary>
        /// The v2-format counterpart of <see cref="MergeUpdates"/>.
        /// </summary>
        public static byte[] MergeUpdatesV2(IList<byte[]> updates)
        {
            if (updates == null)
            {
                throw new ArgumentNullException(nameof(updates));
            }

            return MergeUpdatesCore(updates, s => new UpdateDecoderV2(s), () => new UpdateEncoderV2());
        }

        private static byte[] MergeUpdatesCore(IList<byte[]> updates, Func<Stream, IUpdateDecoder> newDecoder, Func<IUpdateEncoder> newEncoder)
        {
            if (updates.Count == 1)
            {
                return updates[0];
            }

            var updateDecoders = updates
                .Select(u => newDecoder(new MemoryStream(u, writable: false)))
                .ToList();

            try
            {
                var lazyStructDecoders = updateDecoders
                    .Select(d => new LazyStructReader(d, filterSkips: true))
                    .ToList();

                WriteState currWrite = null;

                using (var updateEncoder = newEncoder())
                {
                    var lazyStructEncoder = new LazyStructWriter(updateEncoder);

                    // Note: we need to ensure that all lazyStructDecoders are fully consumed.
                    // Note: should merge document updates whenever possible - even from different updates.
                    // Note: should handle that some operations cannot be applied yet.
                    while (true)
                    {
                        // Write higher clients first - sort by clientID & clock and remove decoders without content.
                        lazyStructDecoders = lazyStructDecoders.Where(dec => dec.Curr != null).ToList();
                        lazyStructDecoders.Sort((dec1, dec2) =>
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

                    // Every decoder above was drained (Curr == null) before the loop could exit, so
                    // each one's stream is now positioned right at the start of its delete set.
                    var deleteSets = updateDecoders.Select(DeleteSet.Read).ToList();
                    var mergedDeleteSet = MergeDeleteSets(deleteSets);
                    mergedDeleteSet.Write(updateEncoder);

                    return updateEncoder.ToArray();
                }
            }
            finally
            {
                foreach (var decoder in updateDecoders)
                {
                    decoder.Dispose();
                }
            }
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

        /// <summary>
        /// Merges several delete sets into one, exactly like real Yjs' <c>mergeDeleteSets</c> +
        /// <c>sortAndMergeDeleteSet</c> (src/utils/DeleteSet.js).
        /// <br/>
        /// We can't just call the constructor <see cref="DeleteSet(IList{DeleteSet})"/> here: its
        /// merge step (<see cref="DeleteSet.SortAndMergeDeleteSet"/>, a file we don't own) only
        /// coalesces ranges that are exactly adjacent (<c>left.Clock + left.Length == right.Clock</c>),
        /// not ranges that overlap or duplicate one another
        /// (<c>left.Clock + left.Length &gt;= right.Clock</c>, as real Yjs checks). Overlaps are
        /// exactly what merging the SAME logical delete range from more than one update produces -
        /// e.g. merging updates that were themselves already merged - so this reimplements the
        /// correct algorithm instead of relying on that constructor.
        /// </summary>
        private static DeleteSet MergeDeleteSets(IList<DeleteSet> deleteSets)
        {
            var merged = new DeleteSet();

            for (var dssIndex = 0; dssIndex < deleteSets.Count; dssIndex++)
            {
                foreach (var kvp in deleteSets[dssIndex].Clients)
                {
                    var client = kvp.Key;
                    if (merged.Clients.ContainsKey(client))
                    {
                        // Write all missing keys from the current ds and all following ones. If
                        // 'merged' already contains 'client', the current ds has already been added.
                        continue;
                    }

                    var dels = new List<DeleteSet.DeleteItem>(kvp.Value);
                    for (var i = dssIndex + 1; i < deleteSets.Count; i++)
                    {
                        if (deleteSets[i].Clients.TryGetValue(client, out var appends))
                        {
                            dels.AddRange(appends);
                        }
                    }

                    merged.Clients[client] = dels;
                }
            }

            SortAndMergeDeleteSetItems(merged);
            return merged;
        }

        private static void SortAndMergeDeleteSetItems(DeleteSet ds)
        {
            foreach (var dels in ds.Clients.Values)
            {
                dels.Sort((a, b) => a.Clock.CompareTo(b.Clock));

                // Merge items without filtering or splicing the array.
                // i is the current pointer.
                // j refers to the current insert position for the pointed item.
                // Try to merge dels[i] into dels[j-1], or set dels[j] = dels[i].
                int i, j;
                for (i = 1, j = 1; i < dels.Count; i++)
                {
                    var left = dels[j - 1];
                    var right = dels[i];

                    if (left.Clock + left.Length >= right.Clock)
                    {
                        dels[j - 1] = new DeleteSet.DeleteItem(left.Clock, Math.Max(left.Length, right.Clock + right.Length - left.Clock));
                    }
                    else
                    {
                        if (j < i)
                        {
                            dels[j] = right;
                        }

                        j++;
                    }
                }

                if (j < dels.Count)
                {
                    dels.RemoveRange(j, dels.Count - j);
                }
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
            using (var decoder = new UpdateDecoderV1(new MemoryStream(update, writable: false)))
            using (var encoder = new UpdateEncoderV1())
            {
                DiffUpdateCore(decoder, stateVector, encoder);
                return encoder.ToArray();
            }
        }

        /// <summary>
        /// The v2-format counterpart of <see cref="DiffUpdate"/>.
        /// </summary>
        public static byte[] DiffUpdateV2(byte[] update, byte[] stateVector)
        {
            using (var decoder = new UpdateDecoderV2(new MemoryStream(update, writable: false)))
            using (var encoder = new UpdateEncoderV2())
            {
                DiffUpdateCore(decoder, stateVector, encoder);
                return encoder.ToArray();
            }
        }

        private static void DiffUpdateCore(IUpdateDecoder decoder, byte[] stateVector, IUpdateEncoder encoder)
        {
            var state = EncodingUtils.DecodeStateVector(new MemoryStream(stateVector, writable: false));
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
            using (var decoder = new UpdateDecoderV1(new MemoryStream(update, writable: false)))
            {
                return EncodeStateVectorFromUpdateCore(decoder);
            }
        }

        /// <summary>
        /// The v2-format counterpart of <see cref="EncodeStateVectorFromUpdate"/>.
        /// </summary>
        public static byte[] EncodeStateVectorFromUpdateV2(byte[] update)
        {
            using (var decoder = new UpdateDecoderV2(new MemoryStream(update, writable: false)))
            {
                return EncodeStateVectorFromUpdateCore(decoder);
            }
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

            using (var ms = new MemoryStream())
            {
                ms.WriteVarUint((uint)pairs.Count);
                foreach (var (client, clock) in pairs)
                {
                    ms.WriteVarUint((uint)client);
                    ms.WriteVarUint((uint)clock);
                }

                return ms.ToArray();
            }
        }

        #endregion

        #region ParseUpdateMeta

        /// <summary>
        /// Returns, for every client that contributed at least one struct to a v1-format update,
        /// the clock range it covers.
        /// </summary>
        public static UpdateMeta ParseUpdateMeta(byte[] update)
        {
            using (var decoder = new UpdateDecoderV1(new MemoryStream(update, writable: false)))
            {
                return ParseUpdateMetaCore(decoder);
            }
        }

        /// <summary>
        /// The v2-format counterpart of <see cref="ParseUpdateMeta"/>.
        /// </summary>
        public static UpdateMeta ParseUpdateMetaV2(byte[] update)
        {
            using (var decoder = new UpdateDecoderV2(new MemoryStream(update, writable: false)))
            {
                return ParseUpdateMetaCore(decoder);
            }
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
        /// information; this is a pure re-encoding.
        /// </summary>
        public static byte[] ConvertUpdateFormatV1ToV2(byte[] update)
        {
            using (var decoder = new UpdateDecoderV1(new MemoryStream(update, writable: false)))
            using (var encoder = new UpdateEncoderV2())
            {
                ConvertUpdateFormatCore(decoder, encoder);
                return encoder.ToArray();
            }
        }

        /// <summary>
        /// Re-encodes a v2-format update in the v1 format.
        /// </summary>
        public static byte[] ConvertUpdateFormatV2ToV1(byte[] update)
        {
            using (var decoder = new UpdateDecoderV2(new MemoryStream(update, writable: false)))
            using (var encoder = new UpdateEncoderV1())
            {
                ConvertUpdateFormatCore(decoder, encoder);
                return encoder.ToArray();
            }
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
            ds.Write(encoder);
        }

        #endregion

        #region DecodeUpdate

        /// <summary>
        /// Decodes a v1-format update into its structs (in order) and its delete set, without
        /// applying it to a document.
        /// </summary>
        public static DecodedUpdate DecodeUpdate(byte[] update)
        {
            using (var decoder = new UpdateDecoderV1(new MemoryStream(update, writable: false)))
            {
                return DecodeUpdateCore(decoder);
            }
        }

        /// <summary>
        /// The v2-format counterpart of <see cref="DecodeUpdate"/>.
        /// </summary>
        public static DecodedUpdate DecodeUpdateV2(byte[] update)
        {
            using (var decoder = new UpdateDecoderV2(new MemoryStream(update, writable: false)))
            {
                return DecodeUpdateCore(decoder);
            }
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
}
