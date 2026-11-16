// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

public class ChangesCollection
{
    public ISet<Item> Added;
    public ISet<Item> Deleted;
    public IList<Delta> Delta;
    public IDictionary<string, ChangeKey> Keys;
}

public class Delta
{
    /// <summary>
    /// What <see cref="Insert"/> holds for an embed whose value is null (Yjs' <c>{ insert: null }</c>,
    /// which only another peer can write), since a null <see cref="Insert"/> means no insert: an embed
    /// of length 1, as every embed. <see cref="YText.ApplyDelta"/> inserts it as that embed.
    /// </summary>
    public static readonly object NullEmbed = new();

    public object Insert;
    public int? Delete;
    public int? Retain;
    public IDictionary<string, object> Attributes;
}

public enum ChangeAction
{
    Add,
    Update,
    Delete
}

public class ChangeKey
{
    public ChangeAction Action;
    public object OldValue;
}

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
    public static ISet<string> WithoutChildList(ISet<string> changed)
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

public partial class YEvent
{
    private ChangesCollection _changes = null;
    private IDictionary<string, ChangeKey> _keys = null;

    internal YEvent(AbstractType target, Transaction transaction)
    {
        Target = target;
        CurrentTarget = target;
        Transaction = transaction;
    }

    public AbstractType Target { get; set; }
    public AbstractType CurrentTarget { get; set; }
    public Transaction Transaction { get; set; }

    public IReadOnlyCollection<object> Path => GetPathTo(CurrentTarget, Target);

    /// <summary>
    /// Mirrors Yjs' <c>event.delta</c>: a shortcut for <c>Changes.Delta</c>. <see cref="YTextEvent"/>
    /// overrides this with its own Quill-style delta computation, exactly as Yjs does.
    /// </summary>
    public virtual IList<Delta> Delta => Changes.Delta;

    /// <summary>
    /// Mirrors Yjs' <c>event.keys</c>: the per-key add/update/delete summary for this event's target.
    /// This is the same map exposed via <see cref="Changes"/>.Keys, computed once and cached.
    /// </summary>
    public IDictionary<string, ChangeKey> Keys => CollectKeys();

    /// <summary>
    /// What changed (Yjs' <c>event.changes</c>): the added and deleted items, the list delta and the
    /// keys. A text overrides it with its own form (see <see cref="YTextEvent.Changes"/>).
    /// </summary>
    public virtual ChangesCollection Changes => CollectChanges();

    /// <summary>
    /// Check if a struct is deleted by this event.
    /// <br/>
    /// In contrast to <c>change.deleted</c>, this method also returns true if the struct was
    /// added and then deleted.
    /// </summary>
    public bool Deletes(AbstractStruct str) => Deletes(ItemRef.Of(str));

    internal bool Deletes(ItemRef str)
    {
        return Transaction.DeleteSet.IsDeleted(str.Id);
    }

    /// <summary>
    /// Check if a struct is added by this event.
    /// <br/>
    /// In contrast to <c>change.added</c>, this method also returns true if the struct was
    /// added and then deleted.
    /// </summary>
    public bool Adds(AbstractStruct str) => Adds(ItemRef.Of(str));

    internal bool Adds(ItemRef str)
    {
        return !Transaction.BeforeState.TryGetValue(str.Id.Client, out var clock) || str.Id.Clock >= clock;
    }

