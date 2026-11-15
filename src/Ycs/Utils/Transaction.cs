// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

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
public partial class Transaction
{
    // How many ended transactions a document keeps for reuse: one, plus those that observers start
    // during a cleanup.
    internal const int PoolSize = 4;

    // Collections that grew beyond this are dropped when the transaction ends, so a document does
    // not hold the peak of one large transaction for good.
    private const int KeepCapacity = 64;

    // Handed to the events of an ended transaction, so that their lazily computed changes throw
    // instead of reading the transaction that reuses the object.
    private static readonly Transaction Ended = new Transaction(null);

    private static readonly Comparison<(int PathLength, YEvent Event)> ByPathLength = static (a, b) => a.PathLength - b.PathLength;

    private readonly YDoc _doc;
    private object _origin;
    private bool _local;
    private bool _ended;

    // Set while the events of types skipped in the observer loop are built: their handlers had their turn.
    internal bool _buildingSkippedEvents;

    // A remote change touched a text with formatting: YText.CleanupAfterTransaction runs after the
    // AfterTransaction handlers.
    internal bool _needFormattingCleanup;

    private DeleteSet _deleteSet;
    private Dictionary<long, long> _beforeState;
    private Dictionary<long, long> _afterState;

    // What the undo manager keeps beyond the transaction; replaced, not cleared, when it ends.
    private bool _deleteSetTaken;
    private bool _beforeStateTaken;
    private bool _afterStateTaken;

    internal readonly ChangedTypes _changed = new();

    // Enumerated in the order the types were added, as Yjs' Map (it orders the deep observer calls):
    // a Dictionary keeps that order while nothing is removed, and entries are only added, or all
    // cleared (SortEventsByOrderOfChange re-adds them).
    internal readonly Dictionary<AbstractType, IList<YEvent>> _changedParentTypes = new();
    internal readonly List<AbstractStruct> _mergeStructs = new();

    // Where an insert continued the run of the item before it in place (Item.ExtendRun) instead of integrating an
    // item of its own, in clock order: the clock at which that item would have started, as runs of equal steps
    // (typing one character at a time is one entry). Yjs merges such an item into its left neighbour at the end of
    // the transaction, so the store ends up the same; until then, everything that could tell the two apart
    // (observers and their events, encoding the store, applying an update, undo) first splits the runs at these
    // clocks (MaterializeRunExtensions), which makes exactly the items Yjs has.
    private List<(long Client, long Start, long Step, int Count)> _runExtensions;

    internal bool HasRunExtensions => _runExtensions is { Count: > 0 };

