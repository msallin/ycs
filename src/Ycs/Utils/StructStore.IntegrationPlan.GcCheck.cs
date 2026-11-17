#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal sealed partial class StructStore
{
    internal sealed partial class IntegrationPlan
    {
        // The parent and key of the planned items without a parent (by their references), for the garbage collection check.
        private Dictionary<PlanItem, (object Parent, string Key)> _keys;
        private readonly List<(long Client, long Clock, long End)> _ranges = new();
        private readonly List<PlanItem> _keyChain = new();

        /// <summary>
        /// Rejects an update whose cleanup would throw: the garbage collection of a deleted type requires every item
        /// in it to be deleted (Yjs' Item.gc throws "Unexpected case" there, after integrating). The one item a deleted
        /// type can leave undeleted is a part of a key's value other than its last part: when a value of several items
        /// (Yjs never writes one undeleted) is split, by an origin or a right origin inside it or by a deleted range that
        /// starts or ends inside it, the key holds two live items, and deleting the type deletes only the last. Such a
        /// part, made by this update or kept from an earlier one (unseen, as in Yjs), fails the update that collects a
        /// type above it. Called only when the document collects garbage; see docs/api.md (Untrusted input).
        /// </summary>
        public void CheckGarbageCollection(YDoc doc, DeleteSet ds, PendingDeletes waiting)
        {
            // The ranges in the order ApplyDeleteSet takes them: the update's, then the waiting ones.
            var ranges = _ranges;
            ranges.Clear();
            foreach (var (client, list) in ds.Clients)
            {
                foreach (var range in list)
                {
                    ranges.Add((client, range.Clock, range.Clock + range.Length));
                }
            }

            waiting.AddApplicableRanges(this, ranges);
            var splits = MapValueSplits(ranges);
            if (splits == null && !_store.MaySplitMapValues)
            {
                return;
            }

            var deleted = DeletedTypes(doc, ranges);
            if (deleted == null)
            {
                return;
            }

            if (_store.MaySplitMapValues)
            {
                foreach (var type in deleted.Keys)
                {
                    if (type is { StoredParent: YType, NestedType: { } nested } && HasLiveNonValue(nested, doc, !type.Keep && doc.GcFilter(GcViewOf(doc, type)), ranges))
                    {
                        throw MapValueSplit();
                    }
                }
            }

            foreach (var part in LiveNonValues(doc, splits, ranges, deleted))
            {
                // Collected when a type above it is deleted now and collected (it, or one between them).
                var collected = false;
                // The walk stops at a collected type: the collection of a type above it does not enter it.
                for (var ancestor = KeyOf(part).Parent; ancestor is PlanItem type; ancestor = KeyOf(type).Parent)
                {
                    collected |= !type.Keep && doc.GcFilter(GcViewOf(doc, type));
                    if (collected && deleted.ContainsKey(type))
                    {
                        throw MapValueSplit();
                    }
                }
            }
        }

        // The filter is given a view of a planned item read from the staging table, or of a stored item.
        private static ItemView GcViewOf(YDoc doc, PlanItem item) => item.GcView(doc);

        private static YUpdateException MapValueSplit() => new(
            "The update deletes a type in which a key has two values (its value was split, by this update or an earlier one), " +
            "which the garbage collection cannot remove. The document is unchanged.");

        // The times of the events that split and delete items, in the order the update carries them out: the structs'
        // references split items (and a key's new item deletes its old value), then range k (from 1, in ApplyDeleteSet's
        // order) splits at its start, deletes what it covers, and splits at its end.
        private const int ReferenceSplit = 0;
        private const int ValueReplaced = 1;

        private static int RangeStart(int k) => 3 * k;

        private static int RangeDeletes(int k) => (3 * k) + 1;

        private static int RangeEnd(int k) => (3 * k) + 2;

        /// <summary>
        /// The boundaries (with their time) at which the update's references and ranges cut an item with a key that
        /// is not deleted, by item, or null when there is none (the common case: a key's value is one item).
        /// </summary>
        private Dictionary<PlanItem, List<(long Boundary, int Time)>> MapValueSplits(List<(long Client, long Clock, long End)> ranges)
        {
            Dictionary<PlanItem, List<(long Boundary, int Time)>> splits = null;
            foreach (var (index, _, _) in _steps)
            {
                if (_table.At(index).Kind != StagedKind.GC)
                {
                    if (_table.LeftOrigin(index) is ItemId left)
                    {
                        Split(ref splits, left, left.Clock + 1, ReferenceSplit);
                    }

                    if (_table.RightOrigin(index) is ItemId right)
                    {
                        Split(ref splits, right, right.Clock, ReferenceSplit);
                    }
                }
            }

            for (var k = 0; k < ranges.Count; k++)
            {
                var (client, start, end) = ranges[k];
                Split(ref splits, new ItemId(client, start), start, RangeStart(k + 1));
                if (end > start)
                {
                    Split(ref splits, new ItemId(client, end - 1), end, RangeEnd(k + 1));
                }
            }

            return splits;
        }

        /// <summary>Records <paramref name="boundary"/> if it falls inside the item that holds <paramref name="id"/> and that item may be a key's undeleted value.</summary>
        private void Split(ref Dictionary<PlanItem, List<(long Boundary, int Time)>> splits, ItemId id, long boundary, int time)
        {
            var (target, start) = Target(id);
            if (!target.IsNull && start < boundary && boundary < target.Id.Clock + target.Length
                && !target.Deleted && !target.HasDeletedContent && (target.ParentSub != null || !target.HasParent))
            {
                splits ??= new Dictionary<PlanItem, List<(long, int)>>();
                if (!splits.TryGetValue(target, out var list))
                {
                    splits[target] = list = new List<(long, int)>();
                }

                list.Add((boundary, time));
            }
        }

        /// <summary>
        /// The type items this update deletes by its ranges and the values of keys it replaces (when the new item is
        /// estimated to land last, see <see cref="LandsLast"/>), with the time they are deleted, or null.
        /// </summary>
        private Dictionary<PlanItem, int> DeletedTypes(YDoc doc, List<(long Client, long Clock, long End)> ranges)
        {
            Dictionary<PlanItem, int> deleted = null;

            void Add(PlanItem type, int time)
            {
                deleted ??= new Dictionary<PlanItem, int>();
                if (!deleted.TryGetValue(type, out var earlier) || earlier > time)
                {
                    deleted[type] = time;
                }
            }

            var chains = new KeyChains();
            foreach (var (index, offset, integrate) in _steps)
            {
                if (integrate && _table.At(index).Kind != StagedKind.GC && LandsLast(doc, PlanItem.Staged(_table, index), offset, chains) is { IsNull: false, Deleted: false } replaced)
                {
                    Add(replaced, ValueReplaced);
                }
            }

            for (var k = 0; k < ranges.Count; k++)
            {
                var (client, start, end) = ranges[k];
                var known = _store.GetState(client);
                end = Math.Min(end, GetState(client));
                for (var clock = start; clock < end;)
                {
                    PlanItem type;
                    long structEnd;
                    if (clock < known)
                    {
                        var found = _store.Find(new ItemId(client, clock));
                        type = PlanItem.Store(found);
                        structEnd = found.Id.Clock + found.Length;
                    }
                    else
                    {
                        var planned = FindPlanned(new ItemId(client, clock));
                        if (planned < 0)
                        {
                            break;
                        }

                        ref readonly var plannedStruct = ref _table.At(planned);
                        type = plannedStruct.Kind is StagedKind.GC or StagedKind.Skip ? default : PlanItem.Staged(_table, planned);
                        structEnd = plannedStruct.Clock + plannedStruct.Length;
                    }

                    if (!type.IsNull && !type.Deleted && type.IsType)
                    {
                        Add(type, RangeDeletes(k + 1));
                    }

                    clock = structEnd;
                }
            }

            return deleted;
        }

        /// <summary>
        /// For a planned item with a key: whether it lands last among its key's items (it becomes the key's value) and
        /// so deletes the value before it, which is returned (or null when there was none or it does not land last).
        /// <paramref name="chains"/> holds each key's first item and value so far, taken from the store and updated
        /// with the planned items in plan order (those estimated deleted are kept there too). Where an item lands is estimated as YATA orders the items of one
        /// origin (by client): before its right origin; before the rest of an item its origin splits, unless that rest
        /// is of a lower client; after an origin only when that is the end of the key's value; without origins, after
        /// the key's first item only when that item's client is lower. Items under a deleted type are deleted.
        /// </summary>
        private PlanItem LandsLast(YDoc doc, PlanItem item, int offset, KeyChains chains)
        {
            // Integrated from the store's state on, an item that overlaps the store has the item before as its origin.
            var leftOrigin = offset > 0 ? new ItemId(item.Id.Client, item.Id.Clock + offset - 1) : item.LeftOrigin;
            var (parent, key) = KeyOf(item);
            if (parent is PlanItem parentItem ? parentItem.Deleted || chains.Deleted.Contains(parentItem) : parent is YType { IsCollected: true } collectedParent && collectedParent.ItemDeleted)
            {
                chains.Deleted.Add(item);
                return default;
            }

            if (item.HasDeletedContent || key == null)
            {
                return default;
            }

            // A root is named by planned items and is the type itself for those in the store.
            if (parent is string name && doc.TryGetRoot(name, out var defined))
            {
                parent = defined;
            }

            if (!chains.Keys.TryGetValue((parent, key), out var chain))
            {
                var type = parent switch
                {
                    PlanItem { StoredParent: YType } typeItem => typeItem.NestedType,
                    YType root => root,
                    _ => null,
                };
                if (type != null && type.TryGetMapItem(key, out var value))
                {
                    var head = value;
                    while (head.Left.IsItem)
                    {
                        head = head.Left;
                    }

                    chain = (PlanItem.Store(head), PlanItem.Store(value));
                }
            }

            bool last;
            if (chain.Value.IsNull)
            {
                last = item.RightOrigin == null;
            }
            else if (item.RightOrigin != null)
            {
                last = false;
            }
            else if (leftOrigin is ItemId origin)
            {
                last = origin.Equals(chain.Value.LastId)
                    || (Target(origin).Item is { IsNull: false } left && !origin.Equals(left.LastId) && left.Id.Client < item.Id.Client && left == chain.Value);
            }
            else
            {
                last = chain.Head.Id.Client < item.Id.Client;
            }

            // Without origins and not last, it lands before the key's first item.
            var first = chain.Head.IsNull || (!last && leftOrigin == null) ? item : chain.Head;
            chains.Keys[(parent, key)] = (first, last ? item : chain.Value);
            if (!last)
            {
                chains.Deleted.Add(item);
            }

            return last ? chain.Value : default;
        }

        /// <summary>The state of <see cref="LandsLast"/>: per key its first item and value, and the planned items estimated deleted on integration.</summary>
        private sealed class KeyChains
        {
            public Dictionary<(object, string), (PlanItem Head, PlanItem Value)> Keys { get; } = new();

            public HashSet<PlanItem> Deleted { get; } = new();
        }

        /// <summary>
        /// Of the items <paramref name="splits"/> cuts, those that are a key's live value (or become one, see
        /// <see cref="LandsLast"/>) and keep a part other than the last undeleted: a boundary cuts a part only while
        /// that part is not deleted yet (by an earlier range, or as the key's value with a type above it).
        /// </summary>
        private List<PlanItem> LiveNonValues(YDoc doc, Dictionary<PlanItem, List<(long Boundary, int Time)>> splits, List<(long Client, long Clock, long End)> ranges, Dictionary<PlanItem, int> deleted)
        {
            var parts = new List<PlanItem>();
            if (splits == null)
            {
                return parts;
            }

            HashSet<PlanItem> values = null;
            foreach (var (target, events) in splits)
            {
                if (KeyOf(target).Key == null)
                {
                    continue;
                }

                if (target.StoredParent is not YType)
                {
                    if (values == null)
                    {
                        // The planned items that become a key's value when they integrate, a later planned item replacing them or not.
                        values = new HashSet<PlanItem>();
                        var chains = new KeyChains();
                        foreach (var (index, offset, integrate) in _steps)
                        {
                            if (integrate && _table.At(index).Kind != StagedKind.GC)
                            {
                                var item = PlanItem.Staged(_table, index);
                                LandsLast(doc, item, offset, chains);
                                if (!chains.Deleted.Contains(item) && !item.HasDeletedContent && KeyOf(item).Key != null)
                                {
                                    values.Add(item);
                                }
                            }
                        }
                    }

                    if (!values.Contains(target))
                    {
                        continue;
                    }
                }

                // When the value is replaced, or a type above it is deleted, its last part (then the key's value) is deleted.
                var typeDeleted = deleted.TryGetValue(target, out var replacedAt) ? replacedAt : int.MaxValue;
                for (var ancestor = KeyOf(target).Parent; ancestor is PlanItem type; ancestor = KeyOf(type).Parent)
                {
                    if (deleted.TryGetValue(type, out var time))
                    {
                        typeDeleted = Math.Min(typeDeleted, time);
                    }
                }

                var client = target.Id.Client;
                var valueEnd = target.Id.Clock + target.Length;
                var boundaries = new List<long> { target.IsStored ? target.Id.Clock : Math.Max(target.Id.Clock, _store.GetState(client)), valueEnd };
                events.Sort((a, b) => a.Time.CompareTo(b.Time));
                foreach (var (boundary, time) in events)
                {
                    var i = boundaries.BinarySearch(boundary);
                    if (i >= 0)
                    {
                        continue;
                    }

                    i = ~i;
                    var (partStart, partEnd) = (boundaries[i - 1], boundaries[i]);
                    if (!((partEnd == valueEnd && typeDeleted < time) || IsCovered(client, partStart, partEnd, ranges, time)))
                    {
                        boundaries.Insert(i, boundary);
                    }
                }

                // Every part but the last must be deleted by a range.
                for (var i = 0; i < boundaries.Count - 2; i++)
                {
                    if (!IsCovered(client, boundaries[i], boundaries[i + 1], ranges, int.MaxValue))
                    {
                        parts.Add(target);
                        break;
                    }
                }
            }

            return parts;
        }

        /// <summary>
        /// Whether a type below a collected one holds an undeleted item that is not the value of its key and that
        /// <paramref name="ranges"/> do not delete (the collection walks every key's items, the value and those left of it).
        /// </summary>
        private static bool HasLiveNonValue(YType type, YDoc doc, bool collected, List<(long Client, long Clock, long End)> ranges)
        {
            var stack = new List<(YType Type, bool Collected)> { (type, collected) };
            while (stack.Count > 0)
            {
                var (t, c) = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                foreach (var value in t.MapItems())
                {
                    for (var item = value; !item.IsNull; item = item.Left.AsItem)
                    {
                        if (c && item != value && !item.Deleted && !IsCovered(item.Id.Client, item.Id.Clock, item.Id.Clock + item.Length, ranges, int.MaxValue))
                        {
                            return true;
                        }

                        Push(item);
                    }
                }

                for (var item = t._start; !item.IsNull; item = item.Right.AsItem)
                {
                    Push(item);
                }

                void Push(ItemRef item)
                {
                    if (item.Kind == SlotKind.Type)
                    {
                        stack.Add((item.TypeOf, c || (!item.Keep && doc.GcFilter(PlanItem.Store(item).GcView(doc)))));
                    }
                }
            }

            return false;
        }

        /// <summary>Whether the ranges that delete before <paramref name="time"/> cover [<paramref name="start"/>, <paramref name="end"/>) of <paramref name="client"/>.</summary>
        private static bool IsCovered(long client, long start, long end, List<(long Client, long Clock, long End)> ranges, int time)
        {
            var own = new List<(long Clock, long End)>();
            for (var k = 0; k < ranges.Count && RangeDeletes(k + 1) < time; k++)
            {
                if (ranges[k].Client == client)
                {
                    own.Add((ranges[k].Clock, ranges[k].End));
                }
            }

            own.Sort((a, b) => a.Clock.CompareTo(b.Clock));
            var covered = start;
            foreach (var (clock, rangeEnd) in own)
            {
                if (clock > covered)
                {
                    break;
                }

                covered = Math.Max(covered, rangeEnd);
                if (covered >= end)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The item that holds <paramref name="id"/> once the plan is carried out, with the clock it starts at (a
        /// planned item that overlaps the store is integrated from the store's state on), or null for a GC struct.
        /// </summary>
        private (PlanItem Item, long Start) Target(ItemId id)
        {
            var known = _store.GetState(id.Client);
            if (id.Clock < known)
            {
                var found = _store.Find(id);
                return found.IsItem ? (PlanItem.Store(found), found.Id.Clock) : (default, 0);
            }

            if (id.Clock < GetState(id.Client) && FindPlanned(id) is var planned and >= 0 && _table.At(planned).Kind is not (StagedKind.GC or StagedKind.Skip))
            {
                return (PlanItem.Staged(_table, planned), Math.Max(_table.At(planned).Clock, known));
            }

            return (default, 0);
        }

        /// <summary>
        /// The parent and key <paramref name="item"/> has once integrated: its own, else those of the item its origin
        /// (or right origin) refers to, as <see cref="ItemRef.Adopt"/> takes them; (null, null) when it is
        /// integrated as a GC struct. The parent is a root's name, a root type, the <see cref="PlanItem"/> of the type's
        /// item, or a collected type (its item is gone, <see cref="YType.ItemDeleted"/> and
        /// <see cref="YType.ParentType"/> tell what its item did).
        /// </summary>
        private (object Parent, string Key) KeyOf(PlanItem item)
        {
            // Only a planned item has no parent yet; its references are integrated before it. The walk is memoized
            // (a large update's items each refer to the one before).
            var chain = _keyChain;
            chain.Clear();
            (object Parent, string Key) result;
            while (true)
            {
                if (_keys != null && _keys.TryGetValue(item, out result))
                {
                    break;
                }

                if (item.HasParent)
                {
                    result = (item.ParentName ?? (item.ParentId is ItemId id ? Target(id).Item.Boxed : item.StoredParent is YType type ? ParentOf(type) : item.StoredParent), item.ParentSub);
                    break;
                }

                chain.Add(item);
                if ((item.LeftOrigin is ItemId l && Target(l).Item.IsNull) || (item.RightOrigin is ItemId r && Target(r).Item.IsNull) || (item.LeftOrigin ?? item.RightOrigin) is not ItemId origin)
                {
                    result = (null, null);
                    break;
                }

                item = Target(origin).Item;
            }

            if (chain.Count > 0)
            {
                _keys ??= new Dictionary<PlanItem, (object, string)>();
                foreach (var c in chain)
                {
                    _keys[c] = result;
                }
            }

            return result;
        }

        // A type as the parent of an item: its item (a collected type has none), else the type (a root, or collected).
        private static object ParentOf(YType type) => type.IsCollected || type._item.IsNull ? type : PlanItem.Store(type._item).Boxed;
    }
}
