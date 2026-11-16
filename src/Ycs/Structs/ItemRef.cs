// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

/// <summary>
/// A struct of the store (an item or a GC struct) as the library holds it: the members of Yjs' <c>Item</c> and
/// <c>AbstractStruct</c>, so that ported code reads like Yjs. It wraps today's object; <c>default</c> is Yjs' null
/// (<see cref="IsNull"/>), and two references are equal when they name the same struct (by reference while it wraps
/// an object). The members of an item throw on a GC struct as a cast to <see cref="Ycs.Item"/> throws.
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

    /// <summary>The reference to <paramref name="str"/> (null gives <c>default</c>): where an object enters the library's code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ItemRef Of(AbstractStruct str) => new(str);

    /// <summary>
    /// A new item (not integrated) between <paramref name="left"/> and <paramref name="right"/>, which are its origins
    /// (Yjs' <c>new Item(id, left, left &amp;&amp; left.lastId, right, right &amp;&amp; right.id, parent, parentSub, content)</c>).
    /// </summary>
    public static ItemRef New(ID id, ItemRef left, ItemRef right, AbstractType parent, string parentSub, IContent content) =>
        new(new Item(id, left._struct, left.IsNull ? null : left.LastId, right._struct, right.IsNull ? null : right.Id, parent, parentSub, content));

    /// <summary>The object, where the library hands a struct out (a public member, the decoded form): null for <c>default</c>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AbstractStruct ToStruct() => _struct;

    /// <summary>The item, where the library hands one out: null for <c>default</c>; throws on a GC struct.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Item ToItem() => (Item)_struct;

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

    /// <summary>Whether both are structs of one kind (the merge rules compare the constructors).</summary>
    public bool IsSameKind(ItemRef other) => _struct.GetType() == other._struct.GetType();

    public ID Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct.Id;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.SetStart(value);
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct.Length;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.SetLength(value);
    }

    public bool Deleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct.Deleted;
    }

    /// <summary>This reference when the struct is an item, else <c>default</c> (what <c>as Item</c> gives).</summary>
    public ItemRef AsItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _struct is Item item ? new(item) : default;
    }

    // The members of an item. Every accessor is inlined: Integrate and the walks read dozens of them, beyond the
    // JIT's inlining budget, and a call per read cost the walks up to 15 % (bench/Ycs.Compare/README.md, "ItemRef").
    private Item ItemObject
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Item)_struct;
    }

    public ItemRef Left
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(ItemObject.Left);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Left = value._struct;
    }

    public ItemRef Right
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(ItemObject.Right);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Right = value._struct;
    }

    public ID? LeftOrigin
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.LeftOrigin;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.LeftOrigin = value;
    }

    public ID? RightOrigin
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.RightOrigin;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.RightOrigin = value;
    }

    /// <summary>The type (<see cref="AbstractType"/>), or before integration a root's name or the type item's id.</summary>
    public object Parent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.Parent;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Parent = value;
    }

    public string ParentSub
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.ParentSub;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.ParentSub = value;
    }

    public ID? Redone
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.Redone;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Redone = value;
    }

    public IContentEx Content
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.Content;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Content = value;
    }

    public bool Marker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.Marker;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Marker = value;
    }

    public bool Keep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.Keep;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => ItemObject.Keep = value;
    }

    public bool Countable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ItemObject.Countable;
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
