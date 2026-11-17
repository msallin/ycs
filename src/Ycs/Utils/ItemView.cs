using System.Collections;

namespace Ycs;

/// <summary>
/// A struct as the caller sees it: the document, an id and a hint (docs/design-item-layout.md, 3.2). It reads the struct
/// that holds its id now, as if split at the id without splitting it: with S that struct and o = id.Clock - S.Id.Clock,
/// <see cref="Length"/>, <see cref="Content"/>, <see cref="LeftOrigin"/>, <see cref="Left"/> and <see cref="Redone"/>
/// are those of the part from o on; the others are S's. A GC struct reads <see cref="Parent"/> null, empty content,
/// <see cref="Deleted"/> true.
/// </summary>
/// <remarks>
/// <para>
/// The hint is the slab handle of the struct the view was made from. It is current while its slot is live and holds
/// the id (a split narrows the range; a merge frees the slot, and the slot may be reused by another struct, which
/// then is the one that holds the id or does not cover it); else one lookup by id finds S. Valid as long as the
/// document: after a merge the view reads the struct that holds its id now, so the second of three merged items reads
/// "bc" where Yjs' orphan reads "b" (H7). A replacement by a GC struct keeps the slot: the view then reads the GC struct.
/// A handle rather than a copy of <c>StructStore.Departures</c>: a departure elsewhere does not make every view look up
/// again.
/// </para>
/// <para>
/// A detached view (<see cref="Detached"/>) holds a decoded struct that is in no store (the integration plan calls
/// <c>GcFilter</c> on planned items): it reads that object and never looks up. Layout: <c>_doc</c>, <c>_id</c>,
/// <c>_hint</c> (a slab handle, -1 for none), <c>_decoded</c> (null for a store view).
/// </para>
/// <para><c>default</c> is no view: every member throws <see cref="InvalidOperationException"/>.</para>
/// </remarks>
public readonly partial struct ItemView : IEquatable<ItemView>
{
    private readonly YDoc? _doc;
    private readonly ID _id;
    private readonly int _hint;
    private readonly AbstractStruct? _decoded;

    private ItemView(YDoc? doc, ID id, int hint, AbstractStruct? decoded)
    {
        _doc = doc;
        _id = id;
        _hint = hint;
        _decoded = decoded;
    }

    /// <summary>The view of the struct <paramref name="str"/> of <paramref name="doc"/>'s store, which it names by its id.</summary>
    internal static ItemView Of(YDoc doc, ItemRef str) => new(doc ?? throw new ArgumentNullException(nameof(doc)), str.Id, str.Handle, null);

    /// <summary>The view of the id <paramref name="id"/> of <paramref name="doc"/>, with no hint: the first read looks it up.</summary>
    internal static ItemView At(YDoc doc, ID id) => new(doc ?? throw new ArgumentNullException(nameof(doc)), id, -1, null);

    /// <summary>
    /// The view of a decoded struct that is in no store, which it reads as it is: it has no neighbours, and its parent is
    /// not resolved (<see cref="Parent"/> is null unless the decoded struct names a type).
    /// </summary>
    internal static ItemView Detached(YDoc? doc, AbstractStruct decoded) => new(doc, decoded.Id, -1, decoded);

    public bool IsDefault => _doc == null && _decoded == null;

    internal bool IsDetached => _decoded != null;

    public YDoc? Doc => _doc;

    public ID Id => Checked() ? _id : default;

    public ItemKind Kind
    {
        get
        {
            if (_decoded != null)
            {
                return _decoded is Item decodedItem ? (ItemKind)decodedItem.Content.Ref : _decoded is GC ? ItemKind.GC : ItemKind.Skip;
            }

            var s = Resolve(out _);
            return s.IsItem ? s.Kind.ToItemKind() : ItemKind.GC;
        }
    }

    public long Length
    {
        get
        {
            if (_decoded != null)
            {
                return _decoded.Length;
            }

            var s = Resolve(out var o);
            return (long)s.Length - o;
        }
    }

    public ID LastId
    {
        get
        {
            if (_decoded != null)
            {
                return new ID(_decoded.Id.Client, _decoded.Id.Clock + _decoded.Length - 1);
            }

            var s = Resolve(out _);
            return new ID(s.Id.Client, s.Id.Clock + s.Length - 1);
        }
    }

    public bool Deleted => _decoded != null ? _decoded.Deleted : Resolve(out _).Deleted;

    public bool Countable => _decoded != null ? _decoded is Item { Countable: true } : Resolve(out _).AsItem is { IsNull: false } item && item.Countable;

    public bool Keep => _decoded == null && Resolve(out _).AsItem is { IsNull: false } item && item.Keep;

    /// <summary>What <c>GetContent()</c> gives from the view's offset on: a read-only slice, never a <c>Splice</c>.</summary>
    public IReadOnlyList<object?> Content
    {
        get
        {
            if (_decoded != null)
            {
                return _decoded is Item decodedItem ? (IReadOnlyList<object?>)decodedItem.Content.GetContent() : Array.Empty<object?>();
            }

            var item = Resolve(out var o);
            if (!item.IsItem)
            {
                return Array.Empty<object?>();
            }

            IReadOnlyList<object?> content = item.Elements;
            return o == 0 ? content : new Slice(content, o);
        }
    }

    public ID? LeftOrigin
    {
        get
        {
            if (_decoded != null)
            {
                return (_decoded as Item)?.LeftOrigin;
            }

            var s = Resolve(out var o);
            return o > 0 ? new ID(_id.Client, _id.Clock - 1) : s.AsItem is { IsNull: false } item ? item.LeftOrigin : null;
        }
    }

    public ID? RightOrigin => _decoded != null ? (_decoded as Item)?.RightOrigin : Resolve(out _).AsItem is { IsNull: false } item ? item.RightOrigin : null;

    /// <summary>The view of (client, clock - 1) at an offset, else of S's left struct; null when there is none (a decoded struct has no neighbours).</summary>
    public ItemView? Left
    {
        get
        {
            if (_decoded != null)
            {
                return null;
            }

            var s = Resolve(out var o);
            return o > 0 ? new ItemView(_doc, new ID(_id.Client, _id.Clock - 1), s.Handle, null) : Neighbour(s.AsItem is { IsNull: false } item ? item.Left : default);
        }
    }

    public ItemView? Right => _decoded != null ? null : Neighbour(Resolve(out _).AsItem is { IsNull: false } item ? item.Right : default);

    public AbstractType? Parent => _decoded != null ? (_decoded as Item)?.Parent as AbstractType : Resolve(out _).AsItem is { IsNull: false } item ? item.Parent : null;

    public string? ParentSub => _decoded != null ? (_decoded as Item)?.ParentSub : Resolve(out _).AsItem is { IsNull: false } item ? item.ParentSub : null;

    /// <summary>S's, moved by the offset as Yjs' <c>splitItem</c> moves it.</summary>
    public ID? Redone
    {
        get
        {
            if (_decoded != null)
            {
                return null;
            }

            var s = Resolve(out var o);
            return (s.AsItem is { IsNull: false } item ? item.Redone : null) is ID redone ? new ID(redone.Client, redone.Clock + o) : null;
        }
    }

    public bool Equals(ItemView other) =>
        ReferenceEquals(_doc, other._doc) && _id.Equals(other._id) && ReferenceEquals(_decoded, other._decoded);

    public override bool Equals(object? obj) => obj is ItemView other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_id.Client, _id.Clock);

    public static bool operator ==(ItemView left, ItemView right) => left.Equals(right);

    public static bool operator !=(ItemView left, ItemView right) => !left.Equals(right);

    public override string ToString() => IsDefault ? "default" : $"{Kind} {_id.Client}:{_id.Clock} ({Length})";

    private bool Checked() => !IsDefault ? true : throw new InvalidOperationException("The view is default(ItemView): it names no struct. Make one from a struct or an id.");

    private ItemView? Neighbour(ItemRef s) => s.IsNull ? null : new ItemView(_doc, s.Id, s.Handle, null);

    /// <summary>The struct of the store that holds the id now, and the offset of the id in it.</summary>
    private ItemRef Resolve(out int offset)
    {
        Checked();
        var store = _doc!.Store;
        if (_hint >= 0 && store.Slab.TryRead(_hint, out var slot) && !slot.IsFree)
        {
            var o = _id.Clock - slot.Clock;
            if (store.ClientTable[slot.Client] == _id.Client && (ulong)o < (ulong)slot.Length)
            {
                offset = (int)o;
                return new ItemRef(store.Slab, _hint);
            }
        }

        var found = store.Find(_id);
        offset = (int)(_id.Clock - found.Id.Clock);
        return found;
    }

    private sealed class Slice : IReadOnlyList<object?>
    {
        private readonly IReadOnlyList<object?> _source;
        private readonly int _offset;

        public Slice(IReadOnlyList<object?> source, int offset)
        {
            _source = source;
            _offset = offset;
        }

        public int Count => _source.Count - _offset;

        public object? this[int index] => index >= 0 && index < Count ? _source[_offset + index] : throw new ArgumentOutOfRangeException(nameof(index));

        public IEnumerator<object?> GetEnumerator()
        {
            for (var i = _offset; i < _source.Count; i++)
            {
                yield return _source[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
