#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

/// <summary>
/// An item the integration plan reasons about: one of the store (an <see cref="ItemRef"/>) or a staged one (a struct of
/// the update's <see cref="StagingTable"/>), planned but not integrated. A planned item that overlaps the store shares its
/// id with the store's item, so an id does not name one: two are equal when both are the same store item or both the same
/// index of the same table, never one of each. A staged item is valid while its table is (the plan clears everything
/// that holds one before the table is reset or reused). <c>default</c> is none (<see cref="IsNull"/>).
/// </summary>
internal readonly partial struct PlanItem : IEquatable<PlanItem>
{
    private readonly StagingTable _table;
    private readonly int _index;
    private readonly ItemRef _stored;

    private PlanItem(StagingTable table, int index, ItemRef stored)
    {
        _table = table;
        _index = index;
        _stored = stored;
    }

    /// <summary>The item at <paramref name="index"/> of <paramref name="table"/>, which must be an item (not a GC or Skip).</summary>
    public static PlanItem Staged(StagingTable table, int index) => new(table, index, default);

    /// <summary>The item of the store <paramref name="item"/> names; <c>default</c> for none or a GC struct.</summary>
    public static PlanItem Store(ItemRef item) => item.IsItem ? new(null, 0, item) : default;

    public bool IsNull => _table is null && _stored.IsNull;

    public bool IsStored => _table is null && !_stored.IsNull;

    public bool IsStaged => _table is not null;

    /// <summary>The index in the table of a staged item.</summary>
    public int Index => _index;

    /// <summary>This item as an object (a parent in the plan's keys), null for none.</summary>
    public object Boxed => IsNull ? null : this;

    public ID Id => _table is null ? _stored.Id : _table.IdOf(_index);

    public ID LastId => _table is null ? _stored.LastId : new ID(Id.Client, _table.At(_index).Clock + _table.At(_index).Length - 1);

    public int Length => _table is null ? _stored.Length : _table.At(_index).Length;

    public bool Deleted => _table is null && _stored.Deleted;

    public bool Keep => _table is null && _stored.Keep;

    /// <summary>The type a stored item belongs to (what <see cref="ItemRef.Parent"/> holds); null for a staged item.</summary>
    public object StoredParent => _table is null ? _stored.Parent : null;

    /// <summary>The root type's name a staged item names as its parent; null for any other.</summary>
    public string ParentName => _table is not null && _table.At(_index).ParentKind == StagedParent.Root ? _table.ParentName(_index) : null;

    /// <summary>The id of the item that holds the parent type of a staged item; null for any other.</summary>
    public ID? ParentId => _table is not null && _table.At(_index).ParentKind == StagedParent.Id ? _table.ParentId(_index) : null;

    /// <summary>Whether the item names a parent (a staged item with no origins) or belongs to a type (a stored one).</summary>
    public bool HasParent => _table is null ? _stored.Parent != null : _table.At(_index).ParentKind != StagedParent.None;

    public string ParentSub => _table is null ? _stored.ParentSub : _table.ParentSub(_index);

    /// <summary>Whether the content is collected or deleted-only (no values).</summary>
    public bool HasDeletedContent => _table is null ? _stored.Kind == SlotKind.Deleted : _table.At(_index).Kind == StagedKind.Deleted;

    /// <summary>Whether the item holds a type.</summary>
    public bool IsType => _table is null ? _stored.Kind == SlotKind.Type : _table.At(_index).Kind == StagedKind.Type;

    /// <summary>The type a stored item holds, null for any other content or a staged item (which has no type yet).</summary>
    public AbstractType NestedType => _table is null && _stored.Kind == SlotKind.Type ? _stored.TypeOf : null;

    public ID? LeftOrigin => _table is null ? _stored.LeftOrigin : _table.LeftOrigin(_index);

    public ID? RightOrigin => _table is null ? _stored.RightOrigin : _table.RightOrigin(_index);

    /// <summary>The view the document's garbage collection filter is asked about; the plan makes one of a staged item.</summary>
    public ItemView GcView(YDoc doc) => ItemView.At(doc, _stored.Id);

    public static bool operator ==(PlanItem left, PlanItem right) => left.Equals(right);

    public static bool operator !=(PlanItem left, PlanItem right) => !left.Equals(right);

    public bool Equals(PlanItem other) => ReferenceEquals(_table, other._table) && _index == other._index && _stored == other._stored;

    public override bool Equals(object obj) => obj is PlanItem other && Equals(other);

    // A store item hashes by its id, which stays while a plan lives; the identity hash of an ItemRef will not once a reference is a slot.
    public override int GetHashCode() => _table is null ? HashCode.Combine(_stored.Id.Client, _stored.Id.Clock) : _index;
}
