// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.ExceptionServices;

namespace Ycs;

public abstract partial class YType
{
    // The item the type is in. Once the type is collected (Collect), nothing but the XML siblings' links (NextSibling,
    // PrevSibling, InsertAfter) and StructStore.IntegrationPlan's keys may read it; the rest asks IsRoot, ParentType,
    // ItemId, ItemParentSub, ItemDeleted or ItemKeep.
    private protected YType()
    {
    }

    internal ItemRef _item;
    internal ItemRef _start;
    internal Dictionary<string, ItemRef> _map = new Dictionary<string, ItemRef>();

    // The facts of the item a collected type was in. Its struct can merge away once the type is collected, and its slot
    // be reused, while user code still holds the type: Collect sets them before the struct is replaced; none changes later.
    private bool _collected;
    private YType _collectedParent;
    private ID _collectedId;
    private string _collectedParentSub;
    private bool _collectedKeep;

    internal bool IsRoot => !_collected && _item.IsNull;

    /// <summary>Whether the type was collected: its facts are kept, its item may be gone.</summary>
    internal bool IsCollected => _collected;

    internal YType ParentType => _collected ? _collectedParent : _item.IsNull ? null : _item.Parent as YType;

    /// <summary>The id of the item the type is in; a root type has none.</summary>
    internal ID ItemId => _collected ? _collectedId : _item.Id;

    /// <summary>Whether the type's item is deleted: a collected type's is, and a root type has none.</summary>
    internal bool ItemDeleted => _collected || (!_item.IsNull && _item.Deleted);

    /// <summary>The key of the item the type is in, null in a list.</summary>
    internal string ItemParentSub => _collected ? _collectedParentSub : _item.ParentSub;

    /// <summary>The item's keep flag; a collected type's item is deleted, so its keep only decides where <see cref="KeepItemAndParents"/> stops.</summary>
    internal bool ItemKeep
    {
        get => _collected ? _collectedKeep : _item.Keep;
        set
        {
            if (_collected)
            {
                _collectedKeep = value;
            }
            else
            {
                _item.Keep = value;
            }
        }
    }

    /// <summary>Sets the keep flag of this type's item and of the items above it, up to the first that has it (Yjs' <c>keepItem</c> on the item's parents).</summary>
    internal void KeepItemAndParents(bool value)
    {
        for (var type = this; !type.IsRoot && type.ItemKeep != value; type = type.ParentType)
        {
            type.ItemKeep = value;
        }
    }

    internal void Collect()
    {
        if (_collected || _item.IsNull)
        {
            return;
        }

        _collectedParent = _item.Parent as YType;
        _collectedId = _item.Id;
        _collectedParentSub = _item.ParentSub;
        _collectedKeep = _item.Keep;
        _collected = true;
        if (PoisonCollectedHandleForTests)
        {
            _item = PoisonItem();
        }
    }

    // Test switch: after collection the type's handle names an unrelated live item, as a reused slot would (7.6), so
    // any read of the struct instead of the facts changes behaviour.
    internal static bool PoisonCollectedHandleForTests;

    internal static ItemRef PoisonItem() =>
        ItemRef.New(new ID(424242, 7), default, default, new YMap(), "POISON", new ContentAny(new object[] { "poison" }));

    // How many keys of _map have a value that is not deleted, kept by ItemRef.Integrate and ItemRef.Delete.
    internal int _mapCount;

    /// <summary>How deeply this type is nested in other types: 0 for a root type (see <see cref="UpdateLimits.MaxDepth"/>).</summary>
    internal int Depth { get; private set; }

    internal virtual void Integrate(YDoc doc, ItemRef item)
    {
        Doc = doc;
        _item = item;
        _collected = false;
        Depth = !item.IsNull && item.Parent is YType parent ? parent.Depth + 1 : 0;

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

    internal virtual YType InternalCopy() { throw new NotImplementedException(); }
    internal virtual YType InternalClone() { throw new NotImplementedException(); }

    internal virtual void Write(IUpdateEncoder encoder) { throw new NotImplementedException(); }

    /// <summary>
    /// For a type whose JSON holds the JSON of the types nested in it (a map, an array): its JSON without them,
    /// each listed in <paramref name="nested"/> with where it goes; null for any other type.
    /// </summary>
    internal virtual object ShallowJson(List<(YType Type, object Container, string Key, int Index)> nested) => null;

    /// <summary>
    /// <see cref="ToJson"/> of <paramref name="root"/> and the maps and arrays nested in it, without recursion: they
    /// may be nested as deep as an update allows (<see cref="UpdateLimits.MaxDepth"/>).
    /// </summary>
    internal static object JsonTree(YType root)
    {
        var nested = new List<(YType Type, object Container, string Key, int Index)>();
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

    // The observers of this type's own events (the leaf types' typed Observe wraps each handler) and of the events of
    // it and its descendants.
    private Action<YEvent> _observers;
    private Action<IReadOnlyList<YEvent>, Transaction> _deepObservers;

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
        if (_observers != null || transaction.Doc.HasTransactionListeners)
        {
            return true;
        }

        for (var type = this; type != null; type = type.ParentType)
        {
            if (type._deepObservers != null)
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

            if (type.IsRoot)
            {
                break;
            }

            type = type.ParentType;
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

    internal ItemRef _First()
    {
        var n = _start;
        while (!n.IsNull && n.Deleted)
        {
            n = n.Right.AsItem;
        }
        return n;
    }

    internal bool HasDeepObservers => _deepObservers != null;

    internal void InvokeEventHandlers(YEvent evt, Transaction transaction)
    {
        var handler = _observers;
        if (handler != null && !transaction._buildingSkippedEvents)
        {
            CallAll(handler, evt);
        }
    }

    internal void CallDeepEventHandlerListeners(IList<YEvent> events, Transaction transaction)
    {
        var handler = _deepObservers;
        if (handler != null)
        {
            CallAll(handler, (IReadOnlyList<YEvent>)events, transaction);
        }
    }

    // Yjs' callEventHandlerListeners (lib0 callAll): every handler runs even if one before threw, and
    // the exception thrown last propagates afterwards.
    private static void CallAll(Action<YEvent> handler, YEvent evt)
    {
        if (handler.HasSingleTarget)
        {
            handler(evt);
            return;
        }

        ExceptionDispatchInfo error = null;
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try
            {
                h(evt);
            }
            catch (Exception e)
            {
                error = ExceptionDispatchInfo.Capture(e);
            }
        }

        error?.Throw();
    }

    private static void CallAll(Action<IReadOnlyList<YEvent>, Transaction> handler, IReadOnlyList<YEvent> events, Transaction transaction)
    {
        if (handler.HasSingleTarget)
        {
            handler(events, transaction);
            return;
        }

        ExceptionDispatchInfo error = null;
        foreach (var h in Delegate.EnumerateInvocationList(handler))
        {
            try
            {
                h(events, transaction);
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

    private protected IEnumerable<KeyValuePair<string, ItemRef>> TypeMapEnumerate() => _map.Where(kvp => !kvp.Value.Deleted);

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
}
