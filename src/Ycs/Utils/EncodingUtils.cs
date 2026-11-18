using System.Buffers;
using System.Diagnostics;

namespace Ycs;

/// <summary>
/// We use first five bits in the info flag for determining the type of the struct.
/// 0: GC
/// 1: Deleted content
/// 2: JSON content
/// 3: Binary content
/// 4: String content
/// 5: Embed content (for richtext content)
/// 6: Format content (a formatting marker for richtext content)
/// 7: Type content
/// 8: Any content
/// 9: Doc content
/// </summary>
internal static class EncodingUtils
{
    /// <summary>Reads the content of a format, embed, binary, JSON or document item, the kinds the table holds as objects.</summary>
    private static IContent ReadColdContent<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input, byte info)
        where TDecoder : IUpdateDecoder
    {
        switch (info & Bits.Bits5)
        {
            case (int)StagedKind.Json:
                return ContentJson.Read(ref decoder, input);
            case (int)StagedKind.Binary:
                return ContentBinary.Read(ref decoder, input);
            case (int)StagedKind.Embed:
                return ContentEmbed.Read(ref decoder, input);
            case (int)StagedKind.Format:
                return ContentFormat.Read(ref decoder, input);
            default:
                return ContentDoc.Read(ref decoder, input);
        }
    }

    /// <param name="structs">All structs by 'client'.</param>
    /// <param name="clock">Write structs starting with 'ItemId(client,clock)'.</param>
    public static void WriteStructs(IUpdateEncoder encoder, ClientStructs structs, long client, long clock)
    {
        // Write first id.
        clock = Math.Max(clock, structs[0].Id.Clock);
        int startNewStructs = structs.FindIndex(clock);

        // Write # encoded structs.
        encoder.RestWriter.WriteVarUint((uint)(structs.Count - startNewStructs));
        encoder.WriteClient(client);
        encoder.RestWriter.WriteVarUint((uint)clock);

        // Write first struct with offset.
        var firstStruct = structs[startNewStructs];
        firstStruct.Write(encoder, (int)(clock - firstStruct.Id.Clock));

        for (int i = startNewStructs + 1; i < structs.Count; i++)
        {
            structs[i].Write(encoder, 0);
        }
    }

    /// <summary>Up to this many clients, the client lists of the encoders are sorted on the stack.</summary>
    internal const int StackClients = 64;


    /// <summary>
    /// Writes the structs of every client the store has more of than <paramref name="sm"/> says,
    /// highest client id first (Yjs' <c>writeClientsStructs</c>).
    /// </summary>
    public static void WriteClientsStructs(IUpdateEncoder encoder, StructStore store, IDictionary<long, long> sm)
    {
        var clientStructs = store.Clients;
        var capacity = clientStructs.Count;
        long[]? rentedClients = null;
        long[]? rentedClocks = null;
        Span<long> clients = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClients = BufferPool<long>.Rent(capacity));
        Span<long> clocks = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClocks = BufferPool<long>.Rent(capacity));
        try
        {
            var count = 0;
            foreach (var (client, structs) in clientStructs)
            {
                var clock = sm.TryGetValue(client, out var known) ? known : 0;
                var last = structs[^1];

                // Only write if new structs are available.
                if (last.Id.Clock + last.Length > clock)
                {
                    clients[count] = client;
                    clocks[count] = clock;
                    count++;
                }
            }

            WriteSorted(encoder, store, clients[..count], clocks[..count]);
        }
        finally
        {
            ReturnClients(rentedClients, rentedClocks);
        }
    }

    /// <summary>
    /// Writes what the store has of the clients the change log records from entry <paramref name="from"/> on, from the
    /// end each had then: <see cref="WriteClientsStructs(IUpdateEncoder, StructStore, IDictionary{long, long})"/> for
    /// the state when the log had <paramref name="from"/> entries, without a walk over the clients that did not change.
    /// </summary>
    public static void WriteClientsStructs(IUpdateEncoder encoder, StructStore store, int from)
    {
        var capacity = store.ChangeCount - from;
        long[]? rentedClients = null;
        long[]? rentedClocks = null;
        Span<long> clients = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClients = BufferPool<long>.Rent(capacity));
        Span<long> clocks = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClocks = BufferPool<long>.Rent(capacity));
        try
        {
            var count = 0;
            for (var i = from; i < store.ChangeCount; i++)
            {
                if (store.IsFirstFrom(i, from))
                {
                    // A client that is in the log grew after the entry.
                    var change = store.ChangeAt(i);
                    clients[count] = change.Client;
                    clocks[count] = change.EndBefore;
                    count++;
                }
            }

            WriteSorted(encoder, store, clients[..count], clocks[..count]);
        }
        finally
        {
            ReturnClients(rentedClients, rentedClocks);
        }
    }

    private static void WriteSorted(IUpdateEncoder encoder, StructStore store, Span<long> clients, Span<long> clocks)
    {
        // Write # states that were updated.
        encoder.RestWriter.WriteVarUint((uint)clients.Length);

        // Write items with higher client ids first.
        // This heavily improves the conflict resolution algorithm.
        clients.Sort(clocks);
        for (var i = clients.Length - 1; i >= 0; i--)
        {
            WriteStructs(encoder, store.Clients[clients[i]], clients[i], clocks[i]);
        }
    }

    private static void ReturnClients(long[]? clients, long[]? clocks)
    {
        if (clients != null)
        {
            BufferPool<long>.Return(clients);
            BufferPool<long>.Return(clocks!); // Rented with clients.
        }
    }

    /// <summary>
    /// Writes the whole store, or what a peer with <paramref name="targetStateVector"/> is missing, as one
    /// update: the structs, then the delete set, written straight from the store.
    /// </summary>
    public static void WriteStateAsUpdate(IUpdateEncoder encoder, StructStore store, IDictionary<long, long> targetStateVector)
    {
        WriteClientsStructs(encoder, store, targetStateVector);
        DeleteSet.WriteFromStore(encoder, store);
    }

    /// <summary>
    /// Yjs' <c>encodeStateAsUpdate</c> / <c>encodeStateAsUpdateV2</c> as one array. A malformed
    /// <paramref name="encodedTargetStateVector"/> throws <see cref="YUpdateException"/>.
    /// </summary>
    public static byte[] EncodeStateAsUpdate(StructStore store, ReadOnlySpan<byte> encodedTargetStateVector, bool v2)
    {
        var targetStateVector = TargetStateVector.Rent();
        IUpdateEncoder? encoder = null;
        try
        {
            var target = targetStateVector.Decode(encodedTargetStateVector);
            encoder = v2 ? UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
            WriteStateAsUpdate(encoder, store, target);
            var update = encoder.ToArray();
            return store.HasPending ? UpdateOperations.MergeWithPending(update, store, target, v2) : update;
        }
        finally
        {
            ReturnEncoder(encoder);
            targetStateVector.Return();
        }
    }

    /// <summary>
    /// Like <see cref="EncodeStateAsUpdate(StructStore, ReadOnlySpan{byte}, bool)"/>, into <paramref name="destination"/>.
    /// The v1 format is written straight into it; v2 (whose columns come first) is assembled from
    /// pooled column buffers with one copy. A malformed state vector throws before anything is
    /// written; should encoding fail later (a bug), the destination may hold part of the update.
    /// </summary>
    public static void EncodeStateAsUpdate(StructStore store, IBufferWriter<byte> destination, ReadOnlySpan<byte> encodedTargetStateVector, bool v2)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var targetStateVector = TargetStateVector.Rent();
        IUpdateEncoder? encoder = null;
        try
        {
            var target = targetStateVector.Decode(encodedTargetStateVector);
            if (store.HasPending)
            {
                encoder = v2 ? UpdateEncoderV2.Rent() : UpdateEncoderV1.Rent();
                WriteStateAsUpdate(encoder, store, target);
                destination.Write(UpdateOperations.MergeWithPending(encoder.ToArray(), store, target, v2));
            }
            else if (v2)
            {
                var encoderV2 = UpdateEncoderV2.Rent();
                encoder = encoderV2;
                WriteStateAsUpdate(encoderV2, store, target);
                encoderV2.WriteTo(destination);
            }
            else
            {
                var encoderV1 = UpdateEncoderV1.Rent();
                encoder = encoderV1;
                encoderV1.Reset(destination);
                WriteStateAsUpdate(encoderV1, store, target);
                encoderV1.Flush();
            }
        }
        finally
        {
            ReturnEncoder(encoder);
            targetStateVector.Return();
        }
    }

    /// <summary>Yjs' <c>encodeStateVector</c>: the same bytes for both update formats.</summary>
    public static byte[] EncodeStateVector(StructStore store)
    {
        var encoder = UpdateEncoderV1.Rent();
        try
        {
            WriteStateVector(encoder, store);
            return encoder.ToArray();
        }
        finally
        {
            encoder.Return();
        }
    }

    /// <summary><see cref="EncodeStateVector(StructStore)"/> straight into <paramref name="destination"/>.</summary>
    public static void EncodeStateVector(StructStore store, IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var encoder = UpdateEncoderV1.Rent();
        try
        {
            encoder.Reset(destination);
            WriteStateVector(encoder, store);
            encoder.Flush();
        }
        finally
        {
            encoder.Return();
        }
    }

    internal static void ReturnEncoder(IUpdateEncoder? encoder)
    {
        switch (encoder)
        {
            case UpdateEncoderV1 v1:
                v1.Return();
                break;
            case UpdateEncoderV2 v2:
                v2.Return();
                break;
        }
    }

    /// <summary>
    /// Reads the struct section of an update one struct at a time, checking everything that can be
    /// checked without a document: every value within its range, no empty struct, no struct ending
    /// beyond <see cref="Limits.MaxClock"/>, no client listed twice, an origin or parent of the same
    /// client only at an earlier clock (it must have existed when the struct was created), and no more
    /// structs than <see cref="IUpdateDecoder.MaxStructs"/>. Throws <see cref="YUpdateException"/>.
    /// Each struct is staged into the table. A mutable struct: keep it in a local or a non-readonly field.
    /// </summary>
    internal struct StructCursor
    {
        private readonly long _sections;
        private readonly HashSet<long>? _clients;
        private StagingTable _staging;
        private long _section;
        private long _structsLeft;
        private long _structCount;
        private long _client;
        private long _clock;
        private object?[]? _values;

        /// <param name="seenClients">An empty set to find a client listed twice; one is created when needed if null.</param>
        /// <param name="staging">
        /// An empty table that takes the structs read: one client range per section, one staged struct per struct.
        /// The cursor only appends; the table's owner resets it.
        /// </param>
        private StructCursor(StagingTable staging, long sections, HashSet<long>? clients)
        {
            _staging = staging;
            _sections = sections;
            _clients = clients;
        }

        /// <summary>
        /// Reads the number of sections from <paramref name="decoder"/>, which is then read through only by this cursor until
        /// <see cref="NextStaged"/> returns -1. <paramref name="input"/> is the input the decoder was made over.
        /// </summary>
        public static StructCursor Begin<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input, StagingTable staging, HashSet<long>? seenClients = null)
            where TDecoder : IUpdateDecoder
        {
            var sections = decoder.Reader.ReadVarUint();
            return new StructCursor(staging, sections, sections > 1 ? seenClients ?? new HashSet<long>() : null);
        }

        /// <summary>Stages the structs from here on into <paramref name="staging"/>, which has begun the open client's range.</summary>
        public void UseStaging(StagingTable staging) => _staging = staging;

        /// <summary>
        /// The next struct, read from the decoder into the table and checked as the cursor's summary says: a String's
        /// characters come from <see cref="IUpdateDecoder.ReadStringChars"/>, a Type's reference and name from
        /// <see cref="ContentType.ReadHeader"/>. Returns its index in the table, or -1 after the last.
        /// </summary>
        public int NextStaged<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
            where TDecoder : IUpdateDecoder
        {
            while (_structsLeft == 0)
            {
                if (_section == _sections)
                {
                    return -1;
                }

                _section++;
                ReadSectionHeader(ref decoder, input);
            }

            _structsLeft--;
            var i = StageStruct(ref decoder, input, _staging, _client, _clock, ref _values);
            _clock += _staging.At(i).Length;
            return i;
        }

        private void ReadSectionHeader<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
            where TDecoder : IUpdateDecoder
        {
            long numberOfStructs = decoder.Reader.ReadVarUint();
            var client = Limits.CheckClient(decoder.ReadClient(input));
            var clock = Limits.CheckClock(decoder.Reader.ReadVarUint());

            if (_clients != null && !_clients.Add(client))
            {
                throw new YUpdateException($"The update lists client {client} more than once.");
            }

            if (numberOfStructs > decoder.MaxStructs - _structCount)
            {
                throw new YUpdateException($"The update holds more than {decoder.MaxStructs} structs (UpdateLimits.MaxStructsPerUpdate).");
            }

            _structCount += numberOfStructs;
            if (numberOfStructs > 0)
            {
                _staging.BeginClient(client, clock);
            }

            _structsLeft = numberOfStructs;
            _client = client;
            _clock = clock;
        }
    }

    /// <summary>
    /// Reads a whole update: its structs into <paramref name="table"/> (as <see cref="StructCursor"/> does) and then its
    /// delete set into the empty <paramref name="ds"/> (as <see cref="DeleteSet.ReadInto"/> does).
    /// </summary>
    internal static void ReadUpdate<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input, StagingTable table, DeleteSet ds, HashSet<long>? seenClients, Stack<List<DeleteSet.DeleteItem>>? spareLists)
        where TDecoder : IUpdateDecoder
    {
        var cursor = StructCursor.Begin(ref decoder, input, table, seenClients);
        while (cursor.NextStaged(ref decoder, input) >= 0)
        {
        }

        DeleteSet.ReadInto(ref decoder, input, ds, spareLists);
    }

    // The struct is added once it is read whole, when its length is known.
    private static int StageStruct<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input, StagingTable table, long client, long clock, ref object?[]? values)
        where TDecoder : IUpdateDecoder
    {
        var id = new ItemId(client, clock);
        var info = decoder.ReadInfo(input);
        switch (info & Bits.Bits5)
        {
            case (int)StagedKind.GC:
                var gcLength = decoder.ReadLength(input);
                Limits.CheckRange(clock, gcLength);
                return table.Add(StagedKind.GC, gcLength);
            case (int)StagedKind.Skip:
                // A range of clocks this update has no data for (written by mergeUpdates).
                var skipLength = decoder.Reader.ReadVarUint();
                Limits.CheckRange(clock, skipLength);
                return table.Add(StagedKind.Skip, (int)skipLength);
        }

        var leftOrigin = (info & Bit.Bit8) == Bit.Bit8 ? (ItemId?)CheckEarlier(decoder.ReadLeftId(input), id) : null;
        var rightOrigin = (info & Bit.Bit7) == Bit.Bit7 ? (ItemId?)CheckEarlier(decoder.ReadRightId(input), id) : null;

        string? parentName = null;
        ItemId? parentId = null;
        string? parentSub = null;
        if ((info & (Bit.Bit7 | Bit.Bit8)) == 0)
        {
            if (decoder.ReadParentInfo(input))
            {
                parentName = decoder.ReadString(input);
            }
            else
            {
                parentId = CheckEarlier(decoder.ReadLeftId(input), id);
            }

            parentSub = (info & Bit.Bit6) == Bit.Bit6 ? decoder.ReadString(input) : null;
        }

        int i;
        switch (info & Bits.Bits5)
        {
            case 1:
                var deleted = decoder.ReadLength(input);
                Limits.CheckRange(clock, deleted);
                i = table.Add(StagedKind.Deleted, deleted, leftOrigin, rightOrigin);
                break;
            case 4:
                // The bound is the room the string may need; where it would grow the buffer, the exact length is.
                var bound = decoder.ReadStringBound(input);
                var textLength = decoder.ReadStringChars(input, table.CharRoom(bound <= table.CharsFree ? bound : decoder.ReadStringExact(input)));
                Limits.CheckRange(clock, textLength);
                i = table.Add(StagedKind.String, textLength, leftOrigin, rightOrigin);
                break;
            case 8:
                var count = decoder.ReadLength(input);
                values ??= new object?[16];
                for (var v = 0; v < count; v++)
                {
                    if (v == values.Length)
                    {
                        Array.Resize(ref values, v * 2);
                    }

                    values[v] = decoder.ReadAny(input);
                }

                Limits.CheckRange(clock, count);
                i = table.Add(StagedKind.Any, count, leftOrigin, rightOrigin);
                values.AsSpan(0, count).CopyTo(table.Values(i));
                Array.Clear(values, 0, count);
                break;
            case 7:
                var typeRef = ContentType.ReadHeader(ref decoder, input, out var typeName);
                Limits.CheckRange(clock, 1);
                i = table.Add(StagedKind.Type, 1, leftOrigin, rightOrigin);
                table.SetType(i, typeRef, typeName);
                break;
            case 2 or 3 or 5 or 6 or 9:
                var cold = ReadColdContent(ref decoder, input, info);
                Limits.CheckRange(clock, cold.Length);
                i = table.Add((StagedKind)(info & Bits.Bits5), cold.Length, leftOrigin, rightOrigin);
                table.SetCold(i, cold);
                break;
            default:
                throw new YUpdateException($"Unknown content type: {info & Bits.Bits5}.");
        }

        if (parentName != null)
        {
            table.SetParentRoot(i, parentName);
        }
        else if (parentId is { } parent)
        {
            table.SetParentId(i, parent);
        }

        if (parentSub != null)
        {
            table.SetParentSub(i, parentSub);
        }

        return i;
    }

    private static ItemId CheckEarlier(ItemId reference, ItemId id)
    {
        Limits.CheckClient(reference.Client);
        if (reference.Client == id.Client && reference.Clock >= id.Clock)
        {
            throw new YUpdateException("A struct refers to a later struct of its own client.");
        }

        return reference;
    }

    public static void WriteStateVector(IDSEncoder encoder, IDictionary<long, long> sv)
    {
        var capacity = sv.Count;
        long[]? rentedClients = null;
        long[]? rentedClocks = null;
        Span<long> clients = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClients = BufferPool<long>.Rent(capacity));
        Span<long> clocks = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClocks = BufferPool<long>.Rent(capacity));
        try
        {
            var count = 0;
            if (sv is Dictionary<long, long> dictionary)
            {
                foreach (var (client, clock) in dictionary)
                {
                    clients[count] = client;
                    clocks[count++] = clock;
                }
            }
            else
            {
                foreach (var (client, clock) in sv)
                {
                    clients[count] = client;
                    clocks[count++] = clock;
                }
            }

            WriteStateVector(encoder, clients[..count], clocks[..count]);
        }
        finally
        {
            if (rentedClients != null)
            {
                BufferPool<long>.Return(rentedClients);
                BufferPool<long>.Return(rentedClocks!); // Rented with rentedClients.
            }
        }
    }

    /// <summary>Writes the state vector of <paramref name="store"/> without building it.</summary>
    public static void WriteStateVector(IDSEncoder encoder, StructStore store)
    {
        var clientStructs = store.Clients;
        var capacity = clientStructs.Count;
        long[]? rentedClients = null;
        long[]? rentedClocks = null;
        Span<long> clients = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClients = BufferPool<long>.Rent(capacity));
        Span<long> clocks = capacity <= StackClients ? stackalloc long[StackClients] : (rentedClocks = BufferPool<long>.Rent(capacity));
        try
        {
            var count = 0;
            foreach (var (client, structs) in clientStructs)
            {
                var last = structs[^1];
                clients[count] = client;
                clocks[count++] = last.Id.Clock + last.Length;
            }

            WriteStateVector(encoder, clients[..count], clocks[..count]);
        }
        finally
        {
            if (rentedClients != null)
            {
                BufferPool<long>.Return(rentedClients);
                BufferPool<long>.Return(rentedClocks!); // Rented with rentedClients.
            }
        }
    }

    private static void WriteStateVector(IDSEncoder encoder, Span<long> clients, Span<long> clocks)
    {
        encoder.RestWriter.WriteVarUint((uint)clients.Length);

        // Highest client id first, as Yjs' writeStateVector sorts, so the same state has the same bytes.
        clients.Sort(clocks);
        for (var i = clients.Length - 1; i >= 0; i--)
        {
            encoder.RestWriter.WriteVarUint((ulong)clients[i]);
            encoder.RestWriter.WriteVarUint((uint)clocks[i]);
        }
    }

    public static IDictionary<long, long> ReadStateVector<TDecoder>(ref TDecoder decoder, ReadOnlySpan<byte> input)
        where TDecoder : IDSDecoder
    {
        var ssLength = decoder.Reader.ReadVarUint();

        // Each entry takes at least two bytes.
        var ss = new Dictionary<long, long>(Limits.Capacity(ssLength, decoder.Reader.Remaining / 2));
        ReadStateVectorEntries(decoder.Reader, ssLength, ss);
        return ss;
    }

    private static void ReadStateVectorEntries(Lib0Reader reader, uint count, Dictionary<long, long> ss)
    {
        for (long i = 0; i < count; i++)
        {
            var client = reader.ReadVarUint53();
            var clock = Limits.CheckClock(reader.ReadVarUint());
            ss[client] = clock;
        }
    }

    public static IDictionary<long, long> DecodeStateVector(Lib0Reader input)
    {
        var decoder = new DSDecoderV2(input);
        return ReadStateVector(ref decoder, default);
    }

    /// <summary>
    /// A state vector to encode against, decoded from a span into a dictionary reused on this thread
    /// (with the reader and a pooled copy of the input), so encoding allocates nothing for it.
    /// </summary>
    private sealed class TargetStateVector
    {
        [ThreadStatic]
        private static TargetStateVector? t_free;

        private readonly Dictionary<long, long> _stateVector = new();
        private readonly Lib0Reader _reader = new();

        public static TargetStateVector Rent()
        {
            var result = t_free ?? new TargetStateVector();
            t_free = null;
            return result;
        }

        /// <summary>The decoded state vector, valid until <see cref="Return"/>. Malformed input throws <see cref="YUpdateException"/>.</summary>
        public Dictionary<long, long> Decode(ReadOnlySpan<byte> encoded)
        {
            if (encoded.IsEmpty)
            {
                return _stateVector;
            }

            var buffer = BufferPool<byte>.Rent(encoded.Length);
            try
            {
                encoded.CopyTo(buffer);
                _reader.Reset(buffer, 0, encoded.Length);
                var count = _reader.ReadVarUint();
                _stateVector.EnsureCapacity(Limits.Capacity(count, _reader.Remaining / 2));
                ReadStateVectorEntries(_reader, count, _stateVector);
                return _stateVector;
            }
            catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
            {
                throw YUpdateException.Wrap(e);
            }
            finally
            {
                _reader.Clear();
                BufferPool<byte>.Return(buffer, encoded.Length);
            }
        }

        public void Return()
        {
            // A huge state vector is not kept.
            if (_stateVector.Count <= 1024)
            {
                _stateVector.Clear();
                t_free = this;
            }
        }
    }
}
