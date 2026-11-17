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
/// "bc" where Yjs' orphan reads "b". A replacement by a GC struct keeps the slot: the view then reads the GC struct.
/// A handle rather than a copy of <c>StructStore.Departures</c>: a departure elsewhere does not make every view look up
/// again.
/// </para>
/// <para>
/// A detached view (<see cref="Detached"/>) names a staged struct that is in no store (the integration plan calls
/// <c>GcFilter</c> on planned items): <c>_hint</c> is its index in <c>_table</c> and <c>_version</c> the table's version
/// when the view was made. While the table still has that version it reads the staged fields, as the struct is staged
/// (no neighbours, no resolved parent, no type yet for a Type); after a reset or truncate it reads the store's struct
/// that holds its id, and throws <see cref="InvalidOperationException"/> when the store has none (the struct was set
/// aside, or its clock is beyond the client's state). Layout: <c>_doc</c>, <c>_id</c>, <c>_hint</c> (a slab handle, -1
/// for none), <c>_table</c> (null for a store view), <c>_version</c>.
/// </para>
/// <para><c>default</c> is no view: every member throws <see cref="InvalidOperationException"/>.</para>
/// </remarks>
public readonly partial struct ItemView : IEquatable<ItemView>
{
    private readonly YDoc? _doc;
    private readonly ItemId _id;
    private readonly int _hint;
    private readonly StagingTable? _table;
    private readonly int _version;

    private ItemView(YDoc? doc, ItemId id, int hint, StagingTable? table = null, int version = 0)
    {
        _doc = doc;
        _id = id;
        _hint = hint;
        _table = table;
        _version = version;
    }

    /// <summary>The view of the struct <paramref name="str"/> of <paramref name="doc"/>'s store, which it names by its id.</summary>
    internal static ItemView Of(YDoc doc, ItemRef str) => new(doc ?? throw new ArgumentNullException(nameof(doc)), str.Id, str.Handle);

    /// <summary>The view of the id <paramref name="id"/> of <paramref name="doc"/>, with no hint: the first read looks it up.</summary>
    internal static ItemView At(YDoc doc, ItemId id) => new(doc ?? throw new ArgumentNullException(nameof(doc)), id, -1);

    /// <summary>
    /// The view of struct <paramref name="index"/> of <paramref name="table"/>, which is in no store: it reads the staged
    /// struct while the table keeps its version, the store's struct after.
    /// </summary>
    internal static ItemView Detached(YDoc? doc, StagingTable table, int index) => new(doc, table.IdOf(index), index, table, table.Version);

    public bool IsDefault => _doc == null && _table == null;

    internal bool IsDetached => _table != null;

    private bool IsStaged => _table != null && _table.Version == _version;

    private ref readonly StagedStruct Staged => ref _table!.At(_hint);

    public YDoc? Doc => _doc;

    public ItemId Id => Checked() ? _id : default;

    public ItemKind Kind
    {
        get
        {
            if (IsStaged)
            {
                return (ItemKind)(int)Staged.Kind;
            }

            var s = Resolve(out _);
            return s.IsItem ? s.Kind.ToItemKind() : ItemKind.GC;
        }
    }

    public long Length
    {
        get
        {
            if (IsStaged)
            {
                return Staged.Length;
            }

            var s = Resolve(out var o);
            return (long)s.Length - o;
        }
    }

    public ItemId LastId
    {
        get
        {
            if (IsStaged)
            {
                return new ItemId(_id.Client, _id.Clock + Staged.Length - 1);
            }

            var s = Resolve(out _);
            return new ItemId(s.Id.Client, s.Id.Clock + s.Length - 1);
        }
    }

    public bool Deleted => IsStaged ? Staged.Kind is StagedKind.GC or StagedKind.Skip : Resolve(out _).Deleted;

    public bool Countable => IsStaged ? Staged.Countable : Resolve(out _).AsItem is { IsNull: false } item && item.Countable;

    public bool Keep => !IsStaged && Resolve(out _).AsItem is { IsNull: false } item && item.Keep;

    /// <summary>What <c>GetContent()</c> gives from the view's offset on: a read-only slice, never a <c>Splice</c>.</summary>
    public IReadOnlyList<object?> Content
    {
        get
        {
            if (IsStaged)
            {
                return StagedContent();
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

    public ItemId? LeftOrigin
    {
        get
        {
            if (IsStaged)
            {
                return _table!.LeftOrigin(_hint);
            }

            var s = Resolve(out var o);
            return o > 0 ? new ItemId(_id.Client, _id.Clock - 1) : s.AsItem is { IsNull: false } item ? item.LeftOrigin : null;
        }
    }

    public ItemId? RightOrigin => IsStaged ? _table!.RightOrigin(_hint) : Resolve(out _).AsItem is { IsNull: false } item ? item.RightOrigin : null;

    /// <summary>The view of (client, clock - 1) at an offset, else of S's left struct; null when there is none (a decoded struct has no neighbours).</summary>
    public ItemView? Left
    {
        get
        {
            if (IsStaged)
            {
                return null;
            }

            var s = Resolve(out var o);
            return o > 0 ? new ItemView(_doc, new ItemId(_id.Client, _id.Clock - 1), s.Handle) : Neighbour(s.AsItem is { IsNull: false } item ? item.Left : default);
        }
    }

    public ItemView? Right => IsStaged ? null : Neighbour(Resolve(out _).AsItem is { IsNull: false } item ? item.Right : default);

    public YType? Parent => IsStaged ? null : Resolve(out _).AsItem is { IsNull: false } item ? item.Parent : null;

    public string? ParentSub => IsStaged ? _table!.ParentSub(_hint) : Resolve(out _).AsItem is { IsNull: false } item ? item.ParentSub : null;

    /// <summary>S's, moved by the offset as Yjs' <c>splitItem</c> moves it.</summary>
    public ItemId? Redone
    {
        get
        {
            if (IsStaged)
            {
                return null;
            }

            var s = Resolve(out var o);
            return (s.AsItem is { IsNull: false } item ? item.Redone : null) is ItemId redone ? new ItemId(redone.Client, redone.Clock + o) : null;
        }
    }

    public bool Equals(ItemView other) =>
        ReferenceEquals(_doc, other._doc) && _id.Equals(other._id) && ReferenceEquals(_table, other._table) && (_table == null || (_hint == other._hint && _version == other._version));

    public override bool Equals(object? obj) => obj is ItemView other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_id.Client, _id.Clock);

    public static bool operator ==(ItemView left, ItemView right) => left.Equals(right);

    public static bool operator !=(ItemView left, ItemView right) => !left.Equals(right);

    public override string ToString() => IsDefault ? "default" : $"{Kind} {_id.Client}:{_id.Clock} ({Length})";

    private bool Checked() => !IsDefault ? true : throw new InvalidOperationException("The view is default(ItemView): it names no struct. Make one from a struct or an id.");

    private ItemView? Neighbour(ItemRef s) => s.IsNull ? null : new ItemView(_doc, s.Id, s.Handle);

    /// <summary>The struct of the store that holds the id now, and the offset of the id in it.</summary>
    private ItemRef Resolve(out int offset)
    {
        Checked();
        var store = _doc?.Store ?? throw new InvalidOperationException($"The staged struct {_id.Client}:{_id.Clock} was released and its view has no document to look it up in.");
        if (_table != null && store.GetState(_id.Client) <= _id.Clock)
        {
            throw new InvalidOperationException($"The staged struct {_id.Client}:{_id.Clock} was released and is not in the store (it was set aside or not integrated).");
        }

        if (_table == null && _hint >= 0 && store.Slab.TryRead(_hint, out var slot) && !slot.IsFree)
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

    private IReadOnlyList<object?> StagedContent()
    {
        switch (Staged.Kind)
        {
            case StagedKind.String:
                var chars = _table!.Chars(_hint);
                var units = new object?[chars.Length];
                for (var i = 0; i < units.Length; i++)
                {
                    units[i] = chars[i].ToString();
                }

                return units;
            case StagedKind.Any:
                return new ContentView([.. _table!.Values(_hint)]);
            case StagedKind.Json or StagedKind.Binary or StagedKind.Embed or StagedKind.Doc:
                return ((IContent)_table!.Cold(_hint)).GetContent();
            default:
                return Array.Empty<object?>();
        }
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
