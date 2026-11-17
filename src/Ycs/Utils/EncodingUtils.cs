#nullable disable // Not annotated for nullable reference types yet.

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
    public static IContent ReadItemContent(IUpdateDecoder decoder, byte info)
    {
        switch (info & Bits.Bits5)
        {
            case 1: // Deleted
                return ContentDeleted.Read(decoder);
            case 2: // JSON
                return ContentJson.Read(decoder);
            case 3: // Binary
                return ContentBinary.Read(decoder);
            case 4: // String
                return ContentString.Read(decoder);
            case 5: // Embed
                return ContentEmbed.Read(decoder);
            case 6: // Format
                return ContentFormat.Read(decoder);
            case 7: // Type
                return ContentType.Read(decoder);
            case 8: // Any
                return ContentAny.Read(decoder);
            case 9: // Doc
                return ContentDoc.Read(decoder);
            default:
                throw new YUpdateException($"Unknown content type: {info & Bits.Bits5}.");
        }
    }

    /// <param name="structs">All structs by 'client'.</param>
    /// <param name="clock">Write structs starting with 'ID(client,clock)'.</param>
    public static void WriteStructs(IUpdateEncoder encoder, ClientStructs structs, long client, long clock)
    {
        // Write first id.
        clock = Math.Max(clock, structs[0].Id.Clock);
        int startNewStructs = StructStore.FindIndexSS(structs, clock);

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
        long[] rentedClients = null;
        long[] rentedClocks = null;
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

            // Write # states that were updated.
            encoder.RestWriter.WriteVarUint((uint)count);

            // Write items with higher client ids first.
            // This heavily improves the conflict resolution algorithm.
            clients[..count].Sort(clocks[..count]);
            for (var i = count - 1; i >= 0; i--)
            {
                WriteStructs(encoder, clientStructs[clients[i]], clients[i], clocks[i]);
            }
        }
        finally
        {
            if (rentedClients != null)
            {
                BufferPool<long>.Return(rentedClients);
                BufferPool<long>.Return(rentedClocks);
            }
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
        IUpdateEncoder encoder = null;
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
        IUpdateEncoder encoder = null;
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

    internal static void ReturnEncoder(IUpdateEncoder encoder)
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
    /// The structs of an update grouped by client, each list in clock order (Yjs' <c>readClientsStructRefs</c>).
    /// Parents stay unresolved (a root type's name or the id of the parent's item) until the struct is
    /// integrated, so reading changes no document. See <see cref="StructCursor"/> for what is checked.
    /// </summary>
    public static Dictionary<long, List<AbstractStruct>> ReadClientStructRefs(IUpdateDecoder decoder)
    {
        var clientRefs = new Dictionary<long, List<AbstractStruct>>();
        ReadClientStructRefs(decoder, clientRefs, seenClients: null, spareLists: null);
        return clientRefs;
    }

    /// <summary>
    /// <see cref="ReadClientStructRefs(IUpdateDecoder)"/> into the empty <paramref name="clientRefs"/>,
    /// with an empty <paramref name="seenClients"/> to find a client listed twice, and the lists taken
    /// from <paramref name="spareLists"/> while it has any. On an exception, the lists taken are in
    /// <paramref name="clientRefs"/>. An empty <paramref name="staging"/> is filled with the same structs.
    /// </summary>
    public static void ReadClientStructRefs(
        IUpdateDecoder decoder,
        Dictionary<long, List<AbstractStruct>> clientRefs,
        HashSet<long> seenClients,
        Stack<List<AbstractStruct>> spareLists,
        StagingTable staging = null)
    {
        Debug.Assert(clientRefs.Count == 0);
        var cursor = new StructCursor(decoder, seenClients, staging);
        List<AbstractStruct> refs = null;

        while (cursor.Next() is { } str)
        {
            if (refs == null || refs[0].Id.Client != str.Id.Client)
            {
                if (spareLists == null || !spareLists.TryPop(out refs))
                {
                    refs = new List<AbstractStruct>();
                }

                clientRefs[str.Id.Client] = refs;
            }

            refs.Add(str);
        }
    }

    /// <summary>
    /// Reads the struct section of an update one struct at a time, checking everything that can be
    /// checked without a document: every value within its range, no empty struct, no struct ending
    /// beyond <see cref="Limits.MaxClock"/>, no client listed twice, an origin or parent of the same
    /// client only at an earlier clock (it must have existed when the struct was created), and no more
    /// structs than <see cref="IUpdateDecoder.MaxStructs"/>. Throws <see cref="YUpdateException"/>.
    /// A mutable struct: keep it in a local or a non-readonly field.
    /// </summary>
    internal struct StructCursor
    {
        private readonly IUpdateDecoder _decoder;
        private readonly long _sections;
        private readonly HashSet<long> _clients;
        private StagingTable _staging;
        private long _section;
        private long _structsLeft;
        private long _structCount;
        private long _client;
        private long _clock;
        private object[] _values;

        /// <param name="seenClients">An empty set to find a client listed twice; one is created when needed if null.</param>
        /// <param name="staging">
        /// An empty table that shadows the structs read: one client range per section, one staged struct per object.
        /// The cursor only appends; the table's owner resets it.
        /// </param>
        public StructCursor(IUpdateDecoder decoder, HashSet<long> seenClients = null, StagingTable staging = null)
        {
            _decoder = decoder;
            _staging = staging;
            _sections = decoder.Reader.ReadVarUint();
            _clients = _sections > 1 ? seenClients ?? new HashSet<long>() : null;
        }

        /// <summary>Stages the structs from here on into <paramref name="staging"/>, which has begun the open client's range.</summary>
        public void UseStaging(StagingTable staging) => _staging = staging;

        /// <summary>The next struct, or null after the last.</summary>
        public AbstractStruct Next()
        {
            while (_structsLeft == 0)
            {
                if (_section == _sections)
                {
                    return null;
                }

                _section++;
                ReadSectionHeader();
            }

            _structsLeft--;
            var str = ReadStruct(_decoder, _client, _clock);
            if (_staging != null)
            {
                StructStaging.Stage(_staging, str);
            }

            _clock += str.Length;
            return str;
        }

        /// <summary>
        /// The next struct staged straight from the decoder, as <see cref="Next"/> checks and reads it but without the
        /// struct object: a String's characters come from <see cref="IUpdateDecoder.ReadStringSpan"/>, a Type's reference
        /// and name are read as <see cref="ContentType.Read"/> does. Returns its index in the table, or -1 after the last.
        /// The table is the one given to the constructor; the cursor appends only.
        /// </summary>
        public int NextStaged()
        {
            while (_structsLeft == 0)
            {
                if (_section == _sections)
                {
                    return -1;
                }

                _section++;
                ReadSectionHeader();
            }

            _structsLeft--;
            var i = StageStruct(_decoder, _staging, _client, _clock, ref _values);
            _clock += _staging.At(i).Length;
            return i;
        }

        private void ReadSectionHeader()
        {
            long numberOfStructs = _decoder.Reader.ReadVarUint();
            var client = Limits.CheckClient(_decoder.ReadClient());
            var clock = Limits.CheckClock(_decoder.Reader.ReadVarUint());

            if (_clients != null && !_clients.Add(client))
            {
                throw new YUpdateException($"The update lists client {client} more than once.");
            }

            if (numberOfStructs > _decoder.MaxStructs - _structCount)
            {
                throw new YUpdateException($"The update holds more than {_decoder.MaxStructs} structs (UpdateLimits.MaxStructsPerUpdate).");
            }

            _structCount += numberOfStructs;
            if (numberOfStructs > 0)
            {
                _staging?.BeginClient(client, clock);
            }

            _structsLeft = numberOfStructs;
            _client = client;
            _clock = clock;
        }
    }

    private static AbstractStruct ReadStruct(IUpdateDecoder decoder, long client, long clock)
    {
        var id = new ID(client, clock);
        var info = decoder.ReadInfo();
        AbstractStruct str;

        switch (info & Bits.Bits5)
        {
            case GC.StructGCRefNumber:
                str = new GC(id, decoder.ReadLength());
                break;
            case Skip.StructSkipRefNumber:
                // A range of clocks this update has no data for (written by mergeUpdates).
                var skipLength = decoder.Reader.ReadVarUint();
                Limits.CheckRange(clock, skipLength);
                str = new Skip(id, (int)skipLength);
                break;
            default:
                // The items that were originally to the left and to the right of this item.
                var leftOrigin = (info & Bit.Bit8) == Bit.Bit8 ? (ID?)CheckEarlier(decoder.ReadLeftId(), id) : null;
                var rightOrigin = (info & Bit.Bit7) == Bit.Bit7 ? (ID?)CheckEarlier(decoder.ReadRightId(), id) : null;

                // Without origins the parent is given: a root type by name, or the item of a nested type.
                object parent = null;
                string parentSub = null;
                if ((info & (Bit.Bit7 | Bit.Bit8)) == 0)
                {
                    parent = decoder.ReadParentInfo() ? decoder.ReadString() : CheckEarlier(decoder.ReadLeftId(), id);
                    parentSub = (info & Bit.Bit6) == Bit.Bit6 ? decoder.ReadString() : null;
                }

                str = new Item(id, leftOrigin, rightOrigin, parent, parentSub, ReadItemContent(decoder, info));
                break;
        }

        Limits.CheckRange(clock, str.Length);
        return str;
    }

    // ReadStruct, into the staging table. The reads and the checks are in ReadStruct's order, so a damaged update fails
    // with the same exception at the same point; the struct is added once it is read whole, when its length is known.
    private static int StageStruct(IUpdateDecoder decoder, StagingTable table, long client, long clock, ref object[] values)
    {
        var id = new ID(client, clock);
        var info = decoder.ReadInfo();
        switch (info & Bits.Bits5)
        {
            case GC.StructGCRefNumber:
                var gcLength = decoder.ReadLength();
                Limits.CheckRange(clock, gcLength);
                return table.Add(StagedKind.GC, gcLength);
            case Skip.StructSkipRefNumber:
                // A range of clocks this update has no data for (written by mergeUpdates).
                var skipLength = decoder.Reader.ReadVarUint();
                Limits.CheckRange(clock, skipLength);
                return table.Add(StagedKind.Skip, (int)skipLength);
        }

        var leftOrigin = (info & Bit.Bit8) == Bit.Bit8 ? (ID?)CheckEarlier(decoder.ReadLeftId(), id) : null;
        var rightOrigin = (info & Bit.Bit7) == Bit.Bit7 ? (ID?)CheckEarlier(decoder.ReadRightId(), id) : null;

        string parentName = null;
        ID? parentId = null;
        string parentSub = null;
        if ((info & (Bit.Bit7 | Bit.Bit8)) == 0)
        {
            if (decoder.ReadParentInfo())
            {
                parentName = decoder.ReadString();
            }
            else
            {
                parentId = CheckEarlier(decoder.ReadLeftId(), id);
            }

            parentSub = (info & Bit.Bit6) == Bit.Bit6 ? decoder.ReadString() : null;
        }

        int i;
        switch (info & Bits.Bits5)
        {
            case 1:
                var deleted = decoder.ReadLength();
                Limits.CheckRange(clock, deleted);
                i = table.Add(StagedKind.Deleted, deleted, leftOrigin, rightOrigin);
                break;
            case 4:
                var text = decoder.ReadStringSpan();
                Limits.CheckRange(clock, text.Length);
                i = table.Add(StagedKind.String, text.Length, leftOrigin, rightOrigin);
                text.CopyTo(table.Chars(i));
                break;
            case 8:
                var count = decoder.ReadLength();
                values ??= new object[16];
                for (var v = 0; v < count; v++)
                {
                    if (v == values.Length)
                    {
                        Array.Resize(ref values, v * 2);
                    }

                    values[v] = decoder.ReadAny();
                }

                Limits.CheckRange(clock, count);
                i = table.Add(StagedKind.Any, count, leftOrigin, rightOrigin);
                values.AsSpan(0, count).CopyTo(table.Values(i));
                Array.Clear(values, 0, count);
                break;
            case 7:
                var typeRef = ContentType.ReadHeader(decoder, out var typeName);
                Limits.CheckRange(clock, 1);
                i = table.Add(StagedKind.Type, 1, leftOrigin, rightOrigin);
                table.SetType(i, typeRef, typeName);
                break;
            case 2 or 3 or 5 or 6 or 9:
                var cold = ReadItemContent(decoder, info);
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

    private static ID CheckEarlier(ID reference, ID id)
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
        long[] rentedClients = null;
        long[] rentedClocks = null;
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
                BufferPool<long>.Return(rentedClocks);
            }
        }
    }

    /// <summary>Writes the state vector of <paramref name="store"/> without building it.</summary>
    public static void WriteStateVector(IDSEncoder encoder, StructStore store)
    {
        var clientStructs = store.Clients;
        var capacity = clientStructs.Count;
        long[] rentedClients = null;
        long[] rentedClocks = null;
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
                BufferPool<long>.Return(rentedClocks);
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

    public static IDictionary<long, long> ReadStateVector(IDSDecoder decoder)
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
        return ReadStateVector(new DSDecoderV2(input));
    }

    /// <summary>
    /// A state vector to encode against, decoded from a span into a dictionary reused on this thread
    /// (with the reader and a pooled copy of the input), so encoding allocates nothing for it.
    /// </summary>
    private sealed class TargetStateVector
    {
        [ThreadStatic]
        private static TargetStateVector t_free;

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
                BufferPool<byte>.Return(buffer);
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
