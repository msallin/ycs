namespace Ycs;

/// <summary>
/// What one update from a peer may cost a document (<see cref="YDocOptions.Limits"/>, or per call of
/// <see cref="YDoc.ApplyUpdate(byte[], object, UpdateLimits)"/>) or the functions of
/// <see cref="UpdateOperations"/> (their <c>limits</c> parameter). An update beyond a limit is
/// rejected with a <see cref="YUpdateException"/> before anything changes. Immutable, so one instance
/// can be shared by any number of documents and threads.
/// <br/>
/// The defaults bound what an untrusted update can make a document allocate (see
/// <c>docs/api.md</c>, "Untrusted input", for the measured costs); Yjs has none of these limits.
/// An application that applies updates it trusts and that may be larger (its own stored state, say)
/// passes <see cref="Unlimited"/>, or its own instance, for that call.
/// </summary>
public sealed class UpdateLimits
{
    private readonly long _maxStructsPerUpdate = 1 << 17;
    private readonly long _maxPendingStructs = 1 << 17;
    private readonly long _maxPendingBytes = 16L << 20;

    /// <summary>The defaults below.</summary>
    public static UpdateLimits Default { get; } = new();

    /// <summary>No limit at all, as Yjs: for input the application trusts.</summary>
    public static UpdateLimits Unlimited { get; } = new()
    {
        MaxStructsPerUpdate = long.MaxValue,
        MaxPendingStructs = long.MaxValue,
        MaxPendingBytes = long.MaxValue,
    };

    /// <summary>
    /// The most structs (items, GC and Skip structs) one update may hold, in either format. The v2
    /// format run-length encodes its columns, so a few bytes can describe any number of structs, and
    /// a v1 struct can be as small as 4 bytes; each costs 60 bytes to 1.1 KB once decoded. Default
    /// 131,072 (Yjs writes 20,000 deleted items in 42 bytes, so this is six times that); a whole
    /// document's state can hold more, see the class summary. Positive.
    /// </summary>
    public long MaxStructsPerUpdate
    {
        get => _maxStructsPerUpdate;
        init => _maxStructsPerUpdate = Positive(value);
    }

    /// <summary>
    /// The most structs that may wait for structs the document does not have yet (Yjs'
    /// <c>pendingStructs</c>). An update that would add more is rejected; an update that lets pending
    /// structs integrate may bring at most this many new ones, so what waits never exceeds twice
    /// this. Default 131,072. Positive.
    /// </summary>
    public long MaxPendingStructs
    {
        get => _maxPendingStructs;
        init => _maxPendingStructs = Positive(value);
    }

    /// <summary>
    /// The most bytes that may wait for structs the document does not have yet: the waiting structs
    /// in their v2 encoding (which bounds their content: strings, values, keys) and the waiting
    /// deletions in their v1 encoding. With <see cref="MaxPendingStructs"/> it bounds the memory of
    /// what waits, as the struct limit bounds what neither encoding does. Counted and enforced as
    /// that limit. Default 16 MiB. Zero allows nothing to wait.
    /// </summary>
    public long MaxPendingBytes
    {
        get => _maxPendingBytes;
        init => _maxPendingBytes = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit cannot be negative.");
    }

    private static long Positive(long value) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The limit must be positive.");
}
