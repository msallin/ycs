// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

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
    public static void WriteStructs(IUpdateEncoder encoder, IList<AbstractStruct> structs, long client, long clock)
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

    public static void WriteClientsStructs(IUpdateEncoder encoder, StructStore store, IDictionary<long, long> _sm)
    {
        // We filter all valid _sm entries into sm.
        var sm = new Dictionary<long, long>();
        foreach (var kvp in _sm)
        {
            var client = kvp.Key;
            var clock = kvp.Value;

            // Only write if new structs are available.
            if (store.GetState(client) > clock)
            {
                sm[client] = clock;
            }
        }

        foreach (var kvp in store.GetStateVector())
        {
            var client = kvp.Key;
            if (!_sm.ContainsKey(client))
            {
                sm[client] = 0;
            }
        }

        // Write # states that were updated.
        encoder.RestWriter.WriteVarUint((uint)sm.Count);

        // Write items with higher client ids first.
        // This heavily improves the conflict resolution algorithm.
        var sortedClients = sm.Keys.ToList();
        sortedClients.Sort((a, b) => b.CompareTo(a));

        foreach (var client in sortedClients)
        {
            WriteStructs(encoder, store.Clients[client], client, sm[client]);
        }
    }

    /// <summary>
    /// The structs of an update grouped by client, each list in clock order (Yjs' <c>readClientsStructRefs</c>).
    /// Parents stay unresolved (a root type's name or the id of the parent's item) until the struct is
    /// integrated, so reading changes no document.
    /// </summary>
    public static Dictionary<long, List<AbstractStruct>> ReadClientStructRefs(IUpdateDecoder decoder)
    {
        var clientRefs = new Dictionary<long, List<AbstractStruct>>();
        List<AbstractStruct> refs = null;

        foreach (var str in ReadStructSection(decoder))
        {
            if (refs == null || refs[0].Id.Client != str.Id.Client)
            {
                refs = new List<AbstractStruct>();
                clientRefs[str.Id.Client] = refs;
            }

            refs.Add(str);
        }

        return clientRefs;
    }

    /// <summary>
    /// Reads the struct section of an update one struct at a time, checking everything that can be
    /// checked without a document: every value within its range, no empty struct, no struct ending
    /// beyond <see cref="Limits.MaxClock"/>, no client listed twice, an origin or parent of the same
    /// client only at an earlier clock (it must have existed when the struct was created), and no more
    /// structs than <see cref="IUpdateDecoder.MaxStructs"/>. Throws <see cref="YUpdateException"/>.
    /// </summary>
    public static IEnumerable<AbstractStruct> ReadStructSection(IUpdateDecoder decoder)
    {
        var numOfStateUpdates = decoder.Reader.ReadVarUint();
        var clients = numOfStateUpdates > 1 ? new HashSet<long>() : null;
        long structCount = 0;

        for (long i = 0; i < numOfStateUpdates; i++)
        {
            long numberOfStructs = decoder.Reader.ReadVarUint();
            var client = Limits.CheckClient(decoder.ReadClient());
            var clock = Limits.CheckClock(decoder.Reader.ReadVarUint());

            if (clients != null && !clients.Add(client))
            {
                throw new YUpdateException($"The update lists client {client} more than once.");
            }

            if (numberOfStructs > decoder.MaxStructs - structCount)
            {
                throw new YUpdateException($"The update holds more structs than its size allows ({decoder.MaxStructs}).");
            }

            structCount += numberOfStructs;

            for (long j = 0; j < numberOfStructs; j++)
            {
                var str = ReadStruct(decoder, client, clock);
                clock += str.Length;
                yield return str;
            }
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

                str = new Item(id, null, leftOrigin, null, rightOrigin, parent, parentSub, ReadItemContent(decoder, info));
                break;
        }

        Limits.CheckRange(clock, str.Length);
        return str;
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
        encoder.RestWriter.WriteVarUint((uint)sv.Count);

        // Highest client id first, as Yjs' writeStateVector sorts, so the same state has the same bytes.
        foreach (var kvp in sv.OrderByDescending(kvp => kvp.Key))
        {
            var client = kvp.Key;
            var clock = kvp.Value;

            encoder.RestWriter.WriteVarUint((uint)client);
            encoder.RestWriter.WriteVarUint((uint)clock);
        }
    }

    public static IDictionary<long, long> ReadStateVector(IDSDecoder decoder)
    {
        var ssLength = decoder.Reader.ReadVarUint();

        // Each entry takes at least two bytes.
        var ss = new Dictionary<long, long>(Limits.Capacity(ssLength, decoder.Reader.Remaining / 2));

        for (long i = 0; i < ssLength; i++)
        {
            var client = decoder.Reader.ReadVarUint();
            var clock = Limits.CheckClock(decoder.Reader.ReadVarUint());
            ss[client] = clock;
        }

        return ss;
    }

    public static IDictionary<long, long> DecodeStateVector(Lib0Reader input)
    {
        return ReadStateVector(new DSDecoderV2(input));
    }
}
