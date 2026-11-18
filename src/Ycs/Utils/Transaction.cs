using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Ycs;

/// <summary>
/// A transaction is created for every change on the Yjs model. It is possible
/// to bundle changes on the Yjs model in a single transaction to minimize
/// the number of messages sent and the number of observer calls.
/// If possible the user of this library should bundle as many changes as possible.
/// <para>
/// A transaction, its collections and the events created in it are valid until the
/// <see cref="YDoc.AfterAllTransactions"/> handlers of its batch have returned. Then the document
/// reuses the object for a later transaction, so read what you need inside a callback or handler
/// and keep that, not the transaction. Reading an ended transaction, or the changes of an event
/// whose transaction ended, throws <see cref="InvalidOperationException"/> (as Yjs throws when an
/// event's changes are computed after its handler).
/// </para>
/// </summary>
public sealed partial class Transaction
{
    // How many ended transactions a document keeps for reuse: one, plus those that observers start
    // during a cleanup.
    internal const int PoolSize = 4;

    // Collections that grew beyond this are dropped when the transaction ends, so a document does
    // not hold the peak of one large transaction for good.
    private const int KeepCapacity = 64;

    // Handed to the events of an ended transaction, so that their lazily computed changes throw
    // instead of reading the transaction that reuses the object.
    private static readonly Transaction Ended = new Transaction(null!);

    private static readonly Comparison<(int PathLength, YEvent Event)> ByPathLength = static (a, b) => a.PathLength - b.PathLength;

    private readonly YDoc _doc;
    private object? _origin;
    private bool _local;
    private bool _ended;

    // Set before the cleanup collects what the transaction deleted, whether or not the document collects garbage: from
    // then on the text that events report as removed is no longer readable (YTextEvent.Removed).
    private bool _contentReleased;

    // Set while the events of types skipped in the observer loop are built: their handlers had their turn.
    internal bool _buildingSkippedEvents;

    // A remote change touched a text with formatting: YText.CleanupAfterTransaction runs after the
    // AfterTransaction handlers.
    internal bool _needFormattingCleanup;

    // Made by the first delete: an insert-only document never holds one.
    private DeleteSet? _deleteSet;

    // The document's state when the transaction began and when its cleanup began (empty before that): both read from
    // the store's change log, from the positions BeforeState and AfterState hold.
    private readonly TransactionState _beforeState;
    private readonly TransactionState _afterState;

    // What the undo manager keeps beyond the transaction; replaced, not cleared, when it ends.
    private bool _deleteSetTaken;

    internal readonly ChangedTypes _changed = new();

    // Enumerated in the order the types were added, as Yjs' Map (it orders the deep observer calls):
    // a Dictionary keeps that order while nothing is removed, and entries are only added, or all
    // cleared (SortEventsByOrderOfChange re-adds them).
    internal readonly Dictionary<YType, IList<YEvent>> _changedParentTypes = new();

    // Ids, not structs: the cleanup's merge pass reads an entry after it merged the entries after it, which can have
    // merged that entry's struct away, and a nested transaction can have split or merged more. The lookup finds the
    // struct that holds the id now.
    internal readonly List<ItemId> _mergeStructs = new();

    // Where an insert continued the run of the item before it in place (ItemRef.ExtendRun) instead of integrating an
    // item of its own, in clock order: the clock at which that item would have started, as runs of equal steps
    // (typing one character at a time is one entry). Yjs merges such an item into its left neighbour at the end of
    // the transaction, so the store ends up the same; until then, everything that could tell the two apart
    // (observers and their events, encoding the store, applying an update, undo) first splits the runs at these
    // clocks (MaterializeRunExtensions), which makes exactly the items Yjs has.
    private List<(long Client, long Start, long Step, int Count)>? _runExtensions;

    internal bool HasRunExtensions => _runExtensions is { Count: > 0 };

    internal void RecordRunExtension(ItemId start)
    {
        var extensions = _runExtensions ??= new List<(long, long, long, int)>();
        if (extensions.Count > 0)
        {
            var (client, first, step, count) = extensions[^1];
            if (client == start.Client && (count == 1 ? start.Clock > first : start.Clock == first + (step * count)))
            {
                extensions[^1] = (client, first, count == 1 ? start.Clock - first : step, count + 1);
                return;
            }
        }

        extensions.Add((start.Client, start.Clock, 0, 1));
    }

