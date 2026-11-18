#nullable enable

using System.Buffers;

namespace Ycs;

/// <summary>
/// Operations on encoded updates that need no document: merging, diffing against a state vector, converting between the
/// formats, reading the state vector or the clock ranges, decoding and describing (Yjs' <c>src/utils/updates.js</c>).
/// Every method reads its input once, bounded by <paramref name="limits"/>
/// (<see cref="UpdateLimits.Default"/> when null; <see cref="UpdateLimits.MaxStructsPerUpdate"/> bounds each input), and
/// throws <see cref="YUpdateException"/> for input that is malformed or beyond a limit. The decoders read arrays, so a
/// span input is copied into a buffer of the shared array pool for the call: no allocation beyond the pool's.
/// </summary>
public static class YUpdate
{
    /// <inheritdoc cref="Merge(ReadOnlySpan{ReadOnlyMemory{byte}}, UpdateFormat, UpdateLimits)"/>
    public static byte[] Merge(IEnumerable<ReadOnlyMemory<byte>> updates, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        return Merge(updates.ToArray(), format, limits);
    }

    /// <inheritdoc cref="Merge(ReadOnlySpan{ReadOnlyMemory{byte}}, UpdateFormat, UpdateLimits)"/>
    public static byte[] Merge(IEnumerable<byte[]> updates, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        return Merge(updates.Select(static u => (ReadOnlyMemory<byte>)u).ToArray(), format, limits);
    }

    /// <summary>
    /// Merges updates into one. The result is associative and idempotent, and can be fed updates an earlier merge produced.
    /// As in Yjs, not where one update holds a GC struct and another an item for the same clock: the struct kept then
    /// depends on the order and the grouping. A single update is returned as a copy, unchecked.
    /// </summary>
    public static byte[] Merge(ReadOnlySpan<ReadOnlyMemory<byte>> updates, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        if (updates.Length == 1)
        {
            return updates[0].ToArray();
        }

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

            return UpdateOperations.MergeUpdates(segments, v2, limits);
        }
        finally
        {
            for (var i = 0; i < copied; i++)
            {
                copies[i].Dispose();
            }
        }
    }

    /// <summary>Extracts the part of <paramref name="update"/> that the peer with the encoded <paramref name="stateVector"/> lacks.</summary>
    public static byte[] Diff(ReadOnlySpan<byte> update, ReadOnlySpan<byte> stateVector, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        using var input = new PooledCopy(update);
        using var vector = new PooledCopy(stateVector);
        return UpdateOperations.DiffUpdate(input.Segment, vector.Segment, v2, limits!);
    }

    /// <summary>
    /// Re-encodes an update in another format: the same information, but for a delete set whose ranges of a client are
    /// out of order or overlap (valid v1, which v2 cannot hold), which is sorted and merged. With equal formats, the
    /// update is re-encoded canonically.
    /// </summary>
    public static byte[] Convert(ReadOnlySpan<byte> update, UpdateFormat from, UpdateFormat to, UpdateLimits? limits = null)
    {
        var fromV2 = from.IsV2();
        var toV2 = to.IsV2();
        using var input = new PooledCopy(update);
        return UpdateOperations.ConvertUpdateFormat(input.Segment, fromV2, toV2, limits!);
    }

    /// <summary>The state vector an update implies, without applying it to a document (both formats encode a state vector alike).</summary>
    public static byte[] EncodeStateVector(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        using var input = new PooledCopy(update);
        return UpdateOperations.EncodeStateVectorFromUpdate(input.Segment, v2, limits!);
    }

    /// <summary>The clock range every client of an update covers.</summary>
    public static UpdateMeta ParseMeta(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        using var input = new PooledCopy(update);
        return UpdateOperations.ParseUpdateMeta(input.Segment, v2, limits!);
    }

    /// <summary>The structs and the delete set of an update. Output is in proportion to the structs, which a v2 update describes in a few bytes.</summary>
    public static DecodedUpdate Decode(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        using var input = new PooledCopy(update);
        return UpdateOperations.DecodeUpdate(input.Segment, v2, limits!);
    }

    /// <summary>The structs and the delete set of an update as plain text, for debugging.</summary>
    public static string Describe(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        using var input = new PooledCopy(update);
        return UpdateOperations.DescribeUpdate(input.Segment, v2, limits!);
    }
}

/// <summary>A span copied into an array of the shared pool, for the decoders that read arrays. Cleared and returned by <see cref="Dispose"/>.</summary>
internal readonly struct PooledCopy : IDisposable
{
    private readonly byte[] _array;
    private readonly int _count;

    public PooledCopy(ReadOnlySpan<byte> source)
    {
        _array = ArrayPool<byte>.Shared.Rent(source.Length);
        _count = source.Length;
        source.CopyTo(_array);
    }

    public ArraySegment<byte> Segment => new(_array, 0, _count);

    public void Dispose()
    {
        _array.AsSpan(0, _count).Clear();
        ArrayPool<byte>.Shared.Return(_array);
    }
}
