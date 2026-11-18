namespace Ycs;

/// <summary>
/// Decodes an update (its structs by client into a <see cref="StagingTable"/>, and its delete set) into objects that are
/// reused from one update to the next on the same thread: the key list of the v2 decoder, the staging table and the
/// delete set. The checks are those of <see cref="EncodingUtils.StructCursor"/> and
/// <see cref="DeleteSet.Read"/>.
/// <br/>
/// <see cref="Read"/> hands out a <see cref="Lease"/>; disposing it clears the delete set's lists and the staging table
/// (dropping the references to the input) and makes the reader available again. Thread-local
/// rather than per document: a server holding many documents keeps one per thread. A read while
/// another lease is open on the thread (re-entrancy) gets the second free reader, or a new one: two readers are kept,
/// for the pending update's retry that reads inside the lease of the update that set it aside.
/// </summary>
internal sealed class UpdateReader
{
    /// <summary>A list that grew beyond this is not kept for the next update.</summary>
    private const int MaxRetainedListCapacity = 1024;

    /// <summary>At most this many spare lists of each kind are kept.</summary>
    private const int MaxSpareLists = 64;

    // Two free readers: the reader of a nested read is released before the one it nests in.
    [ThreadStatic]
    private static UpdateReader? t_free;

    [ThreadStatic]
    private static UpdateReader? t_spare;

    [ThreadStatic]
    private static int t_constructed;

    private readonly List<string> _keys = new();
    private readonly HashSet<long> _clients = new();
    private readonly DeleteSet _deleteSet = new();
    private readonly StagingTable _staging = new();
    private readonly Stack<List<DeleteSet.DeleteItem>> _spareDeleteLists = new();
    private UpdateDecoderV1 _v1;
    private UpdateDecoderV2 _v2;
    private int _version;

    /// <summary>
    /// Decodes <paramref name="update"/> in place. Nothing decoded refers to it, not even during the lease. Malformed input throws (as the
    /// decoders do; the caller wraps it in a <see cref="YUpdateException"/>), and the reader is released.
    /// </summary>
    /// <param name="maxStructs">The most structs the update may hold (<see cref="UpdateLimits.MaxStructsPerUpdate"/>).</param>
    /// <param name="maxDepth">How deeply values may be nested (<see cref="UpdateLimits.MaxDepth"/>).</param>
    public static Lease Read(ReadOnlySpan<byte> update, bool v2, long maxStructs, int maxDepth = UpdateLimits.DefaultMaxDepth)
    {
        var reader = t_free;
        t_free = t_spare;
        t_spare = null;
        if (reader == null)
        {
            reader = new UpdateReader();
            t_constructed++;
        }

        var version = reader._version;
        try
        {
            reader.ReadCore(update, v2, maxStructs, maxDepth);
        }
        catch
        {
            reader.Release(version);
            throw;
        }

        return new Lease(reader, version);
    }

    /// <summary>Whether this thread's reader is free, that is no lease of it is open (for tests).</summary>
    internal static bool IsFreeOnCurrentThread => t_free != null;

    /// <summary>How many readers this thread constructed (for tests).</summary>
    internal static int ConstructedOnCurrentThread => t_constructed;

    private void ReadCore(ReadOnlySpan<byte> update, bool v2, long maxStructs, int maxDepth)
    {
        if (v2)
        {
            _v2 = new UpdateDecoderV2(update, maxStructs, maxDepth, _keys);
            EncodingUtils.ReadUpdate(ref _v2, update, _staging, _deleteSet, _clients, _spareDeleteLists);
        }
        else
        {
            _v1 = new UpdateDecoderV1(update, maxStructs, maxDepth);
            EncodingUtils.ReadUpdate(ref _v1, update, _staging, _deleteSet, _clients, _spareDeleteLists);
        }
    }

    private void Release(int version)
    {
        if (version != _version)
        {
            // Released already.
            return;
        }

        _version++;

        foreach (var list in _deleteSet.Clients.Values)
        {
            Recycle(list, _spareDeleteLists);
        }

        _deleteSet.Clients.Clear();
        _clients.Clear();
        _staging.Reset();
        _v2 = default;
        _keys.Clear();
        if (t_free == null)
        {
            t_free = this;
        }
        else
        {
            t_spare = this;
        }

        static void Recycle<T>(List<T> list, Stack<List<T>> spare)
        {
            // Also a list that is not kept: an array of 85,000 bytes or more is on the large object heap, where a dead
            // one counts as live until a gen2 collection, so the references of an uncleared one keep its contents
            // alive through every gen0 and gen1 collection.
            list.Clear();
            if (list.Capacity <= MaxRetainedListCapacity && spare.Count < MaxSpareLists)
            {
                spare.Push(list);
            }
        }
    }

    /// <summary>The decoded update, valid until <see cref="Dispose"/>; disposing twice is harmless.</summary>
    public readonly struct Lease : IDisposable
    {
        private readonly UpdateReader? _reader;
        private readonly int _version;

        internal Lease(UpdateReader reader, int version)
        {
            _reader = reader;
            _version = version;
        }

        public DeleteSet DeleteSet => Valid()._deleteSet;

        /// <summary>
        /// The structs of the update as staged fields, one table per reader and so per lease: a read while another lease is
        /// open gets another reader and another table. Reset when the lease is released. Each client's structs are one range.
        /// </summary>
        public StagingTable Staging => Valid()._staging;

        public void Dispose() => _reader?.Release(_version);

        private UpdateReader Valid() => _reader != null && _reader._version == _version
            ? _reader
            : throw new ObjectDisposedException(nameof(UpdateReader), "The update was released.");
    }
}
