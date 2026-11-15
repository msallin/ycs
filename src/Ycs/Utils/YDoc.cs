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
    /// <summary>A random one unless set (only when first read, so that one that is set costs nothing).</summary>
    public string Guid
    {
        get => _guid ??= System.Guid.NewGuid().ToString("D");
        set => _guid = value;
    }

    private string _guid;

    /// <summary>
    /// Yjs' <c>collectionid</c>: associates the document with a collection of the provider. A
    /// subdocument without one takes its parent's when it is added. Not encoded.
    /// </summary>
    public string CollectionId { get; set; }

    /// <summary>Any value of the 'any' encoding; a subdocument's meta travels with its item.</summary>
    public object Meta { get; set; }

    public bool AutoLoad { get; set; }

    /// <summary>Whether the document should be synced by the provider now; <see cref="YDoc.Load"/> sets it. Yjs' default is true.</summary>
    public bool ShouldLoad { get; set; } = true;

    /// <summary>
    /// What an update applied to the document may cost (<see cref="UpdateLimits.Default"/> unless set);
    /// <see cref="YDoc.ApplyUpdate(byte[], object, UpdateLimits)"/> can override it for one update. A
    /// subdocument read from an update takes its parent's.
    /// </summary>
    public UpdateLimits Limits
    {
        get => _limits;
        set => _limits = value ?? throw new ArgumentNullException(nameof(value));
    }

    private UpdateLimits _limits = UpdateLimits.Default;

    /// <summary>
    /// The client id to start with instead of a random one; for tests and reproducible benchmarks.
    /// Two documents that edit concurrently must never share a client id. 0 to 2^53 - 1, as Yjs'
    /// <c>doc.clientID</c> (which it generates as a random 32-bit unsigned integer).
    /// </summary>
    public long? ClientId
    {
        get => _clientId;
        set => _clientId = value is null or >= 0 and <= Ycs.Limits.MaxClient
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "A client id is 0 to 2^53 - 1.");
    }

    private long? _clientId;

    internal YDocOptions Clone()
    {
        return new YDocOptions
        {
            Gc = Gc,
            GcFilter = GcFilter,
            Guid = Guid,
            CollectionId = CollectionId,
            Meta = Meta,
            AutoLoad = AutoLoad,
            ShouldLoad = ShouldLoad,
            Limits = Limits,
        };
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
    public object Meta => _opts.Meta;

    /// <summary>What an applied update may cost (<see cref="YDocOptions.Limits"/>).</summary>
    public UpdateLimits Limits => _opts.Limits;
    public string CollectionId { get; internal set; }

    /// <summary>Whether the document should be synced by the provider now (Yjs' <c>shouldLoad</c>); <see cref="Load"/> sets it.</summary>
    public bool ShouldLoad { get; private set; }
    internal List<Transaction> _transactionCleanups;
    // The list of the batch before, kept for the next one (see Transaction.CleanupTransactions).
    internal List<Transaction> _spareTransactionCleanups;
    internal Transaction _transaction;
    internal readonly HashSet<YDoc> Subdocs;

    // Ended transactions for reuse, linked by Transaction._nextFree.
    private Transaction _freeTransactions;
    private int _freeTransactionCount;

    // If this document is a subdocument - a document integrated into another document - them _item is defined.
    internal Item _item;

    private readonly Dictionary<string, AbstractType> _share;

    /// <summary>
    /// 32 random bits, as Yjs' <c>random.uint32()</c>, from the system's random number generator: two
    /// documents (or two processes started together) must not share a client id, which would silently
    /// corrupt the merge.
    /// </summary>
    internal static long GenerateNewClientId()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes);
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
        ShouldLoad = _opts.ShouldLoad;
        CollectionId = _opts.CollectionId;
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
            (item.Parent as AbstractType).Doc.Transact(this, static (doc, tr) => tr.SubdocsLoaded.Add(doc));
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
        // A subdocument's Destroy runs a transaction here whose cleanup changes Subdocs.
        foreach (var sd in Subdocs.ToArray())
        {
            sd.Destroy();
        }

        var item = _item;
        if (item != null)
        {
            _item = null;
            // A collected item holds ContentDeleted already (Yjs sets a property nothing reads).
            var content = item.Content as ContentDoc;
            if (content != null)
            {
                content.Doc = ContentDoc.CreateDocFromOpts(Guid, content.Opts, shouldLoad: false, _opts.Limits);
                content.Doc._item = item;
            }

            ((AbstractType)item.Parent).Doc.Transact((doc: this, item, content), static (s, tr) =>
            {
                if (!s.item.Deleted)
                {
                    tr.SubdocsAdded.Add(s.content.Doc);
                }

                tr.SubdocsRemoved.Add(s.doc);
            });
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

    public long ClientId { get; internal set; }
    internal StructStore Store { get; private set; }

    /// <summary>
    /// Whether received structs or deletions wait for an update this document has not received (Yjs'
    /// <c>doc.store.pendingStructs</c> and <c>pendingDs</c>). What waits is not in the document yet; it is
    /// integrated when the missing update arrives, and updates that do not depend on it are integrated
    /// meanwhile. <see cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/> includes it, as Yjs does.
    /// </summary>
    public bool HasPending => Store.HasPending;

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
        ArgumentNullException.ThrowIfNull(fun);
        Transact(fun, static (f, tr) => f(tr), origin, local);
    }

    /// <summary>
    /// Like <see cref="Transact(Action{Transaction}, object, bool)"/>, with the state that
    /// <paramref name="action"/> needs passed in, so that a <c>static</c> lambda captures nothing
    /// and no closure is allocated: <c>doc.Transact((text, index), static (s, tr) => ...)</c>.
    /// </summary>
    /// <param name="state">Passed to <paramref name="action"/>.</param>
    /// <param name="action">The function that should be executed as a transaction.</param>
    /// <param name="origin">Transaction owner. Will be stored in 'transaction.origin'.</param>
    /// <param name="local">Whether the change originates from this document.</param>
    public void Transact<TState>(TState state, Action<TState, Transaction> action, object origin = null, bool local = true)
    {
        ArgumentNullException.ThrowIfNull(action);

        var transaction = _transaction;
        var initialCall = false;
        if (transaction == null)
        {
            initialCall = true;
            transaction = _transaction = RentTransaction(origin, local);
            _transactionCleanups.Add(transaction);
            if (_transactionCleanups.Count == 1)
            {
                InvokeBeforeAllTransactions();
            }

            InvokeOnBeforeTransaction(transaction);
        }

        var completed = false;
        try
        {
            action(state, transaction);
            completed = true;
        }
        finally
        {
            if (initialCall)
            {
                // A transaction that an observer starts during a cleanup is cleaned up after the one
                // being cleaned up, in the same loop; it is not nested (as in Yjs).
                var finishCleanup = _transactionCleanups.Count > 0 && _transactionCleanups[0] == transaction;
                _transaction = null;

                if (finishCleanup)
                {
                    // Observer calls may create new transactions for which we need to call the observers and do cleanup.
                    // Also we need to ensure that all cleanups are called, even if the observers throw errors.
                    if (completed)
                    {
                        Transaction.CleanupTransactions(this);
                    }
                    else
                    {
                        try
                        {
                            Transaction.CleanupTransactions(this);
                        }
                        catch (Exception)
                        {
                            // The exception of 'action' propagates; one from an observer would hide it.
                        }
                    }
                }
            }
        }
    }

    private Transaction RentTransaction(object origin, bool local)
    {
        var transaction = _freeTransactions;
        if (transaction != null)
        {
            _freeTransactions = transaction._nextFree;
            transaction._nextFree = null;
            _freeTransactionCount--;
        }
        else
        {
            transaction = new Transaction(this);
        }

        transaction.Begin(origin, local);
        return transaction;
    }

    /// <summary>Ends a cleaned-up transaction and keeps it for reuse (up to <see cref="Transaction.PoolSize"/>).</summary>
    internal void ReturnTransaction(Transaction transaction)
    {
        transaction.End();
        if (_freeTransactionCount < Transaction.PoolSize)
        {
            transaction._nextFree = _freeTransactions;
            _freeTransactions = transaction;
            _freeTransactionCount++;
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
        // As Yjs' constructor check: a subclass (YXmlElement for YXmlFragment) is another type.
        if (typeof(T) != typeof(AbstractType) && type.GetType() != typeof(T))
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
                throw new InvalidOperationException($"Type with the name {name} has already been defined with a different constructor");
            }
        }

        return (T)type;
    }

    /// <summary>
    /// Applies an update in the original ('v1') format, the output of Yjs' <c>Y.encodeStateAsUpdate</c>
    /// and of every y-protocols provider (Yjs' <c>Y.applyUpdate</c>). The transaction it runs in is
    /// not local, as in Yjs, also when it is the caller's.
    /// <br/>
    /// A malformed update, or one beyond a limit, throws <see cref="YUpdateException"/> and leaves the
    /// document as it was, except beyond <see cref="UpdateLimits.MaxIntegrationSteps"/>
    /// (<see cref="YUpdateException.PartlyApplied"/>).
    /// </summary>
    /// <param name="update">The update.</param>
    /// <param name="transactionOrigin">Stored on the transaction; observers and the undo manager see it.</param>
    /// <param name="limits">For this update instead of <see cref="YDocOptions.Limits"/>, e.g. <see cref="UpdateLimits.Unlimited"/> for a stored state the application trusts.</param>
    /// <exception cref="YUpdateException">The update is malformed or beyond a limit; the document is unchanged unless <see cref="YUpdateException.PartlyApplied"/>.</exception>
    public void ApplyUpdate(byte[] update, object transactionOrigin = null, UpdateLimits limits = null) => ApplyUpdateV1(update, transactionOrigin, limits);

    /// <inheritdoc cref="ApplyUpdate(byte[], object, UpdateLimits)"/>
    public void ApplyUpdate(ReadOnlySpan<byte> update, object transactionOrigin = null, UpdateLimits limits = null) => ApplyUpdateV1(update, transactionOrigin, limits);

    /// <inheritdoc cref="ApplyUpdate(byte[], object, UpdateLimits)"/>
    public void ApplyUpdateV1(byte[] update, object transactionOrigin = null, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplyUpdate(update, update.Length, v2: false, transactionOrigin, limits);
    }

    /// <inheritdoc cref="ApplyUpdate(byte[], object, UpdateLimits)"/>
    public void ApplyUpdateV1(ReadOnlySpan<byte> update, object transactionOrigin = null, UpdateLimits limits = null)
    {
        ApplyPooledCopy(update, v2: false, transactionOrigin, limits);
    }

    /// <summary>
    /// Applies an update in the v2 format (Yjs' <c>Y.applyUpdateV2</c>).
    /// </summary>
    /// <inheritdoc cref="ApplyUpdate(byte[], object, UpdateLimits)"/>
    public void ApplyUpdateV2(byte[] update, object transactionOrigin = null, UpdateLimits limits = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        ApplyUpdate(update, update.Length, v2: true, transactionOrigin, limits);
    }

    /// <inheritdoc cref="ApplyUpdateV2(byte[], object, UpdateLimits)"/>
    public void ApplyUpdateV2(ReadOnlySpan<byte> update, object transactionOrigin = null, UpdateLimits limits = null)
    {
        ApplyPooledCopy(update, v2: true, transactionOrigin, limits);
    }

    /// <summary>
    /// Like <see cref="ApplyUpdate(ReadOnlySpan{byte}, object, UpdateLimits)"/> (v1), but returns false instead of throwing
    /// <see cref="YUpdateException"/>; the document is then unchanged, except beyond
    /// <see cref="UpdateLimits.MaxIntegrationSteps"/> (see <see cref="YUpdateException.PartlyApplied"/>).
    /// </summary>
    public bool TryApplyUpdate(ReadOnlySpan<byte> update, object transactionOrigin = null, UpdateLimits limits = null) =>
        TryApply(update, v2: false, transactionOrigin, limits);

    /// <summary>
    /// Like <see cref="ApplyUpdateV2(ReadOnlySpan{byte}, object, UpdateLimits)"/>, but returns false instead of throwing
    /// <see cref="YUpdateException"/>; the document is then unchanged, except beyond
    /// <see cref="UpdateLimits.MaxIntegrationSteps"/> (see <see cref="YUpdateException.PartlyApplied"/>).
    /// </summary>
    public bool TryApplyUpdateV2(ReadOnlySpan<byte> update, object transactionOrigin = null, UpdateLimits limits = null) =>
        TryApply(update, v2: true, transactionOrigin, limits);

    private bool TryApply(ReadOnlySpan<byte> update, bool v2, object transactionOrigin, UpdateLimits limits)
    {
        try
        {
            ApplyPooledCopy(update, v2, transactionOrigin, limits);
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
    public byte[] EncodeStateAsUpdateV1(ReadOnlySpan<byte> encodedTargetStateVector = default) =>
        EncodingUtils.EncodeStateAsUpdate(Store, encodedTargetStateVector, v2: false);

    /// <inheritdoc cref="EncodeStateAsUpdate(IBufferWriter{byte}, ReadOnlySpan{byte})"/>
    public void EncodeStateAsUpdateV1(IBufferWriter<byte> destination, ReadOnlySpan<byte> encodedTargetStateVector = default) =>
        EncodingUtils.EncodeStateAsUpdate(Store, destination, encodedTargetStateVector, v2: false);

    /// <summary>
    /// Encodes the document as one update in the v2 format (Yjs' <c>Y.encodeStateAsUpdateV2</c>).
    /// </summary>
    /// <inheritdoc cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/>
    public byte[] EncodeStateAsUpdateV2(ReadOnlySpan<byte> encodedTargetStateVector = default) =>
        EncodingUtils.EncodeStateAsUpdate(Store, encodedTargetStateVector, v2: true);

    /// <summary>
    /// Writes the document as one update in the v2 format into <paramref name="destination"/>.
    /// </summary>
    /// <inheritdoc cref="EncodeStateAsUpdate(ReadOnlySpan{byte})"/>
    public void EncodeStateAsUpdateV2(IBufferWriter<byte> destination, ReadOnlySpan<byte> encodedTargetStateVector = default) =>
        EncodingUtils.EncodeStateAsUpdate(Store, destination, encodedTargetStateVector, v2: true);

    /// <summary>
    /// Encodes the document's state vector (Yjs' <c>Y.encodeStateVector</c>). The state vector has the
    /// same bytes in both update formats.
    /// </summary>
    public byte[] EncodeStateVector() => EncodingUtils.EncodeStateVector(Store);

    /// <summary>
    /// Writes the document's state vector into <paramref name="destination"/>.
    /// </summary>
    /// <inheritdoc cref="EncodeStateVector()"/>
    public void EncodeStateVector(IBufferWriter<byte> destination) => EncodingUtils.EncodeStateVector(Store, destination);

    /// <inheritdoc cref="EncodeStateVector()"/>
    public byte[] EncodeStateVectorV1() => EncodeStateVector();

    /// <inheritdoc cref="EncodeStateVector()"/>
    public byte[] EncodeStateVectorV2() => EncodeStateVector();

    /// <summary>
    /// Decodes the whole update (structs and delete set) before the document is touched, then applies
    /// it (<see cref="StructStore.ApplyUpdate"/>), which checks what depends on the document before it
    /// changes anything.
    /// </summary>
    private void ApplyUpdate(byte[] buffer, int count, bool v2, object transactionOrigin, UpdateLimits limits)
    {
        limits ??= _opts.Limits;
        UpdateReader.Lease update;
        try
        {
            update = UpdateReader.Read(buffer, 0, count, v2, limits.MaxStructsPerUpdate);
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }

        // The decoded lists are reused for the next update once the lease is disposed; the structs
        // themselves are integrated (or encoded as pending) inside the transaction.
        try
        {
            Transact((store: Store, update, limits), static (s, tr) =>
            {
                // As Yjs' readUpdateV2: also when this runs in the caller's transaction.
                tr.Local = false;
                s.store.IntegrationSteps = s.limits.MaxIntegrationSteps;
                try
                {
                    s.store.ApplyUpdate(tr, s.update.Structs, s.update.DeleteSet, s.limits);
                }
                finally
                {
                    s.store.IntegrationSteps = long.MaxValue;
                }
            }, transactionOrigin, local: false);
        }
        finally
        {
            update.Dispose();
        }
    }

    /// <summary>
    /// The decoders read from an array; a span is copied into a pooled one first. Nothing
    /// decoded keeps a reference to the input once the lease is disposed, so the array can be
    /// returned afterwards.
    /// </summary>
    private void ApplyPooledCopy(ReadOnlySpan<byte> update, bool v2, object transactionOrigin, UpdateLimits limits)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(update.Length);
        try
        {
            update.CopyTo(buffer);
            ApplyUpdate(buffer, update.Length, v2, transactionOrigin, limits);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The root type named <paramref name="name"/>, if it was defined (without defining it).</summary>
    internal bool TryGetRoot(string name, out AbstractType type) => _share.TryGetValue(name, out type);

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
            // The encoder goes back before the handler runs, which may encode updates itself.
            byte[] update = null;
            var encoder = UpdateEncoderV1.Rent();
            try
            {
                if (transaction.WriteUpdateMessageFromTransaction(encoder))
                {
                    update = encoder.ToArray();
                }
            }
            finally
            {
                encoder.Return();
            }

            if (update != null)
            {
                handler.Invoke(this, (update, transaction.Origin, transaction));
            }
        }
    }

    internal void InvokeUpdateV2(Transaction transaction)
    {
        var handler = UpdateV2;
        if (handler != null)
        {
            // The encoder goes back before the handler runs, which may encode updates itself.
            byte[] update = null;
            var encoder = UpdateEncoderV2.Rent();
            try
            {
                if (transaction.WriteUpdateMessageFromTransaction(encoder))
                {
                    update = encoder.ToArray();
                }
            }
            finally
            {
                encoder.Return();
            }

            if (update != null)
            {
                handler.Invoke(this, (update, transaction.Origin, transaction));
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

