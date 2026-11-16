// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public partial class AbstractType
{
    public event EventHandler<YEventArgs> EventHandler;
    public event EventHandler<YDeepEventArgs> DeepEventHandler;

    public YDoc Doc { get; protected set; }
    public AbstractType Parent => ParentType;

    public virtual int Length { get; internal set; }

    /// <summary>
    /// Transforms this Shared Type into a JSON-compatible representation (mirrors Yjs' <c>toJSON()</c>).
    /// Must be implemented by each concrete type.
    /// </summary>
    public virtual object ToJson() { throw new NotImplementedException(); }

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
            left = default;
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
                    content = new ContentBinary((byte[])ba.Clone());
                    break;
                default:
                    content = ContentAny.Single(ValueFreezer.Freeze(value, transaction.Doc.Limits.MaxDepth));
                    break;
            }
        }

        var newItem = ItemRef.New(new ID(ownClientId, doc.Store.GetState(ownClientId)), left, default, this, key, content);
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
            v = default;
        }

        while (!v.IsNull && (!snapshot.StateVector.ContainsKey(v.Id.Client) || v.Id.Clock >= snapshot.StateVector[v.Id.Client]))
        {
            v = v.Left.AsItem;
        }

        return !v.IsNull && v.IsVisible(snapshot) ? ValueAt(v.Content, v.Length - 1) : null;
    }

    protected IEnumerable<KeyValuePair<string, object>> TypeMapEnumerateValues()
    {
        foreach (var kvp in _map)
        {
            if (kvp.Value.Deleted)
            {
                continue;
            }

            var key = kvp.Key;
            var value = ValueAt(kvp.Value.Content, kvp.Value.Length - 1);
            yield return new KeyValuePair<string, object>(key, value);
        }
    }
}
