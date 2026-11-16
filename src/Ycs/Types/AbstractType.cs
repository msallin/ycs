// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.ExceptionServices;

namespace Ycs;

public class YEventArgs
{
    internal YEventArgs(YEvent evt, Transaction transaction)
    {
        Event = evt;
        Transaction = transaction;
    }

    public YEvent Event { get; }
    public Transaction Transaction { get; }
}

public class YDeepEventArgs
{
    internal YDeepEventArgs(IList<YEvent> events, Transaction transaction)
    {
        Events = events;
        Transaction = transaction;
    }

    public IList<YEvent> Events { get; }
    public Transaction Transaction { get; }
}

public partial class AbstractType
{
    internal Item _item = null;
    internal Item _start = null;
    internal Dictionary<string, Item> _map = new Dictionary<string, Item>();

    // How many keys of _map have a value that is not deleted, kept by Item.Integrate and Item.Delete.
    internal int _mapCount;

    public event EventHandler<YEventArgs> EventHandler;
    public event EventHandler<YDeepEventArgs> DeepEventHandler;

    public YDoc Doc { get; protected set; }
    public AbstractType Parent => _item != null ? _item.Parent as AbstractType : null;

    public virtual int Length { get; internal set; }

    /// <summary>How deeply this type is nested in other types: 0 for a root type (see <see cref="UpdateLimits.MaxDepth"/>).</summary>
    internal int Depth { get; private set; }

    internal virtual void Integrate(YDoc doc, Item item)
    {
        Doc = doc;
        _item = item;
        Depth = item?.Parent is AbstractType parent ? parent.Depth + 1 : 0;

        // A root type that was read from an update before it was defined takes over the map of
        // its placeholder (YDoc.Get).
        _mapCount = TypeMapCount();
    }

    /// <summary>Throws when a type inserted into this one would be nested deeper than the document's <see cref="UpdateLimits.MaxDepth"/>.</summary>
    internal void CheckCanNest()
    {
        var maxDepth = (Doc?.Limits ?? UpdateLimits.Default).MaxDepth;
        if (Depth + 1 > maxDepth)
        {
            throw new InvalidOperationException($"Shared types cannot be nested deeper than {maxDepth} levels (UpdateLimits.MaxDepth).");
        }
    }

    internal virtual AbstractType InternalCopy() { throw new NotImplementedException(); }
    internal virtual AbstractType InternalClone() { throw new NotImplementedException(); }

    internal virtual void Write(IUpdateEncoder encoder) { throw new NotImplementedException(); }

    /// <summary>
    /// Transforms this Shared Type into a JSON-compatible representation (mirrors Yjs' <c>toJSON()</c>).
    /// Must be implemented by each concrete type.
    /// </summary>
    public virtual object ToJson() { throw new NotImplementedException(); }

    /// <summary>
    /// For a type whose JSON holds the JSON of the types nested in it (a map, an array): its JSON without them,
    /// each listed in <paramref name="nested"/> with where it goes; null for any other type.
    /// </summary>
    internal virtual object ShallowJson(List<(AbstractType Type, object Container, string Key, int Index)> nested) => null;

    /// <summary>
    /// <see cref="ToJson"/> of <paramref name="root"/> and the maps and arrays nested in it, without recursion: they
    /// may be nested as deep as an update allows (<see cref="UpdateLimits.MaxDepth"/>).
    /// </summary>
    internal static object JsonTree(AbstractType root)
    {
        var nested = new List<(AbstractType Type, object Container, string Key, int Index)>();
        var result = root.ShallowJson(nested);
        while (nested.Count > 0)
        {
            var (type, container, key, index) = nested[^1];
            nested.RemoveAt(nested.Count - 1);
            var json = type.ShallowJson(nested) ?? type.ToJson();
            if (container is List<object> list)
            {
                list[index] = json;
            }
            else
            {
                ((Dictionary<string, object>)container)[key] = json;
            }
        }

        return result;
    }

