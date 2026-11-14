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
/// (<see cref="Finish"/>). <see cref="IUpdateEncoder.RestWriter"/> only exposes a getter, so
/// instead of swapping the writer we clear the same <see cref="Lib0Writer"/> after capturing
/// its bytes.
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

        str.Write(lazyWriter.Encoder, offset);
        lazyWriter.Written++;
    }

    /// <summary>
    /// Call this once all structs have been written, to assemble the final
    /// '# clients, then (count, fragment) per client' layout into the encoder.
    /// </summary>
    public static void Finish(LazyStructWriter lazyWriter)
    {
        Flush(lazyWriter);

        // This is empty, because 'Flush' just cleared it.
        var restWriter = lazyWriter.Encoder.RestWriter;

        // Write # states that were updated - i.e. the clients.
        restWriter.WriteVarUint((uint)lazyWriter.ClientStructs.Count);

        foreach (var part in lazyWriter.ClientStructs)
        {
            // Write # encoded structs.
            restWriter.WriteVarUint((uint)part.Written);

            // Write the rest of the fragment.
            restWriter.WriteBytes(part.Fragment);
        }
    }

    private static void Flush(LazyStructWriter lazyWriter)
    {
        if (lazyWriter.Written <= 0)
        {
            return;
        }

        var restWriter = lazyWriter.Encoder.RestWriter;
        var fragment = restWriter.ToArray();

        lazyWriter.ClientStructs.Add((lazyWriter.Written, fragment));

        // Truncate back to empty so unrelated later writes (the next client's fragment, or -
        // once we're done - the delete set) don't see these bytes again.
        restWriter.Clear();

        lazyWriter.Written = 0;
    }
}
