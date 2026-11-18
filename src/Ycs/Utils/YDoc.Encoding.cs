#nullable enable

using System.Buffers;

namespace Ycs;

public sealed partial class YDoc
{
    /// <summary>
    /// Applies an update (Yjs' <c>Y.applyUpdate</c>, <c>Y.applyUpdateV2</c>). The transaction it runs in is not local, as
    /// in Yjs, also when it is the caller's. The span is read in place; nothing keeps a reference to it afterwards.
    /// <br/>
    /// A malformed update, or one beyond a limit, throws <see cref="YUpdateException"/> and leaves the
    /// document as it was, except when it was cut short (<see cref="YUpdateException.PartlyApplied"/>: beyond
    /// <see cref="UpdateLimits.MaxIntegrationSteps"/>, or its own structs waited for waiting structs that were
    /// dropped). Waiting structs of earlier updates that this one lets integrate, and that then fail a check, are
    /// dropped and the update stands whole: <see cref="PendingDropped"/> reports it (docs/api.md,
    /// "Pending structs, retried").
    /// </summary>
    /// <param name="update">The update, in the original (v1) format.</param>
    /// <param name="origin">Stored on the transaction; observers and the undo manager see it.</param>
    /// <param name="limits">For this update instead of <see cref="YDocOptions.Limits"/>, e.g. <see cref="UpdateLimits.Unlimited"/> for a stored state the application trusts.</param>
    /// <exception cref="YUpdateException">The update is malformed or beyond a limit; the document is unchanged unless <see cref="YUpdateException.PartlyApplied"/>.</exception>
    public void ApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateLimits? limits = null) =>
        ApplyUpdate(update, false, origin!, limits!);

    /// <inheritdoc cref="ApplyUpdate(ReadOnlySpan{byte}, object, UpdateLimits)"/>
    /// <param name="format">The encoding of <paramref name="update"/>.</param>
    public void ApplyUpdate(ReadOnlySpan<byte> update, UpdateFormat format, object? origin = null, UpdateLimits? limits = null) =>
        ApplyUpdate(update, format.IsV2(), origin!, limits!);

    /// <summary>
    /// Like <see cref="ApplyUpdate(ReadOnlySpan{byte}, object, UpdateLimits)"/>, but returns false instead of throwing <see cref="YUpdateException"/>; the document
    /// is then unchanged, except when the update was cut short (see <see cref="YUpdateException.PartlyApplied"/>).
    /// </summary>
    public bool TryApplyUpdate(ReadOnlySpan<byte> update, object? origin = null, UpdateLimits? limits = null) =>
        TryApply(update, false, origin!, limits!);

    /// <inheritdoc cref="TryApplyUpdate(ReadOnlySpan{byte}, object, UpdateLimits)"/>
    public bool TryApplyUpdate(ReadOnlySpan<byte> update, UpdateFormat format, object? origin = null, UpdateLimits? limits = null) =>
        TryApply(update, format.IsV2(), origin!, limits!);

    /// <summary>
    /// Encodes the document as one update (Yjs' <c>Y.encodeStateAsUpdate</c>). With a remote state vector, only what the
    /// remote side is missing is written.
    /// </summary>
    /// <param name="stateVector">A state vector from <see cref="EncodeStateVector()"/>; empty for the whole document.</param>
    /// <param name="format">The encoding of the result.</param>
    public byte[] EncodeStateAsUpdate(ReadOnlySpan<byte> stateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        var v2 = format.IsV2();
        MaterializeRunExtensions();
        return EncodingUtils.EncodeStateAsUpdate(Store, stateVector, v2);
    }

    /// <summary>Writes the update <see cref="EncodeStateAsUpdate(ReadOnlySpan{byte}, UpdateFormat)"/> returns into <paramref name="destination"/>.</summary>
    public void EncodeStateAsUpdate(IBufferWriter<byte> destination, ReadOnlySpan<byte> stateVector = default, UpdateFormat format = UpdateFormat.V1)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var v2 = format.IsV2();
        MaterializeRunExtensions();
        EncodingUtils.EncodeStateAsUpdate(Store, destination, stateVector, v2);
    }

    /// <summary>The document's state vector (Yjs' <c>Y.encodeStateVector</c>), the same bytes for both update formats.</summary>
    public byte[] EncodeStateVector() => EncodingUtils.EncodeStateVector(Store);

    /// <summary>Writes the state vector into <paramref name="destination"/>.</summary>
    public void EncodeStateVector(IBufferWriter<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        EncodingUtils.EncodeStateVector(Store, destination);
    }
}
