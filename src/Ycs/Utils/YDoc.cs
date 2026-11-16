// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Ycs;

/// <summary>
/// Yjs instance handles the state of shared data.
/// </summary>
public partial class YDoc
{
    private readonly YDocOptions _opts;
    private string? _guid;

    internal List<Transaction> _transactionCleanups;
    // The list of the batch before, kept for the next one (see Transaction.CleanupTransactions).
    internal List<Transaction>? _spareTransactionCleanups;
    internal Transaction? _transaction;
    internal readonly HashSet<YDoc> Subdocs;

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

    internal void CollectItem()
    {
        if (_itemCollected || _item.IsNull)
        {
            return;
        }

        _collectedParentDoc = ((AbstractType)_item.Parent).Doc;
        _itemCollected = true;
        if (AbstractType.PoisonCollectedHandleForTests)
        {
            _item = AbstractType.PoisonItem();
        }
    }

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
            ApplyPooledCopy(update, v2, transactionOrigin, limits);
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
    private void ApplyUpdate(byte[] buffer, int count, bool v2, object? transactionOrigin, UpdateLimits? limits)
    {
        limits ??= _opts.Limits;
        UpdateReader.Lease update;
        try
        {
            update = UpdateReader.Read(buffer, 0, count, v2, limits.MaxStructsPerUpdate, limits.MaxDepth);
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
    private void ApplyPooledCopy(ReadOnlySpan<byte> update, bool v2, object? transactionOrigin, UpdateLimits? limits)
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

    /// <summary>No search markers are made or used in this document (the tests' reference: markers must not change any output).</summary>
    internal bool SearchMarkersDisabledForTests;

    /// <summary>Every insert integrates an item of its own, as in Yjs (the tests' reference for <see cref="Item.ExtendRun"/>).</summary>
    internal bool RunExtensionsDisabledForTests;

    /// <summary>The marker lookups walk as Yjs' loops do, item by item (the tests' reference for the run-head index, <see cref="ClientStructs.RunHead"/>).</summary>
    internal bool RunHeadIndexDisabledForTests;

    /// <summary>How many steps a marker walk takes item by item before it asks the run-head index (the tests lower it to 1).</summary>
    internal int LiteralWalkStepsForTests = 32;

    /// <summary>When set, every marker lookup adds the item and index it ends at (the tests compare them).</summary>
    internal List<(ID Item, int Index)>? MarkerLookupsForTests;

    /// <summary>The root type named <paramref name="name"/>, if it was defined (without defining it).</summary>
    internal bool TryGetRoot(string name, [MaybeNullWhen(false)] out AbstractType type) => _share.TryGetValue(name, out type);

    internal void InvokeSubdocsChanged(IReadOnlySet<YDoc> loaded, IReadOnlySet<YDoc> added, IReadOnlySet<YDoc> removed)
    {
        SubdocsChanged?.Invoke(this, new SubdocsEventArgs(loaded, added, removed));
    }

    internal bool HasBeforeObserverCallsListeners => BeforeObserverCalls != null;

    /// <summary>Before the store is read as structs (encoded) in a transaction: the runs it extended in place are split (<see cref="Transaction.MaterializeRunExtensions"/>).</summary>
    internal void MaterializeRunExtensions() => _transaction?.MaterializeRunExtensions();

    internal void InvokeOnBeforeObserverCalls(Transaction transaction)
    {
        BeforeObserverCalls?.Invoke(this, transaction);
    }

    internal void InvokeAfterAllTransactions(IReadOnlyList<Transaction> transactions)
    {
        AfterAllTransactions?.Invoke(this, transactions);
    }

    internal void InvokeOnBeforeTransaction(Transaction transaction)
    {
        BeforeTransaction?.Invoke(this, transaction);
    }

    internal void InvokeOnPendingDropped(PendingDrop args) => PendingDropped?.Invoke(this, args);

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
        BeforeAllTransactions?.Invoke(this, EventArgs.Empty);
    }

    internal void InvokeDestroyed()
    {
        Destroyed?.Invoke(this, EventArgs.Empty);
    }

    internal void InvokeUpdateV1(Transaction transaction)
    {
        var handler = Updated;
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
        var handler = UpdatedV2;
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

    internal YDocOptions CloneOptionsWithNewGuid()
    {
        return _opts with { Guid = System.Guid.NewGuid().ToString("D") };
    }

    internal string FindRootTypeKey(AbstractType type)
    {
        foreach (var kvp in _share)
        {
            if (type.Equals(kvp.Value))
            {
                return kvp.Key;
            }
        }

        throw new InvalidOperationException("The type is not a root type of this document.");
    }
}

