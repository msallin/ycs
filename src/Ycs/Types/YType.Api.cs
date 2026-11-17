// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

namespace Ycs;

public abstract partial class YType
{
    public YDoc? Doc { get; private protected set; }
    public YType? Parent => ParentType;

    internal virtual int Length { get; set; }

    /// <summary>
    /// The JSON tree of this type as the document holds it (Yjs' <c>toJSON()</c>): a string for text and XML, a
    /// <see cref="List{T}"/> for an array, a <see cref="Dictionary{TKey, TValue}"/> for a map, nested types already replaced;
    /// the values are stored values (see <see cref="ValueJson"/>).
    /// </summary>
    internal abstract object ToJsonTree();

    /// <summary>
    /// Writes this type as JSON, the form of Yjs' <c>toJSON()</c>: text and XML as a string (<see cref="object.ToString"/>), an array
    /// as an array, a map as an object, nested types the same way. See <see cref="YValue.WriteTo"/> for the other values.
    /// </summary>
    /// <exception cref="InvalidOperationException">The type is a root the document has not been asked for by its kind yet.</exception>
    public void WriteTo(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ValueJson.Write(writer, ToJsonTree());
    }

    /// <summary>The JSON text of this type, as <see cref="WriteTo"/> writes it; see <see cref="YValue.ToJsonString"/> for the defaults of <paramref name="options"/>.</summary>
    public string ToJsonString(System.Text.Json.JsonWriterOptions options = default) => ValueJson.ToString(ToJsonTree(), options);

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
        if (TryGetMapItem(key, out var c))
        {
            c.Delete(transaction);
        }
    }

    private protected void TypeMapSet(Transaction transaction, string key, object? value)
    {
        if (!TryGetMapItem(key, out var left))
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
                    d.CheckCanInsertInto(transaction.Doc);
                    content = new ContentDoc(d);
                    break;
                case YType at:
                    CheckCanInsert(at);
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

        var newItem = ItemRef.New(doc.Store.Slab, new ID(ownClientId, doc.Store.GetState(ownClientId)), left, default, this, key, content);
        newItem.Integrate(transaction, 0);
    }

    private protected bool TryTypeMapGet(string key, out object? value)
    {
        if (TryGetMapItem(key, out var val) && !val.Deleted)
        {
            value = val.StoredValueAt(val.Length - 1);
            return true;
        }

        value = default;
        return false;
    }

    private protected IEnumerable<KeyValuePair<string, object?>> TypeMapEnumerateValues()
    {
        foreach (var kvp in _map)
        {
            var item = MapItem(kvp.Value);
            if (item.Deleted)
            {
                continue;
            }

            var key = kvp.Key;
            var value = item.StoredValueAt(item.Length - 1);
            yield return new KeyValuePair<string, object?>(key, value);
        }
    }
}
