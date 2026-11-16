// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Buffers;

namespace Ycs;

public partial class YDoc
{
    /// <summary>The one the options named, or a random one made when it is first read (a document that never reads it costs none).</summary>
    public string Guid => _guid ??= _opts.Guid ?? System.Guid.NewGuid().ToString("D");
    public bool GarbageCollection => _opts.GarbageCollection;
    public bool AutoLoad => _opts.AutoLoad;
    public YValue Meta => _opts.Meta;

    internal Func<ItemView, bool> GcFilter => _opts.GarbageCollectionFilter ?? YDocOptions.DefaultGcFilter;

    /// <summary>What an applied update may cost (<see cref="YDocOptions.Limits"/>).</summary>
    public UpdateLimits Limits => _opts.Limits;
    public string CollectionId { get; internal set; }

    /// <summary>Whether the document should be synced by the provider now (Yjs' <c>shouldLoad</c>); <see cref="Load"/> sets it.</summary>
    public bool ShouldLoad { get; private set; }

    public YDoc(YDocOptions options = null)
    {
        _opts = options ?? new YDocOptions();
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
        if (!ShouldLoad)
        {
            var parentDoc = _itemCollected ? _collectedParentDoc : _item.IsNull ? null : (_item.Parent as AbstractType).Doc;
            parentDoc?.Transact(this, static (doc, tr) => tr.SubdocsLoaded.Add(doc));
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
        if (_itemCollected)
        {
            _itemCollected = false;
            _item = default;
            _collectedParentDoc.Transact(this, static (doc, tr) => tr.SubdocsRemoved.Add(doc));
        }
        else if (!item.IsNull)
        {
            _item = default;
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
    public event EventHandler<IReadOnlyList<Transaction>> AfterAllTransactions;
    /// <summary>
    /// Fires after each transaction that changed the document, with the change as an update in the
    /// original ('v1') format: Yjs' <c>doc.on('update')</c>, what every y-protocols provider sends.
    /// A handler that throws skips the later handlers, so they miss this update: whoever relied on them must
    /// resync (send the state, or a diff by state vector). Unlike Yjs, the document still finishes the cleanup and
    /// stays usable; the exception escapes from the outermost <c>Transact</c> or <c>ApplyUpdate</c> afterwards.
    /// </summary>
    public event EventHandler<UpdateEventArgs> Updated;

    /// <summary>
    /// Like <see cref="Updated"/>, with the update in the v2 format: Yjs' <c>doc.on('updateV2')</c>.
    /// </summary>
    public event EventHandler<UpdateEventArgs> UpdatedV2;
    public event EventHandler Destroyed;

    /// <summary>
    /// Raised, inside the transaction of an <c>ApplyUpdate</c> that returns normally, when that update let waiting
    /// structs of earlier updates integrate and they failed a check (nesting, garbage collection): they are dropped
    /// (kept, every later retry would fail again), the update itself stands whole. Clients that were handed them as
    /// waiting structs may integrate them under other limits. No user content is passed.
    /// </summary>
    public event EventHandler<PendingDrop> PendingDropped;
    public event EventHandler<SubdocsEventArgs> SubdocsChanged;

    public long ClientId { get; internal set; }

    /// <summary>
    /// Whether received structs or deletions wait for an update this document has not received (Yjs'
    /// <c>doc.store.pendingStructs</c> and <c>pendingDs</c>). What waits is not in the document yet; it is
    /// integrated when the missing update arrives, and updates that do not depend on it are integrated
    /// meanwhile. <see cref="EncodeStateAsUpdate(ReadOnlySpan{byte}, UpdateFormat)"/> includes it, as Yjs does.
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
        }

        try
        {
            if (initialCall)
            {
                // Unlike Yjs, inside the 'try' (as the open yjs#803): a throwing handler skips 'action', and the
                // transaction is cleaned up as an empty one instead of staying the document's forever.
                if (_transactionCleanups.Count == 1)
                {
                    InvokeBeforeAllTransactions();
                }

                InvokeOnBeforeTransaction(transaction);
            }

            action(state, transaction);
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
                    // Observer calls may create new transactions for which we need to call the observers and do
                    // cleanup. As in Yjs, an exception of the cleanup replaces one of 'action' (or of a handler above).
                    Transaction.CleanupTransactions(this);
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

    internal T Get<T>(string name)
        where T : YType
    {
        if (!_share.TryGetValue(name, out var type))
        {
            type = CreateRoot<T>();
            type.Integrate(this, default);
            _share[name] = type;
        }

        // Remote type is realized when this method is called.
        // As Yjs' constructor check: a subclass (YXmlElement for YXmlFragment) is another type.
        if (typeof(T) != typeof(YType) && type.GetType() != typeof(T))
        {
            if (type is YTypePlaceholder)
            {
                var t = CreateRoot<T>();
                t._map = type._map;

                foreach (var kvp in type._map)
                {
                    var n = kvp.Value;
                    for (; !n.IsNull; n = n.Left.AsItem)
                    {
                        n.Parent = t;
                    }
                }

                t._start = type._start;
                for (var n = t._start; !n.IsNull; n = n.Right.AsItem)
                {
                    n.Parent = t;
                }

                t.Length = type.Length;

                _share[name] = t;
                t.Integrate(this, default);
                return t;
            }
            else
            {
                throw new InvalidOperationException($"Type with the name {name} has already been defined with a different constructor");
            }
        }

        return (T)type;
    }

    // No reflection: the root types are a closed set. YType asks for the placeholder of a type the document learned
    // of from an update before anyone defined it.
    private static T CreateRoot<T>()
        where T : YType
    {
        YType created =
            typeof(T) == typeof(YArray) ? new YArray() :
            typeof(T) == typeof(YMap) ? new YMap() :
            typeof(T) == typeof(YText) ? new YText() :
            typeof(T) == typeof(YXmlFragment) ? new YXmlFragment() :
            typeof(T) == typeof(YXmlElement) ? new YXmlElement() :
            typeof(T) == typeof(YXmlText) ? new YXmlText() :
            typeof(T) == typeof(YType) ? new YTypePlaceholder() :
            throw new InvalidOperationException($"{typeof(T).Name} is not a type a document can define by name");
        return (T)created;
    }
}
