// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// The sets in <see cref="Transaction.Changed"/>: per changed type, the map keys that changed, and
/// <c>null</c> when its list of children changed.
/// </summary>
internal static class ChangedKeys
{
    /// <summary>An empty set, for a type that is not in <see cref="Transaction.Changed"/>.</summary>
    public static readonly ISet<string> None = new System.Collections.ObjectModel.ReadOnlySet<string>(new HashSet<string>());

    /// <summary>
    /// The set of a type whose children changed and none of its keys: most changed types are lists
    /// (a text, an array), and they all share this one, read-only, instead of a set each.
    /// </summary>
    public static readonly ISet<string> ChildList = new System.Collections.ObjectModel.ReadOnlySet<string>(new HashSet<string> { null });

    /// <summary>
    /// Returns the set that records <paramref name="parentSub"/> on top of <paramref name="changed"/>
    /// (null for none yet): <see cref="ChildList"/>, <paramref name="changed"/> itself, or a new set.
    /// </summary>
    public static ISet<string> Add(ISet<string> changed, string parentSub)
    {
        if (changed == null)
        {
            return parentSub == null ? ChildList : new SingleKey(parentSub);
        }

        // The shared sets are read-only: a second key makes a set of its own.
        if (changed.IsReadOnly)
        {
            return changed.Contains(parentSub) ? changed : new HashSet<string>(changed) { parentSub };
        }

        changed.Add(parentSub);
        return changed;
    }

    /// <summary>
    /// The set of a type of which one key changed (most map changes: a set or a delete of one key), without the
    /// HashSet, which costs several times its size, until the set is changed: by a second key, or by a handler (Yjs'
    /// set is a mutable Set). Then it holds a HashSet and is one.
    /// </summary>
    private sealed class SingleKey : ISet<string>, IReadOnlySet<string>
    {
        private readonly string _key;
        private HashSet<string> _set;

        public SingleKey(string key) => _key = key;

        private HashSet<string> Set => _set ??= new HashSet<string> { _key };

        public int Count => _set?.Count ?? 1;

        public bool IsReadOnly => false;

        public bool Contains(string item) => _set?.Contains(item) ?? string.Equals(item, _key, StringComparison.Ordinal);

        public IEnumerator<string> GetEnumerator() => _set?.GetEnumerator() ?? One();

        private IEnumerator<string> One()
        {
            yield return _key;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void CopyTo(string[] array, int arrayIndex) => Set.CopyTo(array, arrayIndex);

        public bool IsProperSubsetOf(IEnumerable<string> other) => Set.IsProperSubsetOf(other);

        public bool IsProperSupersetOf(IEnumerable<string> other) => Set.IsProperSupersetOf(other);

        public bool IsSubsetOf(IEnumerable<string> other) => Set.IsSubsetOf(other);

        public bool IsSupersetOf(IEnumerable<string> other) => Set.IsSupersetOf(other);

        public bool Overlaps(IEnumerable<string> other) => Set.Overlaps(other);

        public bool SetEquals(IEnumerable<string> other) => Set.SetEquals(other);

        // The key again (a map entry set again in the transaction) changes nothing and makes no set.
        public bool Add(string item) => (_set != null || !Contains(item)) && Set.Add(item);

        void ICollection<string>.Add(string item) => Add(item);

        public void Clear() => Set.Clear();

        public bool Remove(string item) => Set.Remove(item);

        public void ExceptWith(IEnumerable<string> other) => Set.ExceptWith(other);

        public void IntersectWith(IEnumerable<string> other) => Set.IntersectWith(other);

        public void SymmetricExceptWith(IEnumerable<string> other) => Set.SymmetricExceptWith(other);

        public void UnionWith(IEnumerable<string> other) => Set.UnionWith(other);
    }

    /// <summary>The keys in <paramref name="changed"/> other than <c>null</c>, as a new set.</summary>
    public static HashSet<string> WithoutChildList(ISet<string> changed)
    {
        var keys = new HashSet<string>();
        if (changed != null)
        {
            foreach (var key in changed)
            {
                if (key != null)
                {
                    keys.Add(key);
                }
            }
        }

        return keys;
    }
}

public abstract partial class YEvent
{
    private YChanges _changes = null;
    private IReadOnlyDictionary<string, KeyChange> _keys = null;

    /// <summary>
    /// Every event, when it is made (the observers are called right after), compares the views of the structs of its
    /// target's lists, and its <see cref="Changes"/>, when collected, those of the added and deleted structs, with the
    /// structs (<see cref="ItemView.CheckEvent"/>); the tests switch it on.
    /// </summary>
    internal static bool CheckViewsForTests;

    private protected YEvent(YType target, Transaction transaction)
    {
        Target = target;
        CurrentTarget = target;
        Transaction = transaction;
        if (CheckViewsForTests)
        {
            ItemView.CheckEvent(this, null);
        }
    }

    internal bool Deletes(ItemRef str)
    {
        return Transaction.DeleteSet.IsDeleted(str.Id);
    }

    internal bool Adds(ItemRef str)
    {
        return !Transaction.BeforeState.TryGetValue(str.Id.Client, out var clock) || str.Id.Clock >= clock;
    }