    private IDictionary<string, ChangeKey> CollectKeys()
    {
        if (_keys == null)
        {
            var target = Target;
            // Cached only once complete: a read after the transaction ended throws on every try.
            var keys = new Dictionary<string, ChangeKey>();

            if (!Transaction.Changed.TryGetValue(Target, out var changed))
            {
                changed = ChangedKeys.None;
            }

            foreach (var key in changed)
            {
                if (key != null)
                {
                    ChangeAction action;
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
                                action = ChangeAction.Delete;
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
                                action = ChangeAction.Update;
                                oldValue = LastValue(prev);
                            }
                            else
                            {
                                action = ChangeAction.Add;
                                oldValue = null;
                            }
                        }
                    }
                    else
                    {
                        if (Deletes(item))
                        {
                            action = ChangeAction.Delete;
                            oldValue = LastValue(item);
                        }
                        else
                        {
                            // Nop: this key wasn't actually touched by this transaction.
                            continue;
                        }
                    }

                    keys[key] = new ChangeKey { Action = action, OldValue = oldValue };
                }
            }

            _keys = keys;
        }

        return _keys;
    }

    private static object LastValue(ItemRef item) => AbstractType.ValueAt(item.Content, item.Length - 1);

    private ChangesCollection CollectChanges()
    {
        if (_changes == null)
        {
            var target = Target;
            var added = new HashSet<Item>();
            var deleted = new HashSet<Item>();
            var delta = new List<Delta>();

            var changes = new ChangesCollection
            {
                Added = added,
                Deleted = deleted,
                Delta = delta,
                Keys = CollectKeys()
            };

            if (!Transaction.Changed.TryGetValue(Target, out var changed))
            {
                changed = ChangedKeys.None;
            }

            if (changed.Contains(null))
            {
                Delta lastOp = null;

                void packOp()
                {
                    if (lastOp != null)
                    {
                        delta.Add(lastOp);
                    }
                }

                for (var item = Target._start; !item.IsNull; item = item.Right.AsItem)
                {
                    if (item.Deleted)
                    {
                        if (Deletes(item) && !Adds(item))
                        {
                            if (lastOp == null || lastOp.Delete == null)
                            {
                                packOp();
                                lastOp = new Delta { Delete = 0 };
                            }

                            lastOp.Delete += item.Length;
                            deleted.Add(item.ToItem());
                        }
                        else
                        {
                            // Do nothing.
                        }
                    }
                    else
                    {
                        if (Adds(item))
                        {
                            if (lastOp == null || lastOp.Insert == null)
                            {
                                packOp();
                                lastOp = new Delta { Insert = new List<object>(1) };
                            }

                            (lastOp.Insert as List<object>).AddRange(item.Content.GetContent());
                            added.Add(item.ToItem());
                        }
                        else
                        {
                            if (lastOp == null || lastOp.Retain == null)
                            {
                                packOp();
                                lastOp = new Delta { Retain = 0 };
                            }

                            lastOp.Retain += item.Length;
                        }
                    }
                }

                if (lastOp != null && lastOp.Retain == null)
                {
                    packOp();
                }
            }

            _changes = changes;
        }

        return _changes;
    }

    /// <summary>
    /// Compute the path from this type to the specified target.
    /// </summary>
    /// <summary>The number of elements of <see cref="Path"/>, without walking the siblings to compute them.</summary>
    internal int PathLength
    {
        get
        {
            var length = 0;
            for (var child = Target; !child._item.IsNull && child != CurrentTarget; child = child._item.Parent as AbstractType)
            {
                length++;
            }

            return length;
        }
    }

    private IReadOnlyCollection<object> GetPathTo(AbstractType parent, AbstractType child)
    {
        var path = new Stack<object>();

        while (!child._item.IsNull && child != parent)
        {
            if (child._item.ParentSub != null)
            {
                // Parent is map-ish.
                path.Push(child._item.ParentSub);
            }
            else
            {
                // Parent is array-ish.
                int i = 0;
                var c = (child._item.Parent as AbstractType)._start;
                while (c != child._item && !c.IsNull)
                {
                    var cItem = c.AsItem;
                    if (!cItem.IsNull && !cItem.Deleted && cItem.Countable)
                    {
                        i += cItem.Length;
                    }

                    c = cItem.IsNull ? default : cItem.Right;
                }

                path.Push(i);
            }

            child = child._item.Parent as AbstractType;
        }

        return path;
    }
}