    internal void RecordRunExtension(ID start)
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
                store.GetItemCleanStart(this, new ID(client, first + (step * k)));
            }
        }

        extensions.Clear();
    }

    // The deep observers to call in the cleanup, collected first, as Yjs does.
    private readonly List<(AbstractType Type, IList<YEvent> Events)> _deepCalls = new();

    // Rarely used, so created on first use.
    private Dictionary<string, object> _meta;
    private HashSet<YDoc> _subdocsAdded;
    private HashSet<YDoc> _subdocsRemoved;
    private HashSet<YDoc> _subdocsLoaded;

    // The document's free list of ended transactions.
    internal Transaction _nextFree;

    internal Transaction(YDoc doc)
    {
        _doc = doc;
        _ended = true;
    }

    /// <summary>
    /// The Yjs instance.
    /// </summary>
    public YDoc Doc => _ended ? throw EndedException() : _doc;

    public object Origin => _ended ? throw EndedException() : _origin;

    /// <summary>
    /// Holds the state before the transaction started.
    /// </summary>
    public IDictionary<long, long> BeforeState => _ended ? throw EndedException() : _beforeState;

    /// <summary>
    /// Holds the state after the transaction (empty until its cleanup starts).
    /// </summary>
    public IDictionary<long, long> AfterState => _ended ? throw EndedException() : _afterState;

    /// <summary>
    /// All types that were directly modified (property added or child
    /// inserted/deleted). New types are not included in this Set.
    /// Maps from type to parentSubs ('item.parentSub = null' for YArray).
    /// </summary>
    public IDictionary<AbstractType, ISet<string>> Changed => _ended ? throw EndedException() : _changed;

    /// <summary>
    /// Stores the events for the types that observe also child elements.
    /// It is mainly used by 'observeDeep'.
    /// </summary>
    public IDictionary<AbstractType, IList<YEvent>> ChangedParentTypes => _ended ? throw EndedException() : _changedParentTypes;

    /// <summary>
    /// Stores meta information on the transaction.
    /// </summary>
    public IDictionary<string, object> Meta => _ended ? throw EndedException() : _meta ??= new Dictionary<string, object>();

    /// <summary>
    /// Whether this change originates from this doc. Applying an update makes the transaction it
    /// runs in non-local, as in Yjs.
    /// </summary>
    public bool Local
    {
        get => _ended ? throw EndedException() : _local;
        internal set => _local = value;
    }

    public ISet<YDoc> SubdocsAdded => _ended ? throw EndedException() : _subdocsAdded ??= new HashSet<YDoc>();

    public ISet<YDoc> SubdocsRemoved => _ended ? throw EndedException() : _subdocsRemoved ??= new HashSet<YDoc>();

    public ISet<YDoc> SubdocsLoaded => _ended ? throw EndedException() : _subdocsLoaded ??= new HashSet<YDoc>();

    /// <summary>
    /// Describes the set of deleted items by Ids.
    /// </summary>
    internal DeleteSet DeleteSet => _ended ? throw EndedException() : _deleteSet;

    /// <summary>The delete set, which the caller keeps beyond the transaction (the undo manager's stack item).</summary>
    internal DeleteSet TakeDeleteSet()
    {
        _deleteSetTaken = true;
        return _deleteSet;
    }

    /// <summary>The state before the transaction, which the caller keeps beyond it.</summary>
    internal Dictionary<long, long> TakeBeforeState()
    {
        _beforeStateTaken = true;
        return _beforeState;
    }

    /// <summary>The state after the transaction, which the caller keeps beyond it.</summary>
    internal Dictionary<long, long> TakeAfterState()
    {
        _afterStateTaken = true;
        return _afterState;
    }

    private static InvalidOperationException EndedException() => new(
        "This transaction has ended and its object is reused by the document. Read what you need from a " +
        "transaction, or the changes of an event (Changes, Keys, Delta, Adds, Deletes), in the callback or " +
        "handler that receives it, and keep that instead.");

    /// <summary>Starts the transaction: the state before it is the document's state now.</summary>
    internal void Begin(object origin, bool local)
    {
        Debug.Assert(_ended && _doc != null);
        _ended = false;
        _origin = origin;
        _local = local;
        _deleteSet ??= new DeleteSet();
        _afterState ??= new Dictionary<long, long>();
        _beforeState ??= new Dictionary<long, long>();
        _doc.Store.GetStateVector(_beforeState);
    }

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

        if (_deleteSetTaken || _deleteSet.Clients.Count > KeepCapacity)
        {
            _deleteSet = null;
        }
        else
        {
            // Not taken: nobody else holds the set or its lists.
            _deleteSet.ClearForReuse();
        }

        _beforeState = _beforeStateTaken || _beforeState.Count > KeepCapacity ? null : Cleared(_beforeState);
        _afterState = _afterStateTaken || _afterState.Count > KeepCapacity ? null : Cleared(_afterState);
        _deleteSetTaken = _beforeStateTaken = _afterStateTaken = false;

        static void Clear<TKey, TValue>(Dictionary<TKey, TValue> dictionary)
        {
            var large = dictionary.Count > KeepCapacity;
            dictionary.Clear();
            if (large)
            {
                dictionary.TrimExcess();
            }
        }

        static Dictionary<long, long> Cleared(Dictionary<long, long> dictionary)
        {
            dictionary.Clear();
            return dictionary;
        }
    }

    internal ID GetNextId()
    {
        return new ID(_doc.ClientId, _doc.Store.GetState(_doc.ClientId));
    }

    /// <summary>
    /// If 'type.parent' was added in current transaction, 'type' technically did not change,
    /// it was just added and we should not fire events for 'type'.
    /// </summary>
    internal void AddChangedTypeToTransaction(AbstractType type, string parentSub)
    {
        var item = type._item;
        if (item == null || (_beforeState.TryGetValue(item.Id.Client, out var clock) && item.Id.Clock < clock && !item.Deleted))
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
    /// <see cref="AbstractType.IsObserved"/>); Yjs builds every event. When an observer attached a deep
    /// observer or a document listener meanwhile, the skipped types build theirs now, without calling
    /// their own observers, and every event list is put back into the order of change.
    /// </summary>
    private static void BuildSkippedEvents(Transaction transaction)
    {
        List<KeyValuePair<AbstractType, ISet<string>>> skipped = null;
        foreach (var kvp in transaction._changed)
        {
            var type = kvp.Key;
            if ((type._item == null || !type._item.Deleted) && !HasOwnEvent(transaction, type) && type.IsObserved(transaction))
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
        var order = new Dictionary<AbstractType, int>(transaction._changed.Count);
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
        var ordered = new List<KeyValuePair<AbstractType, IList<YEvent>>>(changedParentTypes.Count);
        var seen = new HashSet<AbstractType>();
        foreach (var kvp in transaction._changed)
        {
            if (!HasOwnEvent(transaction, kvp.Key))
            {
                continue;
            }

            for (var type = kvp.Key; type != null; type = type._item?.Parent as AbstractType)
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

    private static bool HasOwnEvent(Transaction transaction, AbstractType type)
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
    /// <item>what follows them (<see cref="YDoc.AfterTransactionCleanup"/>, <see cref="YDoc.Update"/>,
    /// <see cref="YDoc.UpdateV2"/>, <see cref="YDoc.SubdocsChanged"/>, destroying the removed subdocuments), each step
    /// even if one before threw. Yjs lets such an exception escape at once and never finishes the batch, so its
    /// document cleans up no later transaction (yjs#799); Ycs does what the open yjs#803 does.</item>
    /// </list>
    /// Within one event, a throwing handler skips that event's later handlers (Yjs' <c>emit</c>).
    /// </summary>
    internal static void CleanupTransactions(YDoc doc)
    {
        var transactionCleanups = doc._transactionCleanups;
        ExceptionDispatchInfo error = null;
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
    private static ExceptionDispatchInfo CleanupTransaction(Transaction transaction)
    {
        var doc = transaction._doc;
        var store = doc.Store;
        var ds = transaction._deleteSet;
        var mergeStructs = transaction._mergeStructs;
        ExceptionDispatchInfo error = null;

        doc._transaction = null;

        if (transaction.HasRunExtensions)
        {
            // Whoever is called from here on with the transaction or its events sees Yjs' items, and so does a garbage
            // collection filter (once per item); when nobody is, the runs stay as they are, which is what the merge
            // below would make of them.
            if (doc.HasTransactionListeners || doc.HasBeforeObserverCallsListeners || AnyObserved(transaction)
                || (doc.Gc && doc.GcFilter != YDocOptions.DefaultGcFilter && ds.Clients.Count > 0))
            {
                transaction.MaterializeRunExtensions();
            }
            else
            {
                transaction._runExtensions.Clear();
            }
        }

        try
        {
            ds.SortAndMergeDeleteSet();
            store.GetStateVector(transaction._afterState);

            // An exception here skips every observer call, as in Yjs.
            doc.InvokeOnBeforeObserverCalls(transaction);

            try
            {
                foreach (var kvp in transaction._changed)
                {
                    var itemType = kvp.Key;
                    if (itemType._item == null || !itemType._item.Deleted)
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
                    if (type.HasDeepObservers && (type._item == null || !type._item.Deleted))
                    {
                        // As Yjs: the events whose target was deleted meanwhile are left out (the
                        // observer is still called when none is left).
                        var delivered = events;
                        for (var i = 0; i < events.Count; i++)
                        {
                            if (events[i].Target._item?.Deleted == true)
                            {
                                delivered = events.Where(static e => e.Target._item == null || !e.Target._item.Deleted).ToList();
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
        if (doc.Gc)
        {
            ds.TryGcDeleteSet(store, doc.GcFilter);
        }

        ds.TryMergeDeleteSet(store);

        // On all affected store.clients props, try to merge.
        var beforeState = transaction._beforeState;
        foreach (var kvp in transaction._afterState)
        {
            var client = kvp.Key;
            var clock = kvp.Value;

            if (!beforeState.TryGetValue(client, out var beforeClock))
            {
                beforeClock = 0;
            }

            if (beforeClock != clock)
            {
                var structs = store.Clients[client];
                var firstChangePos = Math.Max(StructStore.FindIndexSS(structs, beforeClock), 1);
                for (int j = structs.Count - 1; j >= firstChangePos;)
                {
                    j -= 1 + DeleteSet.TryToMergeWithLefts(store, structs, j);
                }
            }
        }

        // Try to merge mergeStructs.
        // TODO: It makes more sense to transform mergeStructs to a DS, sort it, and merge from right to left
        //       but at the moment DS does not handle duplicates.
        for (int j = mergeStructs.Count - 1; j >= 0; j--)
        {
            var client = mergeStructs[j].Id.Client;
            var clock = mergeStructs[j].Id.Clock;
            var structs = store.Clients[client];
            var replacedStructPos = StructStore.FindIndexSS(structs, clock);

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

        if (StructStore.CheckAfterTransactionsForTests)
        {
            store.CheckForTests();
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
                // Debug.WriteLine($"{nameof(Transaction)}: Changed the client-id because another client seems to be using it.");
            }
        }

        // @todo: Merge all the transactions into one and provide send the data as a single update message.
        // Unlike Yjs, a throwing handler skips none of the later steps, nor the cleanup of the next transaction.
        Step(static t => t._doc.InvokeOnAfterTransactionCleanup(t), transaction, ref error);
        Step(static t => t._doc.InvokeUpdateV1(t), transaction, ref error);
        Step(static t => t._doc.InvokeUpdateV2(t), transaction, ref error);

        if (transaction._subdocsAdded?.Count > 0 || transaction._subdocsRemoved?.Count > 0 || transaction._subdocsLoaded?.Count > 0)
        {
            var added = transaction.SubdocsAdded;
            var removed = transaction.SubdocsRemoved;

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

            Step(static t => t._doc.InvokeSubdocsChanged(t.SubdocsLoaded, t.SubdocsAdded, t.SubdocsRemoved), transaction, ref error);
            Step(static t =>
            {
                foreach (var subDoc in t.SubdocsRemoved)
                {
                    subDoc.Destroy();
                }
            }, transaction, ref error);
        }

        return error;
    }

    private static void Step(Action<Transaction> step, Transaction transaction, ref ExceptionDispatchInfo error)
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

        var meta = metaObj as ISet<Snapshot>;
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
                    store.GetItemCleanStart(transaction, new ID(client, clock));
                }
            }

            snapshot.DeleteSet.IterateDeletedStructs(transaction, static _ => true);

            meta.Add(snapshot);
        }
    }

    /// <returns>Whether the data was written.</returns>
    internal bool WriteUpdateMessageFromTransaction(IUpdateEncoder encoder)
    {
        if (_deleteSet.Clients.Count == 0 && !StateChanged())
        {
            return false;
        }

        _deleteSet.SortAndMergeDeleteSet();
        EncodingUtils.WriteClientsStructs(encoder, _doc.Store, _beforeState);
        _deleteSet.Write(encoder);

        return true;
    }

    private bool StateChanged()
    {
        foreach (var kvp in _afterState)
        {
            if (!_beforeState.TryGetValue(kvp.Key, out var clockB) || kvp.Value != clockB)
            {
                return true;
            }
        }

        return false;
    }
}