    private IReadOnlyDictionary<string, KeyChange> CollectKeys()
    {
        if (_keys == null)
        {
            var target = Target;
            // Cached only once complete: a read after the transaction ended throws on every try.
            var keys = new Dictionary<string, KeyChange>();

            if (!Transaction.LiveChanged.TryGetValue(Target, out var changed))
            {
                changed = ChangedKeys.None;
            }

            foreach (var key in changed)
            {
                if (key != null)
                {
                    KeyChangeAction action;
                    object oldValue;
                    var item = target._map[key];

                    if (Adds(item))
                    {
                        var prev = item.Left;
                        while (!prev.IsNull && Adds(prev))
                        {
                            prev = prev.Left;
                        }

                        if (Deletes(item))
                        {
                            if (!prev.IsNull && Deletes(prev))
                            {
                                action = KeyChangeAction.Delete;
                                oldValue = LastValue(prev);
                            }
                            else
                            {
                                // Nothing to record for this key (added and deleted in the same transaction).
                                continue;
                            }
                        }
                        else
                        {
                            if (!prev.IsNull && Deletes(prev))
                            {
                                action = KeyChangeAction.Update;
                                oldValue = LastValue(prev);
                            }
                            else
                            {
                                action = KeyChangeAction.Add;
                                oldValue = null;
                            }
                        }
                    }
                    else
                    {
                        if (Deletes(item))
                        {
                            action = KeyChangeAction.Delete;
                            oldValue = LastValue(item);
                        }
                        else
                        {
                            // Nop: this key wasn't actually touched by this transaction.
                            continue;
                        }
                    }

                    keys[key] = new KeyChange(action, oldValue);
                }
            }

            _keys = keys;
        }

        return _keys;
    }

    private static object LastValue(ItemRef item) => AbstractType.ValueAt(item.Content, item.Length - 1);

    private enum DeltaRun
    {
        None,
        Delete,
        Insert,
        Retain,
    }

    private static void PackRun(List<Delta> delta, DeltaRun run, int count, StoredItems inserted)
    {
        switch (run)
        {
            case DeltaRun.Delete:
                delta.Add(new Delta.Delete(count));
                break;
            case DeltaRun.Insert:
                delta.Add(new Delta.InsertItems(inserted));
                break;
            case DeltaRun.Retain:
                delta.Add(new Delta.Retain(count));
                break;
        }
    }

    private YChanges CollectChanges()
    {
        if (_changes == null)
        {
            var target = Target;
            var added = new HashSet<ItemView>();
            var deleted = new HashSet<ItemView>();
            var delta = new List<Delta>();

            var changes = new YChanges(added, deleted, delta, CollectKeys());

            if (!Transaction.LiveChanged.TryGetValue(Target, out var changed))
            {
                changed = ChangedKeys.None;
            }

            if (changed.Contains(null))
            {
                // The run being collected: none, a deletion or a retain (counted), or an insertion (its items).
                var run = DeltaRun.None;
                var count = 0;
                StoredItems inserted = null;

                for (var item = Target._start; !item.IsNull; item = item.Right.AsItem)
                {
                    if (item.Deleted)
                    {
                        if (Deletes(item) && !Adds(item))
                        {
                            if (run != DeltaRun.Delete)
                            {
                                PackRun(delta, run, count, inserted);
                                run = DeltaRun.Delete;
                                count = 0;
                            }

                            count += item.Length;
                            deleted.Add(ItemView.Of(Target.Doc, item));
                        }
                    }
                    else if (Adds(item))
                    {
                        if (run != DeltaRun.Insert)
                        {
                            PackRun(delta, run, count, inserted);
                            run = DeltaRun.Insert;
                            inserted = new StoredItems();
                        }

                        inserted.AddRange(item.Content.GetContent());

                        added.Add(ItemView.Of(Target.Doc, item));
                    }
                    else
                    {
                        if (run != DeltaRun.Retain)
                        {
                            PackRun(delta, run, count, inserted);
                            run = DeltaRun.Retain;
                            count = 0;
                        }

                        count += item.Length;
                    }
                }

                // A trailing retain says nothing.
                if (run != DeltaRun.Retain)
                {
                    PackRun(delta, run, count, inserted);
                }
            }

            if (CheckViewsForTests)
            {
                ItemView.CheckEvent(this, changes);
            }

            _changes = changes;
        }

        return _changes;
    }

    /// <summary>The number of elements of <see cref="Path"/>, without walking the siblings to compute them.</summary>
    internal int PathLength
    {
        get
        {
            var length = 0;
            for (var child = Target; !child.IsRoot && child != CurrentTarget; child = child.ParentType)
            {
                length++;
            }

            return length;
        }
    }

    private PathSegment[] GetPathTo(YType parent, YType child)
    {
        var path = new PathSegment[PathLength];
        var at = path.Length;

        while (!child.IsRoot && child != parent)
        {
            if (child.ItemParentSub != null)
            {
                // Parent is map-ish.
                path[--at] = new PathSegment(child.ItemParentSub, 0);
            }
            else
            {
                // Parent is array-ish.
                int i = 0;
                var id = child.ItemId;
                var c = child.ParentType._start;
                while (!c.IsNull && !c.Id.Equals(id))
                {
                    var cItem = c.AsItem;
                    if (!cItem.IsNull && !cItem.Deleted && cItem.Countable)
                    {
                        i += cItem.Length;
                    }

                    c = cItem.IsNull ? default : cItem.Right;
                }

                path[--at] = new PathSegment(null, i);
            }

            child = child.ParentType;
        }

        return path;
    }
}
