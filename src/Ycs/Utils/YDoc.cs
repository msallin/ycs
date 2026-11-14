// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;
using System.Diagnostics;

namespace Ycs;

public class YDocOptions
{
    private static Predicate<Item> DefaultPredicate = (item) => true;

    public bool Gc { get; set; } = true;
    public Predicate<Item> GcFilter { get; set; } = DefaultPredicate;
    public string Guid { get; set; } = System.Guid.NewGuid().ToString("D");
    public IDictionary<string, object> Meta { get; set; } = null;
    public bool AutoLoad { get; set; } = false;

    /// <summary>
    /// The most an applied update may add to what waits for missing structs (structs and deletions
    /// of other clients this document has not received yet), counted as encoded bytes. An update that
    /// would grow it beyond this is rejected with a <see cref="YUpdateException"/> and the document
    /// is unchanged. Default 16 MiB.
    /// </summary>
    public long MaxPendingBytes
    {
        get => _maxPendingBytes;
        set => _maxPendingBytes = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "The limit cannot be negative.");
    }

    private long _maxPendingBytes = 16 * 1024 * 1024;

    /// <summary>
    /// The client id to start with instead of a random one; for tests and reproducible benchmarks.
    /// Two documents that edit concurrently must never share a client id.
    /// </summary>
    public int? ClientId
    {
        get => _clientId;
        set => _clientId = value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), "A client id cannot be negative.");
    }

    private int? _clientId;

    internal YDocOptions Clone()
    {
        return new YDocOptions
        {
            Gc = Gc,
            GcFilter = GcFilter,
            Guid = Guid,
            Meta = Meta == null ? null : new Dictionary<string, object>(Meta),
            AutoLoad = AutoLoad,
            MaxPendingBytes = MaxPendingBytes,
        };
    }

    internal void Write(IUpdateEncoder encoder, int offset)
    {
        var dict = new Dictionary<string, object>();
        dict["gc"] = Gc;
        dict["guid"] = Guid;
        dict["autoLoad"] = AutoLoad;

        if (Meta != null)
        {
            dict["meta"] = Meta;
        }

        encoder.WriteAny(dict);
    }

    /// <summary>A subdocument's options as another peer wrote them; anything of the wrong type is a <see cref="YUpdateException"/>.</summary>
    internal static YDocOptions Read(IUpdateDecoder decoder)
    {
        if (decoder.ReadAny() is not IDictionary<string, object> dict)
        {
            throw new YUpdateException("A subdocument's options are not an object.");
        }

        return new YDocOptions
        {
            Gc = Get(dict, "gc", true),
            AutoLoad = Get(dict, "autoLoad", false),
            // The guid is read before the options (Yjs ignores 'guid' in the options as well).
            Meta = dict.TryGetValue("meta", out var meta) && meta != null
                ? meta as Dictionary<string, object> ?? throw new YUpdateException("A subdocument's 'meta' is not an object.")
                : null,
        };

        static bool Get(IDictionary<string, object> dict, string key, bool defaultValue) =>
            !dict.TryGetValue(key, out var value) || value == null ? defaultValue
            : value is bool b ? b
            : throw new YUpdateException($"A subdocument's '{key}' is not a boolean.");
    }
}

/// <summary>
/// Yjs instance handles the state of shared data.
/// </summary>
public partial class YDoc
{
    private readonly YDocOptions _opts;

    public string Guid => _opts.Guid;
    public bool Gc => _opts.Gc;
    public Predicate<Item> GcFilter => _opts.GcFilter;
    public bool AutoLoad => _opts.AutoLoad;
    public IDictionary<string, object> Meta => _opts.Meta;

    internal bool ShouldLoad;
    internal readonly IList<Transaction> _transactionCleanups;
    internal Transaction _transaction;
    internal readonly ISet<YDoc> Subdocs;

    // If this document is a subdocument - a document integrated into another document - them _item is defined.
    internal Item _item;

    private readonly IDictionary<string, AbstractType> _share;

