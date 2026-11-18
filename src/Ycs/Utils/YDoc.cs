using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Ycs;

/// <summary>
/// Yjs instance handles the state of shared data.
/// </summary>
public sealed partial class YDoc
{
    private readonly YDocOptions _opts;

    // The handlers, made by the first one added: a document nobody listens to holds none of the eleven fields.
    private YDocEvents? _events;

    private YDocEvents Events => _events ?? Interlocked.CompareExchange(ref _events, new YDocEvents(), null) ?? _events!;

    private string? _guid;

    internal List<Transaction> _transactionCleanups;
    // The list of the batch before, kept for the next one (see Transaction.CleanupTransactions).
    internal List<Transaction>? _spareTransactionCleanups;
    internal Transaction? _transaction;
    // Made by the first subdocument.
    private HashSet<YDoc>? _subdocs;

    internal HashSet<YDoc> Subdocs => _subdocs ??= [];

    // Ended transactions for reuse, linked by Transaction._nextFree.
    private Transaction? _freeTransactions;
    private int _freeTransactionCount;

    // If this document is a subdocument - a document integrated into another document - them _item is defined.
    internal ItemRef _item;

    // A collected subdocument's item is deleted and its struct can merge away, its slot be reused, while user code holds
    // the document: ContentDoc.Gc (CollectItem) keeps the parent document, and nothing reads _item afterwards.
    private bool _itemCollected;
    private YDoc? _collectedParentDoc;

    internal void SetItem(ItemRef item)
    {
        _item = item;
        _itemCollected = false;
        _collectedParentDoc = null;
    }

    /// <summary>
    /// Throws, before any item is made, when this document is a subdocument already (or was collected as one and not
    /// destroyed), or is <paramref name="parent"/> or one of its ancestors: a document names one item.
    /// </summary>
    internal void CheckCanInsertInto(YDoc parent)
    {
        if (!_item.IsNull || _itemCollected)
        {
            throw new ArgumentException("The document is already a subdocument and cannot be inserted again: create a second YDoc with the same guid instead. Nothing was inserted.");
        }

        for (var d = parent; d != null; d = d._item.IsNull || d._itemCollected ? null : ((YType)d._item.Parent!).Doc)
        {
            if (d == this)
            {
                throw new ArgumentException("A document cannot be inserted into itself or into a subdocument of its own. Nothing was inserted.");
            }
        }
    }

    internal void CollectItem()
    {
        if (_itemCollected || _item.IsNull)
        {
            return;
        }

        _collectedParentDoc = ((YType)_item.Parent!).Doc;
        _itemCollected = true;
        if (TestHooks.PoisonCollectedHandle)
        {
            _item = YType.PoisonItem();
        }
    }

    // The root types by name; made by the first one, so a document nobody asked a root of holds none.
    private Dictionary<string, YType>? _share;

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

    internal StructStore Store { get; private set; }

    private Transaction RentTransaction(object? origin, bool local)
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

    private bool TryApply(ReadOnlySpan<byte> update, bool v2, object? transactionOrigin, UpdateLimits? limits)
    {
        try
        {
            ApplyUpdate(update, v2, transactionOrigin, limits);
            return true;
        }
        catch (YUpdateException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes the whole update (structs and delete set) before the document is touched, then applies
    /// it (<see cref="StructStore.ApplyUpdate"/>), which checks what depends on the document before it
    /// changes anything.
    /// </summary>
    private void ApplyUpdate(ReadOnlySpan<byte> update, bool v2, object? transactionOrigin, UpdateLimits? limits)
    {
        limits ??= _opts.Limits;
        UpdateReader.Lease lease;
        try
        {
            lease = UpdateReader.Read(update, v2, limits.MaxStructsPerUpdate, limits.MaxDepth);
        }
        catch (Exception e) when (YUpdateException.IsDecodingFailure(e))
        {
            throw YUpdateException.Wrap(e);
        }

        // The decoded lists are reused for the next update once the lease is disposed; the structs
        // themselves are integrated (or encoded as pending) inside the transaction.
        try
        {
            Transact((store: Store, update: lease, limits), static (s, tr) =>
            {
                // As Yjs' readUpdateV2: also when this runs in the caller's transaction.
                tr.Local = false;
                s.store.IntegrationSteps = s.limits.MaxIntegrationSteps;
                try
                {
                    s.store.ApplyUpdate(tr, s.update.Staging, s.update.DeleteSet, s.limits);
                }
                finally
                {
                    s.store.IntegrationSteps = long.MaxValue;
                }
            }, transactionOrigin, local: false);
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>The root type named <paramref name="name"/>, if it was defined (without defining it).</summary>
    internal bool TryGetRoot(string name, [MaybeNullWhen(false)] out YType type)
    {
        type = null;
        return _share != null && _share.TryGetValue(name, out type);
    }

    internal void InvokeSubdocsChanged(IReadOnlySet<YDoc> loaded, IReadOnlySet<YDoc> added, IReadOnlySet<YDoc> removed)
    {
        _events?.SubdocsChanged?.Invoke(this, new SubdocsEventArgs(loaded, added, removed));
    }

    internal bool HasBeforeObserverCallsListeners => _events?.BeforeObserverCalls != null;

    /// <summary>Before the store is read as structs (encoded) in a transaction: the runs it extended in place are split (<see cref="Transaction.MaterializeRunExtensions"/>).</summary>
    internal void MaterializeRunExtensions() => _transaction?.MaterializeRunExtensions();

    internal void InvokeOnBeforeObserverCalls(Transaction transaction)
    {
        _events?.BeforeObserverCalls?.Invoke(this, transaction);
    }

    internal void InvokeAfterAllTransactions(IReadOnlyList<Transaction> transactions)
    {
        _events?.AfterAllTransactions?.Invoke(this, transactions);
    }

    internal void InvokeOnBeforeTransaction(Transaction transaction)
    {
        _events?.BeforeTransaction?.Invoke(this, transaction);
    }

    internal void InvokeOnPendingDropped(PendingDrop args) => _events?.PendingDropped?.Invoke(this, args);

    internal void InvokeOnAfterTransaction(Transaction transaction)
    {
        _events?.AfterTransaction?.Invoke(this, transaction);
    }

    internal void InvokeOnAfterTransactionCleanup(Transaction transaction)
    {
        _events?.AfterTransactionCleanup?.Invoke(this, transaction);
    }

    internal void InvokeBeforeAllTransactions()
    {
        _events?.BeforeAllTransactions?.Invoke(this, EventArgs.Empty);
    }

    internal void InvokeDestroyed()
    {
        _events?.Destroyed?.Invoke(this, EventArgs.Empty);
    }

    internal void InvokeUpdateV1(Transaction transaction)
    {
        var handler = _events?.Updated;
        if (handler != null)
        {
            // The encoder goes back before the handler runs, which may encode updates itself.
            byte[]? update = null;
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
                handler.Invoke(this, new UpdateEventArgs(update, transaction.Origin, transaction));
            }
        }
    }

    internal void InvokeUpdateV2(Transaction transaction)
    {
        var handler = _events?.UpdatedV2;
        if (handler != null)
        {
            // The encoder goes back before the handler runs, which may encode updates itself.
            byte[]? update = null;
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
                handler.Invoke(this, new UpdateEventArgs(update, transaction.Origin, transaction));
            }
        }
    }

    internal string FindRootTypeKey(YType type)
    {
        foreach (var kvp in _share ?? [])
        {
            if (type.Equals(kvp.Value))
            {
                return kvp.Key;
            }
        }

        throw new InvalidOperationException("The type is not a root type of this document.");
    }
}

