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
/// The hint is the struct the view was made from. It is current while it is not <c>Detached</c> (a merge or a GC
/// replacement sets that) and still covers the id (a split narrows it); else one lookup by id finds S. Valid as long as
/// the document: after a merge the view reads the struct that holds its id now, so the second of three merged items reads
/// "bc" where Yjs' orphan reads "b" (H7). A flag rather than a copy of <c>StructStore.Departures</c>: it costs no field
/// and a departure elsewhere does not make every view look up again.
/// </para>
/// <para>
/// A detached view (<see cref="Detached"/>) holds a decoded struct that is in no store (the integration plan calls
/// <c>GcFilter</c> on planned items): it reads that object and never looks up. Layout: <c>_doc</c>, <c>_id</c>,
/// <c>_hint</c>, <c>_detached</c>. In step 7 the store view's <c>_hint</c> becomes a slab handle (an int) and
/// <c>_hint</c> keeps the decoded object of a detached one (null for a store view, <c>_detached</c> then folds into it).
/// </para>
/// <para><c>default</c> is no view: every member throws <see cref="InvalidOperationException"/>.</para>
/// </remarks>
public readonly partial struct ItemView : IEquatable<ItemView>
{
    private readonly YDoc? _doc;
    private readonly ID _id;
    private readonly ItemRef _hint;
    private readonly bool _detached;

    private ItemView(YDoc? doc, ID id, ItemRef hint, bool detached)
    {
        _doc = doc;
        _id = id;
        _hint = hint;
        _detached = detached;
    }

    /// <summary>The view of the struct <paramref name="str"/> of <paramref name="doc"/>'s store, which it names by its id.</summary>
    internal static ItemView Of(YDoc doc, ItemRef str) => new(doc ?? throw new ArgumentNullException(nameof(doc)), str.Id, str, false);

    /// <summary>The view of the id <paramref name="id"/> of <paramref name="doc"/>, with no hint: the first read looks it up.</summary>
    internal static ItemView At(YDoc doc, ID id) => new(doc ?? throw new ArgumentNullException(nameof(doc)), id, default, false);

    /// <summary>
    /// The view of a decoded struct that is in no store, which it reads as it is. <paramref name="doc"/> only lets
    /// <see cref="Left"/> and <see cref="Right"/> name the store structs a planned item points at (null when there is none).
    /// </summary>
    internal static ItemView Detached(YDoc? doc, AbstractStruct decoded) => new(doc, decoded.Id, ItemRef.Decoded(decoded), true);

    public bool IsDefault => _doc == null && !_detached;

    internal bool IsDetached => _detached;

    public YDoc? Doc => _doc;

    public ID Id => Checked() ? _id : default;

    public ItemKind Kind
    {
        get
        {
            var s = Resolve(out _);
            return s.IsItem ? (ItemKind)s.Content.Ref : s.IsGC ? ItemKind.GC : ItemKind.Skip;
        }
    }

    public long Length
    {
        get
        {
            var s = Resolve(out var o);
            return (long)s.Length - o;
        }
    }

    public ID LastId
    {
        get
        {
            var s = Resolve(out _);
            return new ID(s.Id.Client, s.Id.Clock + s.Length - 1);
        }
    }

    public bool Deleted => Resolve(out _).Deleted;

    public bool Countable => Resolve(out _).AsItem is { IsNull: false } item && item.Countable;

    public bool Keep => Resolve(out _).AsItem is { IsNull: false } item && item.Keep;

    /// <summary>What <c>GetContent()</c> gives from the view's offset on: a read-only slice, never a <c>Splice</c>.</summary>
    public IReadOnlyList<object?> Content
    {
        get
        {
            var item = Resolve(out var o);
            if (!item.IsItem)
            {
                return Array.Empty<object?>();
            }

            IReadOnlyList<object?> content = item.Content.GetContent();
            return o == 0 ? content : new Slice(content, o);
        }
    }

    public ID? LeftOrigin
    {
        get
        {
            var s = Resolve(out var o);
            return o > 0 ? new ID(_id.Client, _id.Clock - 1) : s.AsItem is { IsNull: false } item ? item.LeftOrigin : null;
        }
    }

    public ID? RightOrigin => Resolve(out _).AsItem is { IsNull: false } item ? item.RightOrigin : null;

    /// <summary>The view of (client, clock - 1) at an offset, else of S's left struct; null when there is none.</summary>
    public ItemView? Left
    {
        get
        {
            var s = Resolve(out var o);
            return o > 0 ? new ItemView(_doc, new ID(_id.Client, _id.Clock - 1), s, false) : Neighbour(s.AsItem is { IsNull: false } item ? item.Left : default);
        }
    }

    public ItemView? Right => Neighbour(Resolve(out _).AsItem is { IsNull: false } item ? item.Right : default);

    public AbstractType? Parent => (Resolve(out _).AsItem is { IsNull: false } item ? item.Parent : null) as AbstractType;

    public string? ParentSub => Resolve(out _).AsItem is { IsNull: false } item ? item.ParentSub : null;

    /// <summary>S's, moved by the offset as Yjs' <c>splitItem</c> moves it.</summary>
    public ID? Redone
    {
        get
        {
            var s = Resolve(out var o);
            return (s.AsItem is { IsNull: false } item ? item.Redone : null) is ID redone ? new ID(redone.Client, redone.Clock + o) : null;
        }
    }

    public bool Equals(ItemView other) =>
        ReferenceEquals(_doc, other._doc) && _id.Equals(other._id) && _detached == other._detached && (!_detached || _hint == other._hint);

    public override bool Equals(object? obj) => obj is ItemView other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_id.Client, _id.Clock);

    public static bool operator ==(ItemView left, ItemView right) => left.Equals(right);

    public static bool operator !=(ItemView left, ItemView right) => !left.Equals(right);

    public override string ToString() => IsDefault ? "default" : $"{Kind} {_id.Client}:{_id.Clock} ({Length})";

    private bool Checked() => !IsDefault ? true : throw new InvalidOperationException("The view is default(ItemView): it names no struct. Make one from a struct or an id.");

    private ItemView? Neighbour(ItemRef s) => s.IsNull ? null : _detached && _doc == null ? new ItemView(null, s.Id, s, true) : new ItemView(_doc, s.Id, s, false);

    /// <summary>The struct that holds the id now, and the offset of the id in it.</summary>
    private ItemRef Resolve(out int offset)
    {
        Checked();
        var hint = _hint;
        if (_detached)
        {
            offset = 0;
            return hint;
        }

        if (!hint.IsNull && !hint.IsDetached)
        {
            var o = _id.Clock - hint.Id.Clock;
            if (hint.Id.Client == _id.Client && (ulong)o < (ulong)hint.Length)
            {
                offset = (int)o;
                return hint;
            }
        }

        var found = _doc!.Store.Find(_id);
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