    /// <summary>
    /// Observe all events that are created on this type (Yjs' <c>observe(f)</c>). Disposing the
    /// result unsubscribes, which is handy for lambdas; <c>+=</c>/<c>-=</c> on <see cref="EventHandler"/>
    /// and <see cref="Unobserve"/> work as well.
    /// </summary>
    public IDisposable Observe(EventHandler<YEventArgs> handler)
    {
        EventHandler += handler;
        return new Subscription(() => EventHandler -= handler);
    }

    /// <summary>
    /// Unregister an observer function previously registered with <see cref="Observe"/> (or <c>+=</c>).
    /// </summary>
    /// <remarks>As Yjs' <c>unobserve</c>: a handler registered more than once is removed every time.</remarks>
    public void Unobserve(EventHandler<YEventArgs> handler)
    {
        EventHandler<YEventArgs> before;
        do
        {
            before = EventHandler;
            EventHandler -= handler;
        }
        while (!ReferenceEquals(before, EventHandler));
    }

    /// <summary>
    /// Observe all events that are created by this type and its children (Yjs' <c>observeDeep(f)</c>).
    /// Disposing the result unsubscribes.
    /// </summary>
    public IDisposable ObserveDeep(EventHandler<YDeepEventArgs> handler)
    {
        DeepEventHandler += handler;
        return new Subscription(() => DeepEventHandler -= handler);
    }

    /// <summary>
    /// Unregister a deep observer function previously registered with <see cref="ObserveDeep"/> (or <c>+=</c>).
    /// </summary>
    /// <remarks>As Yjs' <c>unobserveDeep</c>: a handler registered more than once is removed every time.</remarks>
    public void UnobserveDeep(EventHandler<YDeepEventArgs> handler)
    {
        EventHandler<YDeepEventArgs> before;
        do
        {
            before = DeepEventHandler;
            DeepEventHandler -= handler;
        }
        while (!ReferenceEquals(before, DeepEventHandler));
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        private Action _unsubscribe = unsubscribe;

        public void Dispose()
        {
            _unsubscribe?.Invoke();
            _unsubscribe = null;
        }
    }

