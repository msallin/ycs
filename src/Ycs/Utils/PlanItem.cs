#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// An item the integration plan reasons about: one of the store (an <see cref="ItemRef"/>) or a decoded one, planned
/// but not integrated. A planned item that overlaps the store shares its id with the store's item, so an id does not
/// name one: two are equal when both are the same store item or both the same decoded object, never one of each.
/// <c>default</c> is none (<see cref="IsNull"/>).
/// </summary>
internal readonly struct PlanItem : IEquatable<PlanItem>
{
    private readonly Item _decoded;
    private readonly ItemRef _stored;

    private PlanItem(Item decoded, ItemRef stored)
    {
        _decoded = decoded;
        _stored = stored;
    }

    public static PlanItem Decoded(Item item) => new(item, default);

    /// <summary>The item of the store <paramref name="item"/> names; <c>default</c> for none or a GC struct.</summary>
    public static PlanItem Store(ItemRef item) => item.IsItem ? new(null, item) : default;

    public bool IsNull => _decoded is null && _stored.IsNull;

    public bool IsStored => _decoded is null && !_stored.IsNull;

    /// <summary>This item as an object (a parent in the plan's keys), null for none.</summary>
    public object Boxed => IsNull ? null : this;

    public ID Id => _decoded is null ? _stored.Id : _decoded.Id;

    public ID LastId => _decoded is null ? _stored.LastId : _decoded.LastId;

    public int Length => _decoded is null ? _stored.Length : _decoded.Length;

    public bool Deleted => _decoded is null ? _stored.Deleted : _decoded.Deleted;

    public bool Keep => _decoded is null && _stored.Keep;

    public object Parent => _decoded is null ? _stored.Parent : _decoded.Parent;

    public string ParentSub => _decoded is null ? _stored.ParentSub : _decoded.ParentSub;

    /// <summary>Whether the content is collected or deleted-only (no values); a stored item holds no object for it.</summary>
    public bool HasDeletedContent => _decoded is null ? _stored.Kind == SlotKind.Deleted : _decoded.Content is ContentDeleted;

    /// <summary>The content object; null for a stored item of kind <see cref="SlotKind.Deleted"/>.</summary>
    public IContentEx Content => _decoded is null ? _stored.Content : _decoded.Content;

    public ID? LeftOrigin => _decoded is null ? _stored.LeftOrigin : _decoded.LeftOrigin;

    public ID? RightOrigin => _decoded is null ? _stored.RightOrigin : _decoded.RightOrigin;

    /// <summary>The view the document's garbage collection filter is asked about.</summary>
    public ItemView GcView(YDoc doc) => _decoded is null ? ItemView.At(doc, _stored.Id) : ItemView.Detached(doc, _decoded);

    public static bool operator ==(PlanItem left, PlanItem right) => left.Equals(right);

    public static bool operator !=(PlanItem left, PlanItem right) => !left.Equals(right);

    public bool Equals(PlanItem other) => ReferenceEquals(_decoded, other._decoded) && _stored == other._stored;

    public override bool Equals(object obj) => obj is PlanItem other && Equals(other);

    // A store item hashes by its id, which stays while a plan lives; the identity hash of an ItemRef will not once a reference is a slot.
    public override int GetHashCode() => _decoded is null ? HashCode.Combine(_stored.Id.Client, _stored.Id.Clock) : RuntimeHelpers.GetHashCode(_decoded);
}
