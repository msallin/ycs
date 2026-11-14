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

    /// <summary>
    /// One entry per client written so far: how many structs, and where the bytes that client's
    /// structs wrote to <see cref="IUpdateEncoder.RestWriter"/> end (they start where the previous
    /// entry's end; they are everything past the struct-count, i.e. the start clock plus any inline
    /// content bytes).
    /// </summary>
    public List<(int Written, int End)> ClientStructs { get; }

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