    /// <summary>
    /// Yjs uses a random 32-bit client id (see 'generateNewClientId' in Doc.js). It has to be
    /// unpredictable across processes: 'new Random()' is seeded from the clock on the
    /// .NET Framework targets, so two documents created in the same tick - or two server
    /// processes started together - would share a client id and silently corrupt the merge.
    /// </summary>
    internal static int GenerateNewClientId()
    {
        // Non-negative, to stay within the int-typed ClientId; the wire format writes it as a uint.
        return System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue);
    }

    /// <param name="gc">Disable garbage collection.</param>
    /// <param name="gcFilter">WIll be called before an Item is garbage collected. Return false to keep the item.</param>
    public YDoc(YDocOptions opts = null)
    {
        _opts = opts ?? new YDocOptions();
        _transactionCleanups = new List<Transaction>();

        ClientId = _opts.ClientId ?? GenerateNewClientId();
        _share = new Dictionary<string, AbstractType>();
        Store = new StructStore();
        Subdocs = new HashSet<YDoc>();
        ShouldLoad = _opts.AutoLoad;
    }

    /// <summary>
    /// Notify the parent document that you request to load data into this subdocument (if it is a subdocument).
    /// 'load()' might be used in the future to request any provider to load the most current data.
    /// It is safe to call 'Load()' multiple times.
    /// </summary>
    public void Load()
    {
        var item = _item;
        if (item != null && !ShouldLoad)
        {
            Debug.Assert(item.Parent is AbstractType);
            (item.Parent as AbstractType).Doc.Transact(tr =>
            {
                tr.SubdocsLoaded.Add(this);
            }, origin: null, local: true);
        }
        ShouldLoad = true;
    }

    public Snapshot CreateSnapshot() => new Snapshot(new DeleteSet(Store), Store.GetStateVector());

    public IEnumerable<string> GetSubdocGuids()
    {
        return new HashSet<string>(Subdocs.Select(sd => sd.Guid));
    }

    public void Destroy()
    {
        foreach (var sd in Subdocs)
        {
            sd.Destroy();
        }

        var item = _item;
        if (item != null)
        {
            _item = null;
            var content = item.Content as ContentDoc;

            if (item.Deleted)
            {
                if (content != null)
                {
                    content.Doc = null;
                }
            }
            else
            {
                Debug.Assert(content != null);
                var newOpts = content.Opts;
                newOpts.Guid = Guid;

                content.Doc = new YDoc(newOpts);
                content.Doc._item = item;
            }

            (item.Parent as AbstractType).Doc.Transact(tr =>
            {
                if (!item.Deleted)
                {
                    Debug.Assert(content != null);
                    tr.SubdocsAdded.Add(content.Doc);
                }

                tr.SubdocsRemoved.Add(this);
            }, origin: null, local: true);
        }

        InvokeDestroyed();
    }

    public event EventHandler<Transaction> BeforeObserverCalls;
    public event EventHandler<Transaction> BeforeTransaction;
    public event EventHandler<Transaction> AfterTransaction;
    public event EventHandler<Transaction> AfterTransactionCleanup;
    public event EventHandler BeforeAllTransactions;
    public event EventHandler<IList<Transaction>> AfterAllTransactions;
    /// <summary>
    /// Fires after each transaction that changed the document, with the change as an update in the
    /// original ('v1') format: Yjs' <c>doc.on('update')</c>, what every y-protocols provider sends.
    /// </summary>
    public event EventHandler<(byte[] data, object origin, Transaction transaction)> Update;

    /// <summary>
    /// Like <see cref="Update"/>, with the update in the v2 format: Yjs' <c>doc.on('updateV2')</c>.
    /// </summary>
    public event EventHandler<(byte[] data, object origin, Transaction transaction)> UpdateV2;
    public event EventHandler Destroyed;
    public event EventHandler<(ISet<YDoc> Loaded, ISet<YDoc> Added, ISet<YDoc> Removed)> SubdocsChanged;

    public int ClientId { get; internal set; }
    internal StructStore Store { get; private set; }

    /// <summary>
    /// Whether received structs or deletions wait for an update this document has not received (Yjs'
    /// <c>doc.store.pendingStructs</c> and <c>pendingDs</c>). What waits is not in the document yet, and
    /// <see cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/> leaves it out; it is integrated when the
    /// missing update arrives. Updates that do not depend on it are integrated meanwhile.
    /// </summary>
    public bool HasPending => Store.Pending != null || Store.PendingDs.Count != 0;

    /// <summary>
    /// Changes that happen inside of a transaction are bundled.
    /// This means that the observer fires _after_ the transaction is finished and that
    /// all changes that happened inside of the transaction are sent as one message to the
    /// other peers.
    /// </summary>
    /// <param name="fun">The function that should be executed as a transaction.</param>
    /// <param name="origin">Transaction owner. Will be stored in 'transaction.origin'.</param>
    /// <param name="local"></param>
    public void Transact(Action<Transaction> fun, object origin = null, bool local = true)
    {
        bool initialCall = false;
        if (_transaction == null)
        {
            initialCall = true;
            _transaction = new Transaction(this, origin, local);
            _transactionCleanups.Add(_transaction);
            if (_transactionCleanups.Count == 1)
            {
                InvokeBeforeAllTransactions();
            }

            InvokeOnBeforeTransaction(_transaction);
        }

        var completed = false;
        try
        {
            fun(_transaction);
            completed = true;
        }
        finally
        {
            if (initialCall && _transactionCleanups.Count > 0 && _transactionCleanups[0] == _transaction)
            {
                // The first transaction ended, now process observer calls.
                // Observer call may create new transacations for which we need to call the observers and do cleanup.
                // We don't want to nest these calls, so we execute these calls one after another.
                // Also we need to ensure that all cleanups are called, even if the observers throw errors.
                if (completed)
                {
                    Transaction.CleanupTransactions(_transactionCleanups, 0);
                }
                else
                {
                    try
                    {
                        Transaction.CleanupTransactions(_transactionCleanups, 0);
                    }
                    catch (Exception)
                    {
                        // The exception of 'fun' propagates; one from an observer would hide it.
                    }
                }
            }
        }
    }

    public YArray GetArray(string name = "")
    {
        return Get<YArray>(name);
    }

    public YMap GetMap(string name = "")
    {
        return Get<YMap>(name);
    }

    public YText GetText(string name = "")
    {
        return Get<YText>(name);
    }

    public T Get<T>(string name)
        where T : AbstractType, new()
    {
        if (!_share.TryGetValue(name, out var type))
        {
            type = new T();
            type.Integrate(this, null);
            _share[name] = type;
        }

        // Remote type is realized when this method is called.
        if (typeof(T) != typeof(AbstractType) && type is not T)
        {
            if (type.GetType() == typeof(AbstractType))
            {
                var t = new T();
                t._map = type._map;

                foreach (var kvp in type._map)
                {
                    var n = kvp.Value;
                    for (; n != null; n = n.Left as Item)
                    {
                        n.Parent = t;
                    }
                }

                t._start = type._start;
                for (var n = t._start; n != null; n = n.Right as Item)
                {
                    n.Parent = t;
                }

                t.Length = type.Length;

                _share[name] = t;
                t.Integrate(this, null);
                return t;
            }
            else
            {
                throw new Exception($"Type with the name {name} has already been defined with a different constructor");
            }
        }

        return (T)type;
    }

    /// <summary>
    /// Applies an update in the original ('v1') format, the output of Yjs' <c>Y.encodeStateAsUpdate</c>
    /// and of every y-protocols provider (Yjs' <c>Y.applyUpdate</c>). The transaction it runs in is
    /// not local, as in Yjs, also when it is the caller's.
    /// <br/>
    /// A malformed update, or one beyond a limit (see <see cref="YDocOptions.MaxPendingBytes"/>), throws
    /// <see cref="YUpdateException"/> and leaves the document as it was.
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="transactionOrigin">Stored on the transaction; observers and the undo manager see it.</param>
    /// <exception cref="YUpdateException">The update is malformed or beyond a limit; the document is unchanged.</exception>
    public void ApplyUpdate(byte[] update, object transactionOrigin = null) => ApplyUpdateV1(update, transactionOrigin);

    /// <inheritdoc cref="ApplyUpdate(byte[], object)"/>
    public void ApplyUpdate(ReadOnlySpan<byte> update, object transactionOrigin = null) => ApplyUpdateV1(update, transactionOrigin);

    /// <inheritdoc cref="ApplyUpdate(byte[], object)"/>
    public void ApplyUpdateV1(byte[] update, object transactionOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplyUpdate(new Lib0Reader(update), v2: false, transactionOrigin);
    }

    /// <inheritdoc cref="ApplyUpdate(byte[], object)"/>
    public void ApplyUpdateV1(ReadOnlySpan<byte> update, object transactionOrigin = null)
    {
        WithPooledCopy(update, buffer => ApplyUpdate(buffer, v2: false, transactionOrigin));
    }

    /// <summary>
    /// Applies an update in the v2 format (Yjs' <c>Y.applyUpdateV2</c>).
    /// </summary>
    /// <inheritdoc cref="ApplyUpdate(byte[], object)"/>
    public void ApplyUpdateV2(byte[] update, object transactionOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplyUpdate(new Lib0Reader(update), v2: true, transactionOrigin);
    }

    /// <inheritdoc cref="ApplyUpdateV2(byte[], object)"/>
    public void ApplyUpdateV2(ReadOnlySpan<byte> update, object transactionOrigin = null)
    {
        WithPooledCopy(update, buffer => ApplyUpdate(buffer, v2: true, transactionOrigin));
    }

    /// <summary>
    /// Like <see cref="ApplyUpdate(ReadOnlySpan{byte}, object)"/> (v1), but returns false instead of throwing
    /// <see cref="YUpdateException"/>; the document is then unchanged.
    /// </summary>
    public bool TryApplyUpdate(ReadOnlySpan<byte> update, object transactionOrigin = null) => TryApply(update, v2: false, transactionOrigin);

    /// <summary>
    /// Like <see cref="ApplyUpdateV2(ReadOnlySpan{byte}, object)"/>, but returns false instead of throwing
    /// <see cref="YUpdateException"/>; the document is then unchanged.
    /// </summary>
    public bool TryApplyUpdateV2(ReadOnlySpan<byte> update, object transactionOrigin = null) => TryApply(update, v2: true, transactionOrigin);

    private bool TryApply(ReadOnlySpan<byte> update, bool v2, object transactionOrigin)
    {
        try
        {
            WithPooledCopy(update, buffer => ApplyUpdate(buffer, v2, transactionOrigin));
            return true;
        }
        catch (YUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Encodes the document as one update in the original ('v1') format (Yjs' <c>Y.encodeStateAsUpdate</c>).
    /// With a remote state vector, only what the remote side is missing is written.
    /// </summary>
    /// <param name="encodedTargetStateVector">A state vector from <see cref="EncodeStateVector()"/>; empty for the whole document.</param>
    public byte[] EncodeStateAsUpdate(ReadOnlySpan<byte> encodedTargetStateVector = default) => EncodeStateAsUpdateV1(encodedTargetStateVector);

    /// <summary>
    /// Writes the document as one update in the original ('v1') format into <paramref name="destination"/>.
    /// </summary>
    /// <inheritdoc cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/>
    public void EncodeStateAsUpdate(IBufferWriter<byte> destination, ReadOnlySpan<byte> encodedTargetStateVector = default) =>
        EncodeStateAsUpdateV1(destination, encodedTargetStateVector);

    /// <inheritdoc cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/>
    public byte[] EncodeStateAsUpdateV1(ReadOnlySpan<byte> encodedTargetStateVector = default)
    {
        var encoder = new UpdateEncoderV1();
        WriteStateAsUpdate(encoder, DecodeTargetStateVector(encodedTargetStateVector));
        return encoder.ToArray();
    }

    /// <inheritdoc cref="EncodeStateAsUpdate(IBufferWriter{byte}, ReadOnlySpan{byte})"/>
    public void EncodeStateAsUpdateV1(IBufferWriter<byte> destination, ReadOnlySpan<byte> encodedTargetStateVector = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var encoder = new UpdateEncoderV1();
        WriteStateAsUpdate(encoder, DecodeTargetStateVector(encodedTargetStateVector));
        encoder.WriteTo(destination);
    }

    /// <summary>
    /// Encodes the document as one update in the v2 format (Yjs' <c>Y.encodeStateAsUpdateV2</c>).
    /// </summary>
    /// <inheritdoc cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/>
    public byte[] EncodeStateAsUpdateV2(ReadOnlySpan<byte> encodedTargetStateVector = default)
    {
        var encoder = new UpdateEncoderV2();
        WriteStateAsUpdate(encoder, DecodeTargetStateVector(encodedTargetStateVector));
        return encoder.ToArray();
    }

    /// <summary>
    /// Writes the document as one update in the v2 format into <paramref name="destination"/>.
    /// </summary>
    /// <inheritdoc cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/>
    public void EncodeStateAsUpdateV2(IBufferWriter<byte> destination, ReadOnlySpan<byte> encodedTargetStateVector = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var encoder = new UpdateEncoderV2();
        WriteStateAsUpdate(encoder, DecodeTargetStateVector(encodedTargetStateVector));
        encoder.WriteTo(destination);
    }

    /// <summary>
    /// Encodes the document's state vector (Yjs' <c>Y.encodeStateVector</c>). The state vector has the
    /// same bytes in both update formats.
    /// </summary>
    public byte[] EncodeStateVector()
    {
        var encoder = new DSEncoderV1();
        WriteStateVector(encoder);
        return encoder.ToArray();
    }

    /// <inheritdoc cref="EncodeStateVector()"/>
    public byte[] EncodeStateVectorV1() => EncodeStateVector();

    /// <inheritdoc cref="EncodeStateVector()"/>
    public byte[] EncodeStateVectorV2()
    {
        var encoder = new DSEncoderV2();
        WriteStateVector(encoder);
        return encoder.ToArray();
    }

    private static IDictionary<long, long> DecodeTargetStateVector(ReadOnlySpan<byte> encodedTargetStateVector)
    {
        if (encodedTargetStateVector.IsEmpty)
        {
            return new Dictionary<long, long>();
        }

        var bytes = encodedTargetStateVector.ToArray();
        return YUpdateException.Decode(() => EncodingUtils.DecodeStateVector(new Lib0Reader(bytes)));
    }

    /// <summary>
    /// Decodes the whole update (structs and delete set) before the document is touched, then applies
    /// it (<see cref="StructStore.ApplyUpdate"/>), which checks what depends on the document before it
    /// changes anything.
    /// </summary>
    private void ApplyUpdate(Lib0Reader reader, bool v2, object transactionOrigin)
    {
        Dictionary<long, List<AbstractStruct>> structs;
        DeleteSet ds;
        try
        {
            IUpdateDecoder decoder = v2 ? new UpdateDecoderV2(reader) : new UpdateDecoderV1(reader);
            structs = EncodingUtils.ReadClientStructRefs(decoder);
            ds = DeleteSet.Read(decoder);
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }

        Transact(tr =>
        {
            // As Yjs' readUpdateV2: also when this runs in the caller's transaction.
            tr.Local = false;
            Store.ApplyUpdate(tr, structs, ds, _opts.MaxPendingBytes);
        }, transactionOrigin, local: false);
    }

    /// <summary>
    /// The decoders read from an array; a span is copied into a pooled one first. Nothing
    /// decoded keeps a reference to the input, so the array can be returned afterwards.
    /// </summary>
    private static void WithPooledCopy(ReadOnlySpan<byte> update, Action<Lib0Reader> read)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(update.Length);
        try
        {
            update.CopyTo(buffer);
            read(new Lib0Reader(buffer, 0, update.Length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Write all the document as a single update message. If you specify the satte of the remote client, it will only
    /// write the operations that are missing.
    /// </summary>
    internal void WriteStateAsUpdate(IUpdateEncoder encoder, IDictionary<long, long> targetStateVector)
    {
        EncodingUtils.WriteClientsStructs(encoder, Store, targetStateVector);
        new DeleteSet(Store).Write(encoder);
    }

    internal void WriteStateVector(IDSEncoder encoder)
    {
        EncodingUtils.WriteStateVector(encoder, Store.GetStateVector());
    }

    internal void InvokeSubdocsChanged(ISet<YDoc> loaded, ISet<YDoc> added, ISet<YDoc> removed)
    {
        SubdocsChanged?.Invoke(this, (loaded, added, removed));
    }

    internal void InvokeOnBeforeObserverCalls(Transaction transaction)
    {
        BeforeObserverCalls?.Invoke(this, transaction);
    }

    internal void InvokeAfterAllTransactions(IList<Transaction> transactions)
    {
        AfterAllTransactions?.Invoke(this, transactions);
    }

    internal void InvokeOnBeforeTransaction(Transaction transaction)
    {
        BeforeTransaction?.Invoke(this, transaction);
    }

    internal void InvokeOnAfterTransaction(Transaction transaction)
    {
        AfterTransaction?.Invoke(this, transaction);
    }

    internal void InvokeOnAfterTransactionCleanup(Transaction transaction)
    {
        AfterTransactionCleanup?.Invoke(this, transaction);
    }

    internal void InvokeBeforeAllTransactions()
    {
        BeforeAllTransactions?.Invoke(this, null);
    }

    internal void InvokeDestroyed()
    {
        Destroyed?.Invoke(this, null);
    }

    internal void InvokeUpdateV1(Transaction transaction)
    {
        var handler = Update;
        if (handler != null)
        {
            var encoder = new UpdateEncoderV1();
            var hasContent = transaction.WriteUpdateMessageFromTransaction(encoder);
            if (hasContent)
            {
                handler.Invoke(this, (encoder.ToArray(), transaction.Origin, transaction));
            }
        }
    }

    internal void InvokeUpdateV2(Transaction transaction)
    {
        var handler = UpdateV2;
        if (handler != null)
        {
            var encoder = new UpdateEncoderV2();
            var hasContent = transaction.WriteUpdateMessageFromTransaction(encoder);
            if (hasContent)
            {
                handler.Invoke(this, (encoder.ToArray(), transaction.Origin, transaction));
            }
        }
    }

    internal YDocOptions CloneOptionsWithNewGuid()
    {
        var newOpts = _opts.Clone();
        newOpts.Guid = System.Guid.NewGuid().ToString("D");
        return newOpts;
    }

    internal string FindRootTypeKey(AbstractType type)
    {
        foreach (var kvp in _share)
        {
            if (type?.Equals(kvp.Value) ?? false)
            {
                return kvp.Key;
            }
        }

        throw new Exception();
    }
}
