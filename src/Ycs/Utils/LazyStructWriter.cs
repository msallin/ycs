// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

/// <summary>
/// Writes structs lazily, one at a time, without knowing up front how many structs (or which
/// clients) will end up in the update - which is exactly the situation
/// <see cref="UpdateOperations"/>'s merge/diff/convert algorithms are in.
/// <br/>
/// Ported from Yjs' <c>LazyStructWriter</c> plus its module-private helpers
/// <c>flushLazyStructWriter</c>, <c>writeStructToLazyStructWriter</c>, and
/// <c>finishLazyStructWriting</c> (src/utils/updates.js).
/// <br/>
/// The client/struct-count header for a run of structs belonging to one client can only be
/// written once we know how many structs that client ended up with, but the structs
/// themselves must be written to the encoder's underlying stream as we go. Yjs solves this by
/// swapping out the encoder's 'rest' stream for a fresh one every time the writer moves to a
/// new client, stashing the bytes written so far as a fragment, then re-assembling
/// '# clients, then (count, fragment) per client' once everything is known
/// (<see cref="Finish"/>). Here the fragments stay where they were written, in
/// <see cref="IUpdateEncoder.RestWriter"/>, and only their ends are recorded; <see cref="Finish"/>
/// moves them behind the counts through one pooled copy.
/// </summary>
internal sealed class LazyStructWriter
{
    public LazyStructWriter(IUpdateEncoder encoder)
    {
        Encoder = encoder;
        CurrClient = 0;
        Written = 0;
        ClientStructs = new List<(int Written, int End)>();
    }

    public IUpdateEncoder Encoder { get; }
    public long CurrClient { get; set; }
    public int Written { get; set; }

    /// <summary>How many structs were written, for every client.</summary>
    public long TotalWritten { get; private set; }

    /// <summary>
    /// One entry per client written so far: how many structs, and where the bytes that client's
    /// structs wrote to <see cref="IUpdateEncoder.RestWriter"/> end (they start where the previous
    /// entry's end; they are everything past the struct-count, i.e. the start clock plus any inline
    /// content bytes).
    /// </summary>
    public List<(int Written, int End)> ClientStructs { get; }

    /// <summary>
    /// Writes struct <paramref name="i"/> of <paramref name="table"/> from its staged fields, <paramref name="offset"/>
    /// clocks in, starting a new client fragment if needed. The offset slices as Yjs' <c>write(encoder, offset)</c>
    /// does: an item's origin becomes its own clock before the cut and a String or an Any starts at the offset, with
    /// no replacement of a split surrogate pair (that is <see cref="StructStaging.CopyFrom"/>'s, as Yjs' <c>splice</c>).
    /// </summary>
    public static void WriteStruct(LazyStructWriter lazyWriter, StagingTable table, int i, int offset)
    {
        var id = table.IdOf(i);

        // Flush curr if we start another client.
        if (lazyWriter.Written > 0 && lazyWriter.CurrClient != id.Client)
        {
            Flush(lazyWriter);
        }

        if (lazyWriter.Written == 0)
        {
            lazyWriter.CurrClient = id.Client;

            // Write next client.
            lazyWriter.Encoder.WriteClient(id.Client);

            // Write startClock.
            lazyWriter.Encoder.RestWriter.WriteVarUint((uint)(id.Clock + offset));
        }

        var encoder = lazyWriter.Encoder;
        switch (table.At(i).Kind)
        {
            case StagedKind.GC:
                encoder.WriteInfo(GC.StructGCRefNumber);
                encoder.WriteLength(table.At(i).Length - offset);
                break;
            case StagedKind.Skip:
                encoder.WriteInfo(Skip.StructSkipRefNumber);

                // Write as VarUint because Skips can't make use of predictable length-encoding.
                encoder.RestWriter.WriteVarUint((uint)(table.At(i).Length - offset));
                break;
            default:
                WriteItem(encoder, table, i, id, offset);
                break;
        }

        lazyWriter.Written++;
        lazyWriter.TotalWritten++;
    }

    // Yjs' Item#write: the parent is written only when neither origin is, and an offset makes the left origin the id before it.
    private static void WriteItem(IUpdateEncoder encoder, StagingTable table, int i, ID id, int offset)
    {
        ref readonly var s = ref table.At(i);
        var origin = offset > 0 ? new ID(id.Client, id.Clock + offset - 1) : table.LeftOrigin(i);
        var rightOrigin = table.RightOrigin(i);
        var info = ((int)s.Kind & Bits.Bits5) |
            (origin == null ? 0 : Bit.Bit8) |
            (rightOrigin == null ? 0 : Bit.Bit7) |
            (s.ParentSub < 0 ? 0 : Bit.Bit6);
        encoder.WriteInfo((byte)info);

        if (origin != null)
        {
            encoder.WriteLeftId(origin.Value);
        }

        if (rightOrigin != null)
        {
            encoder.WriteRightId(rightOrigin.Value);
        }

        if (origin == null && rightOrigin == null)
        {
            if (s.ParentKind == StagedParent.Root)
            {
                encoder.WriteParentInfo(true);
                encoder.WriteString(table.ParentName(i));
            }
            else
            {
                encoder.WriteParentInfo(false);
                encoder.WriteLeftId(table.ParentId(i));
            }

            if (s.ParentSub >= 0)
            {
                encoder.WriteString(table.ParentSub(i)!);
            }
        }

        switch (s.Kind)
        {
            case StagedKind.Deleted:
                encoder.WriteLength(s.Length - offset);
                break;
            case StagedKind.String:
                encoder.WriteString(table.Chars(i)[offset..]);
                break;
            case StagedKind.Any:
                var values = table.Values(i);
                encoder.WriteLength(values.Length - offset);
                for (var v = offset; v < values.Length; v++)
                {
                    encoder.WriteAny(values[v]!);
                }

                break;
            case StagedKind.Type:
                encoder.WriteTypeRef((uint)s.TypeRef);
                if (s.TypeRef is YXmlElement.YXmlElementRefId or YXmlHook.YXmlHookRefId)
                {
                    encoder.WriteKey(table.TypeName(i)!);
                }

                break;
            default:
                ((IContentEx)table.Cold(i)).Write(encoder, offset);
                break;
        }
    }

    /// <summary>
    /// Call this once all structs have been written, to assemble the final
    /// '# clients, then (count, fragment) per client' layout into the encoder.
    /// </summary>
    public static void Finish(LazyStructWriter lazyWriter)
    {
        Flush(lazyWriter);

        var restWriter = lazyWriter.Encoder.RestWriter;
        var length = restWriter.Length;
        var fragments = BufferPool<byte>.Rent(length);
        try
        {
            restWriter.WrittenSpan.CopyTo(fragments);
            restWriter.Clear();

            // Write # states that were updated - i.e. the clients.
            restWriter.WriteVarUint((uint)lazyWriter.ClientStructs.Count);

            var start = 0;
            foreach (var (written, end) in lazyWriter.ClientStructs)
            {
                // Write # encoded structs.
                restWriter.WriteVarUint((uint)written);

                // Write the rest of the fragment.
                restWriter.WriteBytes(fragments.AsSpan(start, end - start));
                start = end;
            }
        }
        finally
        {
            BufferPool<byte>.Return(fragments);
        }
    }

    private static void Flush(LazyStructWriter lazyWriter)
    {
        if (lazyWriter.Written <= 0)
        {
            return;
        }

        lazyWriter.ClientStructs.Add((lazyWriter.Written, lazyWriter.Encoder.RestWriter.Length));
        lazyWriter.Written = 0;
    }
}
