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
internal readonly struct ItemRef : IEquatable<ItemRef>
{
    private readonly AbstractStruct _struct;

    private ItemRef(AbstractStruct str)
    {
        _struct = str;
    }

    /// <summary>The reference to <paramref name="str"/> (null gives <c>default</c>): where an object enters the library's code.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ItemRef Of(AbstractStruct str) => new(str);

    /// <summary>The object, where the library hands a struct out (a public member, the decoded form): null for <c>default</c>.</summary>
    public AbstractStruct ToStruct() => _struct;

    /// <summary>The item, where the library hands one out: null for <c>default</c>; throws on a GC struct.</summary>
    public Item ToItem() => (Item)_struct;

    public bool IsNull => _struct is null;

    /// <summary>Whether the struct is an item (Yjs' <c>instanceof Item</c>); false for <c>default</c>.</summary>
    public bool IsItem => _struct is Item;

    /// <summary>Whether the struct is a GC struct (Yjs' <c>instanceof GC</c>); false for <c>default</c>.</summary>
    public bool IsGC => _struct is GC;

    /// <summary>Whether both are structs of one kind (the merge rules compare the constructors).</summary>
    public bool IsSameKind(ItemRef other) => _struct.GetType() == other._struct.GetType();

    public ID Id => _struct.Id;

    public int Length => _struct.Length;

    public bool Deleted => _struct.Deleted;

    // The members of an item.
    private Item AsItem => (Item)_struct;

    public ItemRef Left
    {
        get => new(AsItem.Left);
        set => AsItem.Left = value._struct;
    }

    public ItemRef Right
    {
        get => new(AsItem.Right);
        set => AsItem.Right = value._struct;
    }

    public ID? LeftOrigin
    {
        get => AsItem.LeftOrigin;
        set => AsItem.LeftOrigin = value;
    }

    public ID? RightOrigin
    {
        get => AsItem.RightOrigin;
        set => AsItem.RightOrigin = value;
    }

    /// <summary>The type (<see cref="AbstractType"/>), or before integration a root's name or the type item's id.</summary>
    public object Parent
    {
        get => AsItem.Parent;
        set => AsItem.Parent = value;
    }

    public string ParentSub
    {
        get => AsItem.ParentSub;
        set => AsItem.ParentSub = value;
    }

    public ID? Redone
    {
        get => AsItem.Redone;
        set => AsItem.Redone = value;
    }

    public IContentEx Content
    {
        get => AsItem.Content;
        set => AsItem.Content = value;
    }

    public bool Marker
    {
        get => AsItem.Marker;
        set => AsItem.Marker = value;
    }

    public bool Keep
    {
        get => AsItem.Keep;
        set => AsItem.Keep = value;
    }

    public bool Countable
    {
        get => AsItem.Countable;
        set => AsItem.Countable = value;
    }

    public ID LastId => AsItem.LastId;

    public static bool operator ==(ItemRef left, ItemRef right) => ReferenceEquals(left._struct, right._struct);

    public static bool operator !=(ItemRef left, ItemRef right) => !ReferenceEquals(left._struct, right._struct);

    public bool Equals(ItemRef other) => ReferenceEquals(_struct, other._struct);

    public override bool Equals(object obj) => obj is ItemRef other && ReferenceEquals(_struct, other._struct);

    // The identity hash of the object, as the object's own GetHashCode (no struct overrides it).
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(_struct);

    public override string ToString() => _struct is null ? "null" : $"{(_struct is GC ? "GC" : "Item")} {_struct.Id.Client}:{_struct.Id.Clock} ({_struct.Length})";

    // While the library moves onto references (layout step 4); removed when it is done.
    public static implicit operator ItemRef(AbstractStruct str) => new(str);

    public static implicit operator AbstractStruct(ItemRef reference) => reference._struct;
}
