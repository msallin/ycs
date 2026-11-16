// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public abstract partial class YType
{
    public YDoc Doc { get; private protected set; }
    public YType Parent => ParentType;

    internal virtual int Length { get; set; }

    /// <summary>Transforms this shared type into a JSON-compatible representation (mirrors Yjs' <c>toJSON()</c>).</summary>
    public abstract object ToJson();

    /// <summary>
    /// Observe all events that are created by this type and its children (Yjs' <c>observeDeep(f)</c>): the
    /// events of one transaction, those of the outer types first. Disposing the result unsubscribes.
    /// </summary>
    public IDisposable ObserveDeep(Action<IReadOnlyList<YEvent>, Transaction> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _deepObservers += handler;
        return new Subscription(() => _deepObservers -= handler);
    }

    /// <summary>
    /// Registers <paramref name="handler"/> for the events of this type (Yjs' <c>observe(f)</c>); the leaf types
    /// expose it as <c>Observe</c> with their own event type. Disposing the result unsubscribes.
    /// </summary>
    private protected IDisposable AddObserver<TEvent>(Action<TEvent> handler)
        where TEvent : YEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        Action<YEvent> observer = e => handler((TEvent)e);
        _observers += observer;
        return new Subscription(() => _observers -= observer);
    }

    private protected void TypeMapDelete(Transaction transaction, string key)
    {
        if (_map.TryGetValue(key, out var c))
        {
            c.Delete(transaction);
        }
    }

    private protected void TypeMapSet(Transaction transaction, string key, object value)
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
                case YType at:
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

    private protected bool TryTypeMapGet(string key, out object value)
    {
        if (_map.TryGetValue(key, out var val) && !val.Deleted)
        {
            value = ValueAt(val.Content, val.Length - 1);
            return true;
        }

        value = default;
        return false;
    }

    private protected IEnumerable<KeyValuePair<string, object>> TypeMapEnumerateValues()
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
