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
/// (<see cref="Finish"/>). <see cref="IUpdateEncoder.RestWriter"/> only exposes a getter here
/// (its setter is private in the concrete encoders, which we don't own), so instead of
/// swapping the stream reference we truncate the SAME <see cref="MemoryStream"/> back to
/// empty after capturing its bytes - the concrete encoders always back <c>RestWriter</c> with a
/// <see cref="MemoryStream"/>, so this is safe.
/// </summary>
internal sealed class LazyStructWriter
{
    public LazyStructWriter(IUpdateEncoder encoder)
    {
        Encoder = encoder;
        CurrClient = 0;
        Written = 0;
        ClientStructs = new List<(int Written, byte[] Fragment)>();
    }

    public IUpdateEncoder Encoder { get; }
    public long CurrClient { get; set; }
    public int Written { get; set; }

    /// <summary>
    /// One entry per client written so far: how many structs, and the raw bytes that client's
    /// structs wrote to <see cref="IUpdateEncoder.RestWriter"/> (everything past the
    /// struct-count, i.e. the start clock plus any inline content bytes).
    /// </summary>
    public List<(int Written, byte[] Fragment)> ClientStructs { get; }

    /// <summary>
    /// Writes one struct (with the given clock offset into it) to the lazy writer, starting a
    /// new client fragment if needed.
    /// </summary>
    public static void WriteStruct(LazyStructWriter lazyWriter, AbstractStruct str, int offset)
    {
        // Flush curr if we start another client.
        if (lazyWriter.Written > 0 && lazyWriter.CurrClient != str.Id.Client)
        {
            Flush(lazyWriter);
        }

        if (lazyWriter.Written == 0)
        {
            lazyWriter.CurrClient = str.Id.Client;

            // Write next client.
            lazyWriter.Encoder.WriteClient(str.Id.Client);

            // Write startClock.
            lazyWriter.Encoder.RestWriter.WriteVarUint((uint)(str.Id.Clock + offset));
        }

        WriteStructForLazyOps(str, lazyWriter.Encoder, offset);
        lazyWriter.Written++;
    }

    /// <summary>
    /// Call this once all structs have been written, to assemble the final
    /// '# clients, then (count, fragment) per client' layout into the encoder.
    /// </summary>
    public static void Finish(LazyStructWriter lazyWriter)
    {
        Flush(lazyWriter);

        // This is a fresh (empty) MemoryStream, because 'Flush' just truncated it.
        var restWriter = lazyWriter.Encoder.RestWriter;

        // Write # states that were updated - i.e. the clients.
        restWriter.WriteVarUint((uint)lazyWriter.ClientStructs.Count);

        foreach (var part in lazyWriter.ClientStructs)
        {
            // Write # encoded structs.
            restWriter.WriteVarUint((uint)part.Written);

            // Write the rest of the fragment.
            restWriter.Write(part.Fragment, 0, part.Fragment.Length);
        }
    }

    private static void Flush(LazyStructWriter lazyWriter)
    {
        if (lazyWriter.Written <= 0)
        {
            return;
        }

        // The concrete encoders (DSEncoderV1/V2) always back 'RestWriter' with a MemoryStream.
        var restWriter = (MemoryStream)lazyWriter.Encoder.RestWriter;
        var fragment = restWriter.ToArray();

        lazyWriter.ClientStructs.Add((lazyWriter.Written, fragment));

        // Truncate back to empty so unrelated later writes (the next client's fragment, or -
        // once we're done - the delete set) don't see these bytes again.
        restWriter.SetLength(0);
        restWriter.Position = 0;

        lazyWriter.Written = 0;
    }

    /// <summary>
    /// Dispatches to <see cref="AbstractStruct.Write"/> for GC/Skip (whose Write is already
    /// correct), but uses <see cref="WriteItemForLazyOps"/> for Item: structs produced by
    /// <see cref="LazyStructReader"/> may carry an unresolved parent (a key string or an ID
    /// rather than an <see cref="AbstractType"/>), which <see cref="Item.Write"/> - a file we
    /// don't own - does not know how to serialize.
    /// </summary>
    private static void WriteStructForLazyOps(AbstractStruct str, IUpdateEncoder encoder, int offset)
    {
        if (str is Item item)
        {
            WriteItemForLazyOps(item, encoder, offset);
        }
        else
        {
            str.Write(encoder, offset);
        }
    }

    /// <summary>
    /// Re-implements <see cref="Item.Write"/> for the lazy-struct-reading path, adding the
    /// case (marked in real Yjs as "this edge case was added by differential updates") where
    /// an item at the start of its type has an unresolved parent: a raw key string (this
    /// item's type lives directly in the document's root map) or the ID of the item that
    /// defines the parent type, rather than a resolved <see cref="AbstractType"/>.
    /// </summary>
    private static void WriteItemForLazyOps(Item item, IUpdateEncoder encoder, int offset)
    {
        var origin = offset > 0 ? (ID?)new ID(item.Id.Client, item.Id.Clock + offset - 1) : item.LeftOrigin;
        var rightOrigin = item.RightOrigin;
        var parentSub = item.ParentSub;
        var info = (item.Content.Ref & Bits.Bits5) |
            (origin == null ? 0 : Bit.Bit8) |
            (rightOrigin == null ? 0 : Bit.Bit7) |
            (parentSub == null ? 0 : Bit.Bit6);
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
            switch (item.Parent)
            {
                case string yKey:
                    encoder.WriteParentInfo(true);
                    encoder.WriteString(yKey);
                    break;
                case ID parentId:
                    encoder.WriteParentInfo(false);
                    encoder.WriteLeftId(parentId);
                    break;
                default:
                    // Structs flowing through UpdateOperations always originate from
                    // LazyStructReader (or from slicing/merging such structs), which never
                    // resolves 'parent' to an AbstractType - so this should be unreachable.
                    throw new InvalidOperationException(
                        "Item has an unresolved parent that is neither a key string nor an ID.");
            }

            if (parentSub != null)
            {
                encoder.WriteString(parentSub);
            }
        }

        item.Content.Write(encoder, offset);
    }
}
