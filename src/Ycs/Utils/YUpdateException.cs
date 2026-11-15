namespace Ycs;

/// <summary>
/// Thrown when encoded Yjs data (an update, a state vector, a delete set, a snapshot or a relative
/// position) is malformed, or exceeds a limit of this library. Applying, merging, diffing or decoding
/// such data throws nothing else, and an update that fails to apply leaves the document as it was
/// (unless <see cref="PartlyApplied"/>): the caller can drop the update (and, if it came from a peer,
/// the peer) and continue.
/// </summary>
public sealed class YUpdateException : FormatException
{
    public YUpdateException()
        : base("The encoded Yjs data is malformed.")
    {
    }

    public YUpdateException(string message)
        : base(message)
    {
    }

    public YUpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// True when <see cref="UpdateLimits.MaxIntegrationSteps"/> ran out while the update was integrated
    /// (the only limit that is not checked before the document changes): the structs integrated before
    /// stay in the document, observers and <see cref="YDoc.Update"/> see them, and the rest of the update
    /// and its deletions are dropped. What stays is a causally complete part of the update, as if the
    /// peer had sent less, so the document stays valid and converges with any peer that has the same.
    /// </summary>
    public bool PartlyApplied { get; init; }

    /// <summary>
    /// Runs a decoding step and reports any failure as a <see cref="YUpdateException"/>. Only for code
    /// that does not change a document: what it throws is a consequence of the input.
    /// </summary>
    internal static T Decode<T>(Func<T> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception e) when (IsDecodingFailure(e))
        {
            throw Wrap(e);
        }
    }

    internal static bool IsDecodingFailure(Exception e) => e is not (YUpdateException or OutOfMemoryException or InsufficientExecutionStackException);

    internal static YUpdateException Wrap(Exception e) => e as YUpdateException ?? new YUpdateException($"The encoded Yjs data is malformed ({e.GetType().Name}).", e);
}