    /// <summary>Splits the runs extended in place back into the items Yjs has (see <see cref="_runExtensions"/>).</summary>
    internal void MaterializeRunExtensions()
    {
        if (_runExtensions is not { Count: > 0 } extensions)
        {
            return;
        }

        var store = _doc.Store;
        foreach (var (client, first, step, count) in extensions)
        {
            for (var k = 0; k < count; k++)
            {
                store.GetItemCleanStart(this, new ItemId(client, first + (step * k)));
            }
        }

        extensions.Clear();
    }

    // The deep observers to call in the cleanup, collected first, as Yjs does.
    private readonly List<(YType Type, IList<YEvent> Events)> _deepCalls = new();

    // Rarely used, so created on first use.
    private Dictionary<string, object?>? _meta;
    private HashSet<YDoc>? _subdocsAdded;
    private HashSet<YDoc>? _subdocsRemoved;
    private HashSet<YDoc>? _subdocsLoaded;

    // The document's free list of ended transactions.
    internal Transaction? _nextFree;

    internal Transaction(YDoc doc)
    {
        _doc = doc;
        _beforeState = new TransactionState();
        _afterState = new TransactionState();
        _ended = true;
    }

    /// <summary>
    /// Describes the set of deleted items by Ids.
    /// </summary>
    internal DeleteSet DeleteSet => _ended ? throw EndedException() : _deleteSet ??= new DeleteSet();

    /// <summary>The delete set, which the caller keeps beyond the transaction (the undo manager's stack item).</summary>
    internal DeleteSet TakeDeleteSet()
    {
        _deleteSetTaken = true;
        return _deleteSet ??= new DeleteSet();
    }

    /// <summary>The deleted content of the transaction's events is gone or about to go: its observers and <c>AfterTransaction</c> handlers have run.</summary>
    internal bool ContentReleased => _ended || _contentReleased;

    private static InvalidOperationException EndedException() => new(
        "This transaction has ended and its object is reused by the document. Read what you need from a " +
        "transaction, or the changes of an event (Changes, Keys, Delta, Adds, Deletes), in the callback or " +
        "handler that receives it, and keep that instead.");

    /// <summary>Starts the transaction: the state before it is the document's state now.</summary>
    internal void Begin(object? origin, bool local)
    {
        Debug.Assert(_ended && _doc != null);
        _ended = false;
        _contentReleased = false;
        _origin = origin;
        _local = local;
        var store = _doc.Store;
        _beforeState.Set(store, store.BeginChanges());
    }

    /// <summary>The clients whose lists grew in the transaction, from its start to the start of its cleanup.</summary>
    internal ChangedClients ChangedClients => new(_doc.Store, _beforeState.Position, _afterState.Position);

