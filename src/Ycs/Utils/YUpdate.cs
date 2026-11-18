#nullable enable

using System.Buffers;

namespace Ycs;

/// <summary>
/// Operations on encoded updates that need no document: merging, diffing against a state vector, converting between the
/// formats, reading the state vector or the clock ranges, decoding and describing (Yjs' <c>src/utils/updates.js</c>).
/// Every method reads its input once, bounded by <paramref name="limits"/>
/// (<see cref="UpdateLimits.Default"/> when null; <see cref="UpdateLimits.MaxStructsPerUpdate"/> bounds each input), and
/// throws <see cref="YUpdateException"/> for input that is malformed or beyond a limit. The input is read in place and
/// nothing keeps it after the call. A form that takes an <see cref="IBufferWriter{T}"/> writes it only after the whole input
/// was read, so a malformed input throws with nothing written, and the writer may share memory with the input.
/// </summary>
public static class YUpdate
{
    /// <inheritdoc cref="Merge(ReadOnlySpan{ReadOnlyMemory{byte}}, UpdateFormat, UpdateLimits)"/>
    public static byte[] Merge(IEnumerable<ReadOnlyMemory<byte>> updates, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        return Merge(updates.ToArray(), format, limits);
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

        return UpdateOperations.MergeUpdates(updates, v2, limits);
    }

    /// <summary>Merges updates into one, written to <paramref name="destination"/>; as <see cref="Merge(ReadOnlySpan{ReadOnlyMemory{byte}}, UpdateFormat, UpdateLimits)"/> otherwise.</summary>
    public static void Merge(IBufferWriter<byte> destination, ReadOnlySpan<ReadOnlyMemory<byte>> updates, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        UpdateOperations.MergeUpdates(destination, updates, format.IsV2(), limits);
    }

    /// <summary>Extracts the part of <paramref name="update"/> that the peer with the encoded <paramref name="stateVector"/> lacks.</summary>
    public static byte[] Diff(ReadOnlySpan<byte> update, ReadOnlySpan<byte> stateVector, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        return UpdateOperations.DiffUpdate(update, stateVector, v2, limits);
    }

    /// <summary>Extracts the part of <paramref name="update"/> that <paramref name="stateVector"/> lacks, written to <paramref name="destination"/>.</summary>
    public static void Diff(IBufferWriter<byte> destination, ReadOnlySpan<byte> update, ReadOnlySpan<byte> stateVector, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        UpdateOperations.DiffUpdate(destination, update, stateVector, format.IsV2(), limits);
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
        return UpdateOperations.ConvertUpdateFormat(update, fromV2, toV2, limits);
    }

    /// <summary>Re-encodes an update in another format, written to <paramref name="destination"/>; as <see cref="Convert(ReadOnlySpan{byte}, UpdateFormat, UpdateFormat, UpdateLimits)"/> otherwise.</summary>
    public static void Convert(IBufferWriter<byte> destination, ReadOnlySpan<byte> update, UpdateFormat from, UpdateFormat to, UpdateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        UpdateOperations.ConvertUpdateFormat(destination, update, from.IsV2(), to.IsV2(), limits);
    }

    /// <summary>The state vector an update implies, without applying it to a document (both formats encode a state vector alike).</summary>
    public static byte[] EncodeStateVector(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        return UpdateOperations.EncodeStateVectorFromUpdate(update, v2, limits);
    }

    /// <summary>The state vector an update implies, written to <paramref name="destination"/>.</summary>
    public static void EncodeStateVector(IBufferWriter<byte> destination, ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        UpdateOperations.EncodeStateVectorFromUpdate(destination, update, format.IsV2(), limits);
    }

    /// <summary>The clock range every client of an update covers.</summary>
    public static UpdateMeta ParseMeta(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        return UpdateOperations.ParseUpdateMeta(update, v2, limits);
    }

    /// <summary>The structs and the delete set of an update. Output is in proportion to the structs, which a v2 update describes in a few bytes.</summary>
    public static DecodedUpdate Decode(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        return UpdateOperations.DecodeUpdate(update, v2, limits);
    }

    /// <summary>The structs and the delete set of an update as plain text, for debugging.</summary>
    public static string Describe(ReadOnlySpan<byte> update, UpdateFormat format = UpdateFormat.V1, UpdateLimits? limits = null)
    {
        var v2 = format.IsV2();
        return UpdateOperations.DescribeUpdate(update, v2, limits);
    }
}