    /// <summary>
    /// Whether an event for a change of this type in <paramref name="transaction"/> reaches anyone: a
    /// handler on this type, a deep handler on it or an ancestor, or a document-level transaction
    /// listener (<see cref="YDoc.HasTransactionListeners"/>). When not, <see cref="CallObserver"/>
    /// builds no event, and the type and its ancestors are not added to
    /// <see cref="Transaction.ChangedParentTypes"/>.
    /// </summary>
    internal bool IsObserved(Transaction transaction)
    {
        if (EventHandler != null || transaction.Doc.HasTransactionListeners)
        {
            return true;
        }

        for (var type = this; type != null; type = type._item?.Parent as AbstractType)
        {
            if (type.DeepEventHandler != null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Call event listeners with an event. This will also add an event to all parents
    /// for observeDeep handlers.
    /// </summary>
    internal virtual void CallTypeObservers(Transaction transaction, YEvent evt)
    {
        var type = this;

        while (true)
        {
            if (!transaction.ChangedParentTypes.TryGetValue(type, out var values))
            {
                values = new List<YEvent>();
                transaction.ChangedParentTypes[type] = values;
            }

            values.Add(evt);

            if (type._item == null)
            {
                break;
            }

            type = type._item.Parent as AbstractType;
        }

        InvokeEventHandlers(evt, transaction);
    }

    /// <summary>
    /// Creates YEvent and calls all type observers.
    /// Must be implemented by each type.
    /// </summary>
    internal virtual void CallObserver(Transaction transaction, ISet<string> parentSubs)
    {
        // Do nothing.
    }

    internal Item _First()
    {
        var n = _start;
        while (n != null && n.Deleted)
        {
            n = n.Right as Item;
        }
        return n;
    }

    internal bool HasDeepObservers => DeepEventHandler != null;

    internal void InvokeEventHandlers(YEvent evt, Transaction transaction)
    {
        var handler = EventHandler;
        if (handler != null && !transaction._buildingSkippedEvents)
        {
            CallAll(handler, this, new YEventArgs(evt, transaction));
        }
    }

    internal void CallDeepEventHandlerListeners(IList<YEvent> events, Transaction transaction)
    {
        var handler = DeepEventHandler;
        if (handler != null)
        {
            CallAll(handler, this, new YDeepEventArgs(events, transaction));
        }
    }

    // Yjs' callEventHandlerListeners (lib0 callAll): every handler runs even if one before threw, and
    // the exception thrown last propagates afterwards.
    private static void CallAll<TArgs>(EventHandler<TArgs> handler, object sender, TArgs args)
    {
        if (handler.HasSingleTarget)
        {
            handler(sender, args);
            return;
        }

        ExceptionDispatchInfo error = null;
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try
            {
                h(sender, args);
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        }

        error?.Throw();
    }

    // The name of a root type: it never changes, and finding it walks the document's root types.
    private string _rootTypeKey;

    internal string FindRootTypeKey()
    {
        return _rootTypeKey ??= Doc.FindRootTypeKey(this);
    }

    protected void TypeMapDelete(Transaction transaction, string key)
    {
        if (_map.TryGetValue(key, out var c))
        {
            c.Delete(transaction);
        }
    }

    protected void TypeMapSet(Transaction transaction, string key, object value)
    {
        if (!_map.TryGetValue(key, out var left))
        {
            left = null;
        }

        var doc = transaction.Doc;
        var ownClientId = doc.ClientId;
        IContent content;

        if (value == null)
        {
            content = ContentAny.Single(null);
        }
        else
        {
            switch (value)
            {
                case YDoc d:
                    content = new ContentDoc(d);
                    break;
                case AbstractType at:
                    CheckCanNest();
                    content = new ContentType(at);
                    break;
                case byte[] ba:
                    content = new ContentBinary(ba);
                    break;
                default:
                    AnyEncoding.Validate(value, transaction.Doc.Limits.MaxDepth);
                    content = ContentAny.Single(value);
                    break;
            }
        }

        var newItem = new Item(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, left?.LastId, null, null, this, key, content);
        newItem.Integrate(transaction, 0);
    }

    protected bool TryTypeMapGet(string key, out object value)
    {
        if (_map.TryGetValue(key, out var val) && !val.Deleted)
        {
            value = ValueAt(val.Content, val.Length - 1);
            return true;
        }

        value = default;
        return false;
    }

    protected object TypeMapGetSnapshot(string key, Snapshot snapshot)
    {
        if (!_map.TryGetValue(key, out var v))
        {
            v = null;
        }

        while (v != null && (!snapshot.StateVector.ContainsKey(v.Id.Client) || v.Id.Clock >= snapshot.StateVector[v.Id.Client]))
        {
            v = v.Left as Item;
        }

        return v != null && ItemRef.Of(v).IsVisible(snapshot) ? ValueAt(v.Content, v.Length - 1) : null;
    }

    protected IEnumerable<KeyValuePair<string, Item>> TypeMapEnumerate() => _map.Where(kvp => !kvp.Value.Deleted);

    /// <summary>
    /// The value at <paramref name="index"/> of <paramref name="content"/>; for 'any' content without the read-only wrapper.
    /// Null for collected content, as Yjs' <c>getContent()[index]</c> of <c>[]</c> is <c>undefined</c>.
    /// </summary>
    internal static object ValueAt(IContent content, int index) => content switch
    {
        ContentAny any => any.ValueAt(index),
        ContentJson json => json.ValueAt(index),
        ContentDeleted => null,
        _ => content.GetContent()[index],
    };

    /// <summary>How many keys have a value that is not deleted, by walking the map (<see cref="_mapCount"/> is kept instead).</summary>
    internal int TypeMapCount()
    {
        var count = 0;
        foreach (var value in _map.Values)
        {
            if (!value.Deleted)
            {
                count++;
            }
        }

        return count;
    }

    protected IEnumerable<KeyValuePair<string, object>> TypeMapEnumerateValues()
    {
        foreach (var kvp in TypeMapEnumerate())
        {
            var key = kvp.Key;
            var value = ValueAt(kvp.Value.Content, kvp.Value.Length - 1);
            yield return new KeyValuePair<string, object>(key, value);
        }
    }
}
