#nullable enable

namespace Ycs;

/// <summary>What a <see cref="YDoc"/> is made with; the document keeps the values it was made with.</summary>
public sealed record YDocOptions
{
    /// <summary>A random one unless set.</summary>
    public string? Guid { get; init; }

    /// <summary>
    /// Yjs' <c>collectionid</c>: associates the document with a collection of the provider. A
    /// subdocument without one takes its parent's when it is added. Not encoded.
    /// </summary>
    public string? CollectionId { get; init; }

    /// <summary>A subdocument's meta travels with its item.</summary>
    public YValue Meta { get; init; }

    public bool AutoLoad { get; init; }

    /// <summary>Whether the document should be synced by the provider now; <see cref="YDoc.Load"/> sets it. Yjs' default is true.</summary>
    public bool ShouldLoad { get; init; } = true;

    /// <summary>Yjs' <c>gc</c>: whether deleted content is collected. Snapshots need a document that keeps it (false).</summary>
    public bool GarbageCollection { get; init; } = true;

    /// <summary>
    /// Called once per deleted item before it is collected: false keeps it. Only with <see cref="GarbageCollection"/>.
    /// </summary>
    public Func<ItemView, bool>? GarbageCollectionFilter { get; init; }

    /// <summary>
    /// What an update applied to the document may cost; <see cref="YDoc.ApplyUpdate(byte[], object, UpdateLimits)"/> can
    /// override it for one update. A subdocument read from an update takes its parent's.
    /// </summary>
    public UpdateLimits Limits
    {
        get => _limits;
        init => _limits = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// The client id to start with instead of a random one; for tests and reproducible benchmarks.
    /// Two documents that edit concurrently must never share a client id. 0 to 2^53 - 1, as Yjs'
    /// <c>doc.clientID</c> (which it generates as a random 32-bit unsigned integer).
    /// </summary>
    public long? ClientId
    {
        get => _clientId;
        init => _clientId = value is null or >= 0 and <= Ycs.Limits.MaxClient
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "A client id is 0 to 2^53 - 1.");
    }

    private readonly UpdateLimits _limits = UpdateLimits.Default;
    private readonly long? _clientId;

    // What a document made without options holds: the record is immutable, so documents share it.
    internal static readonly YDocOptions Default = new();

    // Collects everything; another filter is called once per item (see Transaction.CleanupTransaction).
    internal static readonly Func<ItemView, bool> DefaultGcFilter = static _ => true;
}
