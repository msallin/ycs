// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// A struct of the store (an item or a GC struct) as the library holds it: the members of Yjs' <c>Item</c> and
/// <c>AbstractStruct</c>, so that ported code reads like Yjs. It wraps today's object; <c>default</c> is Yjs' null
/// (<see cref="IsNull"/>), and two references are equal when they name the same struct (by reference while it wraps
/// an object). A struct of a store is read from its slot (<see cref="Slab"/>); the object is written along, as a
/// mirror the store's checks compare. The members of an item read as none on a GC struct, and throw
/// <see cref="NullReferenceException"/> on a struct outside a store: a type test, which the walks take faster than a cast.
/// </summary>
/// <remarks>
/// Valid until its struct is merged away or its type collected: a holder may keep one across a split (the left part
/// stays the struct it names), not across a cleanup's merge pass or a type's collection, except the holders that
/// split and merge keep current (docs/design-item-layout.md, 2.4 and 3.1).
/// </remarks>
internal readonly partial struct ItemRef : IEquatable<ItemRef>
{
    private readonly AbstractStruct _struct;

    private ItemRef(AbstractStruct str)
    {
        _struct = str;
    }

    /// <summary>
    /// The reference to <paramref name="str"/> (null gives <c>default</c>). Within the library only this type calls it:
    /// a decoded struct enters the store through <see cref="Adopt"/>, a decoded one that is only read through
    /// <see cref="Decoded"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ItemRef Of(AbstractStruct str) => new(str);

    /// <summary>
    /// A decoded struct that is in no store, read in place (the view of <see cref="ItemView.Detached"/>). It is never
    /// put in a store, a list or a link: a struct enters one only through <see cref="Adopt"/>.
    /// </summary>
    public static ItemRef Decoded(AbstractStruct decoded) => new(decoded);

    /// <summary>A new GC struct (not in a store) for the range <paramref name="length"/> from <paramref name="id"/>.</summary>
    public static ItemRef NewGC(ID id, int length) => new(new GC(id, length));

    /// <summary>
    /// A new item (not integrated) between <paramref name="left"/> and <paramref name="right"/>, which are its origins
    /// (Yjs' <c>new Item(id, left, left &amp;&amp; left.lastId, right, right &amp;&amp; right.id, parent, parentSub, content)</c>).
    /// </summary>
    public static ItemRef New(ID id, ItemRef left, ItemRef right, AbstractType parent, string parentSub, IContent content) =>
        new(new Item(id, left._struct, left.IsNull ? null : left.LastId, right._struct, right.IsNull ? null : right.Id, parent, parentSub, content));

    /// <summary>The object, for the tests of the library (no method of it calls this outside <see cref="ItemRef"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal AbstractStruct ToStruct() => _struct;

    /// <summary>The item, for the tests of the library (see <see cref="ToStruct"/>): null for <c>default</c>; throws on a GC struct.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Item ToItem() => (Item)_struct;

    /// <summary>The handle of the struct's live slot, -1 when it has none (<c>default</c>, a decoded struct, one that left the store).</summary>
    internal int Handle => _struct is null ? -1 : _struct.Handle;

    /// <summary>The store whose slab holds the struct's slot, or null.</summary>
    internal StructStore SlotStore => _struct?.SlotStore;

    internal void AttachSlot(StructStore store) => _struct.AttachSlot(store);

    internal void TakeSlotFrom(ItemRef old) => _struct.TakeSlotFrom(old._struct);

    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct is null;
    }

    /// <summary>Whether the struct is an item (Yjs' <c>instanceof Item</c>); false for <c>default</c>.</summary>
    public bool IsItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct is Item;
    }

    /// <summary>Whether the struct is a GC struct (Yjs' <c>instanceof GC</c>); false for <c>default</c>.</summary>
    public bool IsGC
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct is GC;
    }

    /// <summary>Whether the struct left every list (merged away, or replaced by a GC struct): its id and length are the range it held.</summary>
    public bool IsDetached => _struct is Item item ? item.Detached : _struct is GC { Detached: true };

    /// <summary>Whether both are structs of one kind (the merge rules compare the constructors).</summary>
    public bool IsSameKind(ItemRef other) => _struct.GetType() == other._struct.GetType();

    // Reads. A struct of a store has a handle and its slot is read (an index into a page array); the object is a mirror
    // that is only written. A struct that never was in a store (decoded, or an item before its slot is attached) has no
    // slot and is read as the object it is. A struct that left the store (merged away, replaced) has no slot and no
    // right to its object's values: reading it throws.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AbstractStruct Unattached(AbstractStruct str)
    {
        if (str is Item { Detached: true } or GC { Detached: true })
        {
            ThrowDetached(str);
        }

        return str;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDetached(AbstractStruct str) =>
        throw new InvalidOperationException(
            $"The struct {str.Id.Client}:{str.Id.Clock} left the store (merged into its left neighbour or replaced by a GC struct) and its slot is freed: " +
            "a reference to it is stale and reads nothing. Nothing was changed; the holder must take the struct that replaced it.");

    public ID Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? ReadId(str) : Unattached(str).Id;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.SetStart(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ID ReadId(AbstractStruct str)
    {
        var store = str.SlotStore;
        ref readonly var slot = ref SlotOf(str);
        return new ID(store.ClientTable[slot.Client], slot.Clock);
    }

    // Every read of a slot goes through these: in Debug the slot's owner must be the struct read (a handle that outlived
    // its struct names another one once the slot is reused); in Release they are the plain reads.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref readonly Slot SlotOf(AbstractStruct str)
    {
        str.SlotStore.Slab.CheckOwner(str.Handle, str);
        return ref str.SlotStore.Slab.Read(str.Handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object RefOf(AbstractStruct str)
    {
        str.SlotStore.Slab.CheckOwner(str.Handle, str);
        return str.SlotStore.Slab.ReadRef(str.Handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object KeyOf(AbstractStruct str)
    {
        str.SlotStore.Slab.CheckOwner(str.Handle, str);
        return str.SlotStore.Slab.ReadKey(str.Handle);
    }

    // The struct a link of the slot of <from> names: in Debug a live slot whose owner holds that handle.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static AbstractStruct LinkedOwner(AbstractStruct from, int handle)
    {
        var owner = (AbstractStruct)from.SlotStore.Slab.ReadOwner(handle);
        CheckLinked(handle, owner, from.SlotStore);
        return owner;
    }

    [Conditional("DEBUG")]
    private static void CheckLinked(int handle, AbstractStruct owner, StructStore store)
    {
        if (owner == null || owner.Handle != handle || !ReferenceEquals(owner.SlotStore, store))
        {
            throw new InvalidOperationException(
                $"A link names the slot {handle}, which is free or owned by a struct that does not hold it: the link outlived its target. Nothing was changed.");
        }
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? SlotOf(str).Length : Unattached(str).Length;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.SetLength(value);
    }

    public bool Deleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? (SlotOf(str).Info & Slot.DeletedFlag) != 0 : Unattached(str).Deleted;
        }
    }

    /// <summary>This reference when the struct is an item, else <c>default</c> (what <c>as Item</c> gives).</summary>
    public ItemRef AsItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct is Item item ? new(item) : default;
    }

    // The members of an item. Every accessor is inlined: Integrate and the walks read dozens of them, beyond the
    // JIT's inlining budget, and a call per read cost the walks up to 15 % (bench/Ycs.Compare/README.md, "ItemRef").
    // A GC struct's slot holds no links, origins, parent, content or key: they read as none.
    private Item ItemObject
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct as Item;
    }

    // The kind of the content (its reference number); 0 for a GC struct.
    internal int ContentRef
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? SlotOf(str).Kind : Unattached(str) is Item item ? item.Content.Ref : 0;
        }
    }

    public ItemRef Left
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            if (str.Handle < 0)
            {
                return new((Unattached(str) as Item).Left);
            }

            var handle = SlotOf(str).Left;
            return handle < 0 ? default : new(LinkedOwner(str, handle));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Left = value._struct;
    }

    public ItemRef Right
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            if (str.Handle < 0)
            {
                return new((Unattached(str) as Item).Right);
            }

            var handle = SlotOf(str).Right;
            return handle < 0 ? default : new(LinkedOwner(str, handle));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Right = value._struct;
    }

    public ID? LeftOrigin
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            if (str.Handle < 0)
            {
                return (Unattached(str) as Item).LeftOrigin;
            }

            var store = str.SlotStore;
            ref readonly var slot = ref SlotOf(str);
            return slot.OriginClient < 0 ? null : new ID(store.ClientTable[slot.OriginClient], slot.OriginClock);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.LeftOrigin = value;
    }

    public ID? RightOrigin
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            if (str.Handle < 0)
            {
                return (Unattached(str) as Item).RightOrigin;
            }

            var store = str.SlotStore;
            ref readonly var slot = ref SlotOf(str);
            return slot.ROriginClient < 0 ? null : new ID(store.ClientTable[slot.ROriginClient], slot.ROriginClock);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.RightOrigin = value;
    }

    /// <summary>The type (<see cref="AbstractType"/>), or before integration a root's name or the type item's id.</summary>
    public object Parent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            if (str.Handle < 0)
            {
                return (Unattached(str) as Item).Parent;
            }

            var index = SlotOf(str).Parent;
            return index < 0 ? null : str.SlotStore.Types[index];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Parent = value;
    }

    public string ParentSub
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? (string)KeyOf(str) : (Unattached(str) as Item).ParentSub;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.ParentSub = value;
    }

    // The id a redone item names is not in the slot (only whether it has one is).
    public ID? Redone
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            if (str.Handle >= 0 && (SlotOf(str).Info & Slot.HasRedoneFlag) == 0)
            {
                return null;
            }

            return (Unattached(str) as Item).Redone;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Redone = value;
    }

    public IContentEx Content
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? (IContentEx)RefOf(str) : (Unattached(str) as Item).Content;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Content = value;
    }

    public bool Marker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? (SlotOf(str).Info & Slot.MarkerFlag) != 0 : (Unattached(str) as Item).Marker;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Marker = value;
    }

    public bool Keep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? (SlotOf(str).Info & Slot.KeepFlag) != 0 : (Unattached(str) as Item).Keep;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Keep = value;
    }

    public bool Countable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var str = _struct;
            return str.Handle >= 0 ? (SlotOf(str).Info & Slot.CountableFlag) != 0 : (Unattached(str) as Item).Countable;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Countable = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(ItemRef left, ItemRef right) => ReferenceEquals(left._struct, right._struct);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(ItemRef left, ItemRef right) => !ReferenceEquals(left._struct, right._struct);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ItemRef other) => ReferenceEquals(_struct, other._struct);

    public override bool Equals(object obj) => obj is ItemRef other && ReferenceEquals(_struct, other._struct);

    // The identity hash of the object, as the object's own GetHashCode (no struct overrides it).
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(_struct);

    public override string ToString() => _struct is null ? "null" : $"{(_struct is GC ? "GC" : "Item")} {_struct.Id.Client}:{_struct.Id.Clock} ({_struct.Length})";
}