    /// <summary>
    /// Ends the transaction after its batch: the events created in it let go of it, and what it
    /// collected is cleared for reuse (or dropped, where someone keeps it or it grew large).
    /// </summary>
    internal void End()
    {
        foreach (var kvp in _changedParentTypes)
        {
            var events = kvp.Value;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].Transaction == this)
                {
                    events[i].Transaction = Ended;
                }
            }
        }

        _ended = true;
        _needFormattingCleanup = false;
        _origin = null;
        _meta = null;
        _subdocsAdded = null;
        _subdocsRemoved = null;
        _subdocsLoaded = null;
        var largeChanged = _changed.Count > KeepCapacity;
        _changed.Clear();
        if (largeChanged)
        {
            _changed.TrimExcess();
        }

        Clear(_changedParentTypes);
        _runExtensions?.Clear();
        _mergeStructs.Clear();
        if (_mergeStructs.Capacity > KeepCapacity)
        {
            _mergeStructs.Capacity = 0;
        }

        _deepCalls.Clear();

        if (_deleteSet != null)
        {
            if (_deleteSetTaken || _deleteSet.Clients.Count > KeepCapacity)
            {
                _deleteSet = null;
            }
            else
            {
                // Not taken: nobody else holds the set or its lists.
                _deleteSet.ClearForReuse();
            }
        }

        _beforeState.Reset();
        _afterState.Reset();
        _deleteSetTaken = false;

        static void Clear<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
            where TKey : notnull
        {
            var large = dictionary.Count > KeepCapacity;
            dictionary.Clear();
            if (large)
            {
                dictionary.TrimExcess();
            }
        }
    }

    /// <summary>
    /// If 'type.parent' was added in current transaction, 'type' technically did not change,
    /// it was just added and we should not fire events for 'type'.
    /// </summary>
    internal void AddChangedTypeToTransaction(YType type, string? parentSub)
    {
        if (type.IsRoot || (_beforeState.TryGetValue(type.ItemId.Client, out var clock) && type.ItemId.Clock < clock && !type.ItemDeleted))
        {
            // Most changed types are lists, which share one read-only set (ChangedKeys.ChildList).
            _changed.TryGetValue(type, out var set);
            var updated = ChangedKeys.Add(set, parentSub);
            if (!ReferenceEquals(updated, set))
            {
                _changed[type] = updated;
            }
        }
    }

    /// <summary>
    /// A changed type builds its event only when someone observes it at its turn (see
    /// <see cref="YType.IsObserved"/>); Yjs builds every event. When an observer attached a deep
    /// observer or a document listener meanwhile, the skipped types build theirs now, without calling
    /// their own observers, and every event list is put back into the order of change.
    /// </summary>
    private static void BuildSkippedEvents(Transaction transaction)
    {
        List<KeyValuePair<YType, ISet<string?>>>? skipped = null;
        foreach (var kvp in transaction._changed)
        {
            var type = kvp.Key;
            if (!type.ItemDeleted && !HasOwnEvent(transaction, type) && type.IsObserved(transaction))
            {
                (skipped ??= new()).Add(kvp);
            }
        }

        if (skipped == null)
        {
            return;
        }

        transaction._buildingSkippedEvents = true;
        try
        {
            foreach (var kvp in skipped)
            {
                kvp.Key.CallObserver(transaction, kvp.Value);
            }
        }
        finally
        {
            transaction._buildingSkippedEvents = false;
        }

        SortEventsByOrderOfChange(transaction);
    }

    // Separate from BuildSkippedEvents, whose common path must not allocate the closure.
    private static void SortEventsByOrderOfChange(Transaction transaction)
    {
        var order = new Dictionary<YType, int>(transaction._changed.Count);
        foreach (var kvp in transaction._changed)
        {
            order[kvp.Key] = order.Count;
        }

        var changedParentTypes = transaction._changedParentTypes;
        foreach (var events in changedParentTypes.Values)
        {
            if (events is List<YEvent> list && list.Count > 1)
            {
                StableSort.Sort(list, (a, b) => order.GetValueOrDefault(a.Target, int.MaxValue).CompareTo(order.GetValueOrDefault(b.Target, int.MaxValue)));
            }
        }

        // The keys as Yjs adds them: each changed type that has an event, then its ancestors.
        var ordered = new List<KeyValuePair<YType, IList<YEvent>>>(changedParentTypes.Count);
        var seen = new HashSet<YType>();
        foreach (var kvp in transaction._changed)
        {
            if (!HasOwnEvent(transaction, kvp.Key))
            {
                continue;
            }

            for (var type = kvp.Key; type != null; type = type.ParentType)
            {
                if (seen.Add(type) && changedParentTypes.TryGetValue(type, out var events))
                {
                    ordered.Add(new(type, events));
                }
            }
        }

        foreach (var kvp in changedParentTypes)
        {
            if (seen.Add(kvp.Key))
            {
                ordered.Add(kvp);
            }
        }

        changedParentTypes.Clear();
        foreach (var kvp in ordered)
        {
            changedParentTypes.Add(kvp.Key, kvp.Value);
        }
    }

    private static bool HasOwnEvent(Transaction transaction, YType type)
    {
        if (transaction._changedParentTypes.TryGetValue(type, out var events))
        {
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].Target == type)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Cleans up the document's transactions, including those that observers start meanwhile, then
    /// ends them for reuse; Yjs' <c>cleanupTransactions</c>, except where a handler throws after the observer calls.
    /// Every step of every transaction's cleanup runs, also after a handler threw, and of the exceptions the last
    /// escapes once the batch is finished (<see cref="YDoc.AfterAllTransactions"/>' last of all):
    /// <list type="bullet">
    /// <item>the observer calls (<see cref="YDoc.BeforeObserverCalls"/>, whose exception skips the others of that
    /// transaction, the observers, the deep observers, <see cref="YDoc.AfterTransaction"/>, the formatting cleanup)
    /// as Yjs' <c>callAll</c>;</item>
    /// <item>what follows them (<see cref="YDoc.AfterTransactionCleanup"/>, <see cref="YDoc.Updated"/>,
    /// <see cref="YDoc.UpdatedV2"/>, <see cref="YDoc.SubdocsChanged"/>, destroying the removed subdocuments), each step
    /// even if one before threw. Yjs lets such an exception escape at once and never finishes the batch, so its
    /// document cleans up no later transaction (yjs#799); Ycs does what the open yjs#803 does.</item>
    /// </list>
    /// Within one event, a throwing handler skips that event's later handlers (Yjs' <c>emit</c>).
    /// </summary>
    internal static void CleanupTransactions(YDoc doc)
    {
        // The change log, which the states of the transactions of this batch read, is kept until the outermost batch
        // has finished: an 'AfterAllTransactions' handler may start another and still reads these transactions.
        var store = doc.Store;
        store.EnterBatch();
        try
        {
            CleanupBatch(doc);
        }
        finally
        {
            store.ExitBatch();
        }
    }

    private static void CleanupBatch(YDoc doc)
    {
        var transactionCleanups = doc._transactionCleanups;
        ExceptionDispatchInfo? error = null;
        for (var i = 0; i < transactionCleanups.Count; i++)
        {
            error = CleanupTransaction(transactionCleanups[i]) ?? error;
        }

        // A transaction that an 'AfterAllTransactions' handler starts is a new batch, in the
        // other list (as Yjs replaces 'doc._transactionCleanups').
        doc._transaction = null;
        doc._transactionCleanups = doc._spareTransactionCleanups ?? new List<Transaction>();
        doc._spareTransactionCleanups = null;
        try
        {
            doc.InvokeAfterAllTransactions(transactionCleanups);
        }
        finally
        {
            for (var i = 0; i < transactionCleanups.Count; i++)
            {
                doc.ReturnTransaction(transactionCleanups[i]);
            }

            transactionCleanups.Clear();
            doc._spareTransactionCleanups = transactionCleanups;

            // Nothing that reads a struct freed in this batch is pending now.
            doc.Store.Slab.AllowReuse();
            doc.Store.CompactIfWorthwhile();
        }

        error?.Throw();
    }

    private static bool AnyObserved(Transaction transaction)
    {
        foreach (var (type, _) in transaction._changed)
        {
            if (type.IsObserved(transaction))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Yjs' 'cleanupTransactions' for one transaction: the observer calls (Yjs' <c>try</c> part), then the structs are
    /// merged and the document's handlers called (Yjs' <c>finally</c> part); every handler step runs even if one
    /// before threw, and the last exception is returned.
    /// </summary>
    private static ExceptionDispatchInfo? CleanupTransaction(Transaction transaction)
    {
        var doc = transaction._doc;
        var store = doc.Store;
        var ds = transaction._deleteSet;
        var mergeStructs = transaction._mergeStructs;
        ExceptionDispatchInfo? error = null;

        doc._transaction = null;

        if (transaction.HasRunExtensions)
        {
            // Whoever is called from here on with the transaction or its events sees Yjs' items, and so does a garbage
            // collection filter (once per item); when nobody is, the runs stay as they are, which is what the merge
            // below would make of them.
            if (doc.HasTransactionListeners || doc.HasBeforeObserverCallsListeners || AnyObserved(transaction)
                || (doc.GarbageCollection && doc.GcFilter != YDocOptions.DefaultGcFilter && ds is { Clients.Count: > 0 }))
            {
                transaction.MaterializeRunExtensions();
            }
            else
            {
                transaction._runExtensions!.Clear();
            }
        }

        try
        {
            ds?.SortAndMergeDeleteSet();
            transaction._afterState.Set(store, store.ChangeCount);

            // An exception here skips every observer call, as in Yjs.
            doc.InvokeOnBeforeObserverCalls(transaction);

            try
            {
                foreach (var kvp in transaction._changed)
                {
                    var itemType = kvp.Key;
                    if (!itemType.ItemDeleted)
                    {
                        try
                        {
                            itemType.CallObserver(transaction, kvp.Value);
                        }
                        catch (Exception e)
                        {
                            error = ExceptionDispatchInfo.Capture(e);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }

            try
            {
                BuildSkippedEvents(transaction);
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }

            // Deep observe events: every event gets its current target first, then the observers
            // are called (an event shared by several types keeps the last one, as in Yjs).
            var deepCalls = transaction._deepCalls;
            try
            {
                foreach (var kvp in transaction._changedParentTypes)
                {
                    var type = kvp.Key;
                    var events = kvp.Value;

                    // We need to think about the possibility that the user transforms the YDoc in the event.
                    if (type.HasDeepObservers && !type.ItemDeleted)
                    {
                        // As Yjs: the events whose target was deleted meanwhile are left out (the
                        // observer is still called when none is left).
                        var delivered = events;
                        for (var i = 0; i < events.Count; i++)
                        {
                            if (events[i].Target.ItemDeleted)
                            {
                                delivered = events.Where(static e => !e.Target.ItemDeleted).ToList();
                                break;
                            }
                        }

                        for (var i = 0; i < delivered.Count; i++)
                        {
                            delivered[i].CurrentTarget = type;
                        }

                        events = delivered;
                        if (events.Count > 1)
                        {
                            // Sort events by path length so that top-level events are fired first. The
                            // length is computed once per event (Yjs caches the path).
                            var keyed = new List<(int PathLength, YEvent Event)>(events.Count);
                            for (var i = 0; i < events.Count; i++)
                            {
                                keyed.Add((events[i].PathLength, events[i]));
                            }

                            StableSort.Sort(keyed, ByPathLength);
                            var sortedEvents = new List<YEvent>(events.Count);
                            foreach (var (_, e) in keyed)
                            {
                                sortedEvents.Add(e);
                            }

                            events = sortedEvents;
                        }

                        deepCalls.Add((type, events));
                    }
                }
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }

            for (var i = 0; i < deepCalls.Count; i++)
            {
                try
                {
                    deepCalls[i].Type.CallDeepEventHandlerListeners(deepCalls[i].Events, transaction);
                }
                catch (Exception e)
                {
                    error = ExceptionDispatchInfo.Capture(e);
                }
            }

            try
            {
                doc.InvokeOnAfterTransaction(transaction);
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }

            try
            {
                if (transaction._needFormattingCleanup)
                {
                    YText.CleanupAfterTransaction(transaction);
                }
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        }
        catch (Exception e)
        {
            error = ExceptionDispatchInfo.Capture(e);
        }

        // What follows is Yjs' 'finally' part.
        // Replace deleted items with ItemDeleted / GC.
        // This is where content is actually removed from the Yjs Doc.
        transaction._contentReleased = true;
        if (doc.GarbageCollection)
        {
            ds?.TryGcDeleteSet(store, doc);
        }

        TestHooks.Compact(store.Slab, true);
        ds?.TryMergeDeleteSet(store);

        // On all affected store.clients props, try to merge.
        var beforeState = transaction._beforeState;
        foreach (var (_, structs, beforeClock, _) in transaction.ChangedClients)
        {
            var firstChangePos = Math.Max(structs.FindIndex(beforeClock), 1);
            for (int j = structs.Count - 1; j >= firstChangePos;)
            {
                j -= 1 + DeleteSet.TryToMergeWithLefts(store, structs, j);
            }
        }

        // Try to merge mergeStructs.
        for (int j = mergeStructs.Count - 1; j >= 0; j--)
        {
            var structs = store.Clients[mergeStructs[j].Client];
            var replacedStructPos = structs.FindIndex(mergeStructs[j].Clock);

            // Both neighbours merged already when more than one struct went.
            if (replacedStructPos + 1 < structs.Count && DeleteSet.TryToMergeWithLefts(store, structs, replacedStructPos + 1) > 1)
            {
                continue;
            }

            if (replacedStructPos > 0)
            {
                DeleteSet.TryToMergeWithLefts(store, structs, replacedStructPos);
            }
        }

        TestHooks.Compact(store.Slab, false);

        if (TestHooks.CheckAfterTransactions)
        {
            store.CheckForTests(transaction);
        }

        if (!transaction._local)
        {
            if (!transaction._afterState.TryGetValue(doc.ClientId, out var afterClock))
            {
                afterClock = -1;
            }

            if (!beforeState.TryGetValue(doc.ClientId, out var beforeClock))
            {
                beforeClock = -1;
            }

            if (afterClock != beforeClock)
            {
                doc.ClientId = YDoc.GenerateNewClientId();
            }
        }

        // Unlike Yjs, a throwing handler skips none of the later steps, nor the cleanup of the next transaction.
        Step(static t => t._doc.InvokeOnAfterTransactionCleanup(t), transaction, ref error);
        Step(static t => t._doc.InvokeUpdateV1(t), transaction, ref error);
        Step(static t => t._doc.InvokeUpdateV2(t), transaction, ref error);

        if (transaction._subdocsAdded?.Count > 0 || transaction._subdocsRemoved?.Count > 0 || transaction._subdocsLoaded?.Count > 0)
        {
            var added = transaction.AddedSubdocs;
            var removed = transaction.RemovedSubdocs;

            foreach (var subDoc in added)
            {
                subDoc.ClientId = doc.ClientId;
                subDoc.CollectionId ??= doc.CollectionId;
                doc.Subdocs.Add(subDoc);
            }

            foreach (var subDoc in removed)
            {
                doc.Subdocs.Remove(subDoc);
            }

            Step(static t => t._doc.InvokeSubdocsChanged(t.LoadedSubdocs, t.AddedSubdocs, t.RemovedSubdocs), transaction, ref error);
            Step(static t =>
            {
                foreach (var subDoc in t.RemovedSubdocs)
                {
                    subDoc.Destroy();
                }
            }, transaction, ref error);
        }

        return error;
    }

    private static void Step(Action<Transaction> step, Transaction transaction, ref ExceptionDispatchInfo? error)
    {
        try
        {
            step(transaction);
        }
        catch (Exception e)
        {
            error = ExceptionDispatchInfo.Capture(e);
        }
    }

    internal static void SplitSnapshotAffectedStructs(Transaction transaction, Snapshot snapshot)
    {
        if (!transaction.Meta.TryGetValue("splitSnapshotAffectedStructs", out var metaObj))
        {
            metaObj = new HashSet<Snapshot>();
            transaction.Meta["splitSnapshotAffectedStructs"] = metaObj;
        }

        var meta = (ISet<Snapshot>)metaObj!;
        var store = transaction.Doc.Store;

        // Check if we already split for this snapshot.
        if (!meta.Contains(snapshot))
        {
            foreach (var kvp in snapshot.StateVector)
            {
                var client = kvp.Key;
                var clock = kvp.Value;

                if (clock < store.GetState(client))
                {
                    store.GetItemCleanStart(transaction, new ItemId(client, clock));
                }
            }

            snapshot.DeleteSet.IterateDeletedStructs(transaction, static _ => true);

            meta.Add(snapshot);
        }
    }

    /// <returns>Whether the data was written.</returns>
    internal bool WriteUpdateMessageFromTransaction(IUpdateEncoder encoder)
    {
        if (_deleteSet is not { Clients.Count: > 0 } && !StateChanged())
        {
            return false;
        }

        var deleteSet = DeleteSet;
        deleteSet.SortAndMergeDeleteSet();
        EncodingUtils.WriteClientsStructs(encoder, _doc.Store, _beforeState.Position);
        deleteSet.Write(encoder);

        return true;
    }

    // The entries from the start to the start of the cleanup are all the clients that grew.
    private bool StateChanged() => _afterState.Position > _beforeState.Position;
}
