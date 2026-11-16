// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// Decodes an update (its structs by client, and its delete set) into objects that are reused from
/// one update to the next on the same thread: the reader, the v1 and v2 decoders with their column
/// readers, the dictionary, the per-client lists and the delete set. The checks are those of
/// <see cref="EncodingUtils.ReadClientStructRefs(IUpdateDecoder)"/> and <see cref="DeleteSet.Read"/>.
/// <br/>
/// <see cref="Read"/> hands out a <see cref="Lease"/>; disposing it clears the lists and the staging table (dropping the
/// references to the structs and the input) and makes the reader available again. Thread-local
/// rather than per document: a server holding many documents keeps one per thread. A read while
/// another lease is open on the thread (re-entrancy) simply gets a new reader.
/// </summary>
internal sealed class UpdateReader
{
    /// <summary>A list that grew beyond this is not kept for the next update.</summary>
    private const int MaxRetainedListCapacity = 1024;

    /// <summary>At most this many spare lists of each kind are kept.</summary>
    private const int MaxSpareLists = 64;

    /// <summary>
    /// Whether reads fill a <see cref="StagingTable"/> (<see cref="Lease.Staging"/>); the tests switch it on. Off, no table
    /// is allocated and the cursor stages nothing. Set before the first read.
    /// </summary>
    internal static bool StageForTests;

    [ThreadStatic]
    private static UpdateReader t_free;

    private readonly Lib0Reader _input = new();
    private readonly Dictionary<long, List<AbstractStruct>> _structs = new();
    private readonly Stack<List<AbstractStruct>> _spareStructLists = new();
    private readonly HashSet<long> _clients = new();
    private readonly DeleteSet _deleteSet = new();
    private StagingTable _staging;
    private readonly Stack<List<DeleteSet.DeleteItem>> _spareDeleteLists = new();
    private UpdateDecoderV1 _v1;
    private UpdateDecoderV2 _v2;
    private int _version;

    /// <summary>
    /// Decodes <paramref name="count"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/>.
    /// Nothing decoded refers to the buffer after the lease is disposed. Malformed input throws (as the
    /// decoders do; the caller wraps it in a <see cref="YUpdateException"/>), and the reader is released.
    /// </summary>
    /// <param name="maxStructs">The most structs the update may hold (<see cref="UpdateLimits.MaxStructsPerUpdate"/>).</param>
    /// <param name="maxDepth">How deeply values may be nested (<see cref="UpdateLimits.MaxDepth"/>).</param>
    public static Lease Read(byte[] buffer, int offset, int count, bool v2, long maxStructs, int maxDepth = UpdateLimits.DefaultMaxDepth)
    {
        var reader = t_free ?? new UpdateReader();
        t_free = null;
        var version = reader._version;
        try
        {
            reader.ReadCore(buffer, offset, count, v2, maxStructs, maxDepth);
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

    private void ReadCore(byte[] buffer, int offset, int count, bool v2, long maxStructs, int maxDepth)
    {
        _input.Reset(buffer, offset, count);

        IUpdateDecoder decoder;
        if (v2)
        {
            if (_v2 == null)
            {
                _v2 = new UpdateDecoderV2(_input, maxStructs);
            }
            else
            {
                _v2.Reset(maxStructs);
            }

            decoder = _v2;
        }
        else
        {
            _v1 ??= new UpdateDecoderV1(_input, maxStructs);
            _v1.MaxStructs = maxStructs;
            decoder = _v1;
        }

        _input.MaxDepth = maxDepth;
        if (StageForTests)
        {
            _staging ??= new StagingTable();
        }

        EncodingUtils.ReadClientStructRefs(decoder, _structs, _clients, _spareStructLists, _staging);
        DeleteSet.ReadInto(decoder, _deleteSet, _spareDeleteLists);
    }

    private void Release(int version)
    {
        if (version != _version)
        {
            // Released already.
            return;
        }

        _version++;

        foreach (var list in _structs.Values)
        {
            Recycle(list, _spareStructLists);
        }

        foreach (var list in _deleteSet.Clients.Values)
        {
            Recycle(list, _spareDeleteLists);
        }

        _structs.Clear();
        _deleteSet.Clients.Clear();
        _clients.Clear();
        _staging?.Reset();
        _input.Clear();
        _v2?.Clear();
        t_free = this;

        static void Recycle<T>(List<T> list, Stack<List<T>> spare)
        {
            // Also a list that is not kept: an array of 85,000 bytes or more is on the large object heap, where a dead
            // one counts as live until a gen2 collection, so the references of an uncleared one keep the structs and
            // their document alive through every gen0 and gen1 collection.
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
        private readonly UpdateReader _reader;
        private readonly int _version;

        internal Lease(UpdateReader reader, int version)
        {
            _reader = reader;
            _version = version;
        }

        /// <summary>The structs by client, each list in clock order.</summary>
        public Dictionary<long, List<AbstractStruct>> Structs => Valid()._structs;

        public DeleteSet DeleteSet => Valid()._deleteSet;

        /// <summary>
        /// The shadow of <see cref="Structs"/>, one table per reader and so per lease: a read while another lease is
        /// open gets another reader and another table. Reset when the lease is released. Null unless
        /// <see cref="StageForTests"/> is on.
        /// </summary>
        public StagingTable Staging => Valid()._staging;

        public void Dispose() => _reader?.Release(_version);

        private UpdateReader Valid() => _reader != null && _reader._version == _version
            ? _reader
            : throw new ObjectDisposedException(nameof(UpdateReader), "The update was released.");
    }
}
