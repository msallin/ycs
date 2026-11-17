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
/// <c>AbstractStruct</c>, so that ported code reads like Yjs, over the slot of a <see cref="Slab"/> a handle names.
/// A read is an index into the slab's pages and a write goes to the slot, so no object stands for a struct;
/// <c>default</c> is Yjs' null (<see cref="IsNull"/>), and two references are equal when they name the same slot of the
/// same slab. The members of an item read as none on a GC struct.
/// </summary>
/// <remarks>
/// <para>
/// Valid until its struct is merged away (its slot is freed) or its type collected: a holder may keep one across a
/// split (the left part stays the struct it names) and across the replacement by a GC struct (the slot stays), not
/// across a cleanup's merge pass, except the holders that split and merge keep current (docs/design-item-layout.md, 2.4
/// and 3.1). In Debug a reference keeps the generation of its slot when it is made and every read checks it
/// (<see cref="Slab.CheckGeneration"/>), so a reference that outlived its struct throws; in Release a read costs no more
/// than the index.
/// </para>
/// </remarks>
internal readonly partial struct ItemRef : IEquatable<ItemRef>
{
    private readonly Slab _slab;
    private readonly int _handle;
#if DEBUG
    private readonly int _generation;
#endif

    /// <summary>The reference to the live slot <paramref name="handle"/> of <paramref name="slab"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ItemRef(Slab slab, int handle)
    {
        _slab = slab;
        _handle = handle;
#if DEBUG
        _generation = slab.Generation(handle);
#endif
    }

#if DEBUG
    private ItemRef(Slab slab, int handle, int generation)
    {
        _slab = slab;
        _handle = handle;
        _generation = generation;
    }
#endif

    /// <summary>The reference a holder's <paramref name="held"/> names in <paramref name="slab"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ItemRef Held(Slab slab, ItemHandle held) =>
#if DEBUG
        new(slab, held.Value, held.Generation);
#else
        new(slab, held.Value);
#endif

    /// <summary>What a holder keeps of this reference: its handle (and in Debug its generation), none for <c>default</c>.</summary>
    internal ItemHandle Hold
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get =>
            _slab is null ? ItemHandle.None :
#if DEBUG
            new(_handle, _generation);
#else
            new(_handle);
#endif
    }

    /// <summary>The slab whose slot the reference names; null for <c>default</c>.</summary>
    internal Slab Slab => _slab;

    /// <summary>The handle of the struct's slot, -1 for <c>default</c>.</summary>
    internal int Handle
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slab is null ? -1 : _handle;
    }

    /// <summary>
    /// Whether the slot was freed (the struct merged into its left neighbour) and not yet reused, or names none, read
    /// without the Debug generation check: for the code that looks for a stale reference.
    /// </summary>
    internal bool IsFreed => _slab is null || !_slab.TryRead(_handle, out var slot) || slot.IsFree;

    public bool IsNull
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slab is null;
    }

    /// <summary>Whether the struct is an item (Yjs' <c>instanceof Item</c>); false for <c>default</c>.</summary>
    public bool IsItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slab is not null && Read().Kind != 0;
    }

    /// <summary>Whether the struct is a GC struct (Yjs' <c>instanceof GC</c>); false for <c>default</c>.</summary>
    public bool IsGC
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slab is not null && Read().Kind == 0;
    }

    /// <summary>Whether both are structs of one kind (the merge rules compare the kinds).</summary>
    public bool IsSameKind(ItemRef other) => (Read().Kind == 0) == (other.Read().Kind == 0);

    /// <summary>This reference when the struct is an item, else <c>default</c> (what <c>as Item</c> gives).</summary>
    public ItemRef AsItem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => IsItem ? this : default;
    }

    // Every read and write of a slot or of its objects is preceded by this: in Debug the slot's generation must be the
    // reference's; in Release the call is gone.
#pragma warning disable CA1822 // Instance data in Debug.
    [Conditional("DEBUG")]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Check()
    {
#if DEBUG
        _slab.CheckGeneration(_handle, _generation);
#endif
    }
#pragma warning restore CA1822

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref readonly Slot Read()
    {
        Check();
        return ref _slab.Read(_handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Slot Write()
    {
        Check();
        return ref _slab.Write(_handle);
    }

    /// <summary>
    /// The slot, for the code that reads several fields of it: valid until the next <see cref="Slab.Allocate"/> (a split,
    /// <see cref="New"/>), so take it after the last allocation of the method and drop it before the next one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref readonly Slot Peek() => ref Read();

    /// <summary><see cref="Peek"/> for writing; the same rule.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref Slot Poke() => ref Write();

    /// <summary>The clock of the struct's first address, without the client lookup <see cref="Id"/> does.</summary>
    internal long Clock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Read().Clock;
    }

    /// <summary>The index of the struct's client in the slab's client table: equal for structs of one client.</summary>
    internal int ClientIndex
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Read().Client;
    }

    public ID Id
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly var slot = ref Read();
            return new ID(_slab.Clients[slot.Client], slot.Clock);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            var client = _slab.Clients.GetOrAdd(value.Client);
            ref var slot = ref Write();
            slot.Clock = value.Clock;
            slot.Client = client;
        }
    }

    public int Length
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Read().Length;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Write().Length = value;
    }

    public bool Deleted
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Read().Info & Slot.DeletedFlag) != 0;
    }

    // The members of an item. Every accessor is inlined: Integrate and the walks read dozens of them, beyond the
    // JIT's inlining budget, and a call per read cost the walks up to 15 % (bench/Ycs.Compare/README.md, "ItemRef").
    // A GC struct's slot holds no links, origins, parent, content or key: they read as none.

    // The kind of the content (its reference number); 0 for a GC struct.
    internal int ContentRef
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Read().Kind;
    }

    public ItemRef Left
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var handle = Read().Left;
            return handle < 0 ? default : new(_slab, handle);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Write().Left = value.Handle;
    }

    public ItemRef Right
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var handle = Read().Right;
            return handle < 0 ? default : new(_slab, handle);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Write().Right = value.Handle;
    }

    public ID? LeftOrigin
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly var slot = ref Read();
            return slot.OriginClient < 0 ? null : new ID(_slab.Clients[slot.OriginClient], slot.OriginClock);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            var client = value is { } id ? _slab.Clients.GetOrAdd(id.Client) : -1;
            ref var slot = ref Write();
            slot.OriginClient = client;
            slot.OriginClock = value?.Clock ?? 0;
        }
    }

    public ID? RightOrigin
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            ref readonly var slot = ref Read();
            return slot.ROriginClient < 0 ? null : new ID(_slab.Clients[slot.ROriginClient], slot.ROriginClock);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            var client = value is { } id ? _slab.Clients.GetOrAdd(id.Client) : -1;
            ref var slot = ref Write();
            slot.ROriginClient = client;
            slot.ROriginClock = value?.Clock ?? 0;
        }
    }

    /// <summary>The type the item is in, null for a GC struct and for an item of a type that was collected.</summary>
    public AbstractType Parent
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            var index = Read().Parent;
            return index < 0 ? null : _slab.Types[index];
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            var index = value?.TableIndex(_slab.Types) ?? -1;
            Write().Parent = index;
        }
    }

    public string ParentSub
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Check();
            return (string)_slab.ReadKey(_handle);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            Check();
            _slab.SetKey(_handle, value);
        }
    }

    // The id a redone item names is beside the slab (the slot holds only whether it has one).
    public ID? Redone
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Read().Info & Slot.HasRedoneFlag) == 0 ? null : _slab.GetRedone(_handle);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            ref var slot = ref Write();
            slot.Info = value == null ? slot.Info & ~Slot.HasRedoneFlag : slot.Info | Slot.HasRedoneFlag;
            _slab.SetRedone(_handle, value);
        }
    }

    public IContentEx Content
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Check();
            return (IContentEx)_slab.ReadRef(_handle);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set
        {
            ref var slot = ref Write();
            slot.Info = (slot.Info & ~(0xFF << 8)) | (value.Ref << 8);
            _slab.SetRef(_handle, value is ContentDeleted ? null : value);
        }
    }

    public bool Marker
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Read().Info & Slot.MarkerFlag) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => SetFlag(Slot.MarkerFlag, value);
    }

    public bool Keep
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Read().Info & Slot.KeepFlag) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => SetFlag(Slot.KeepFlag, value);
    }

    public bool Countable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (Read().Info & Slot.CountableFlag) != 0;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => SetFlag(Slot.CountableFlag, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetFlag(int flag, bool value)
    {
        ref var slot = ref Write();
        slot.Info = value ? slot.Info | flag : slot.Info & ~flag;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(ItemRef left, ItemRef right) => left._handle == right._handle && ReferenceEquals(left._slab, right._slab);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(ItemRef left, ItemRef right) => !(left == right);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ItemRef other) => this == other;

    public override bool Equals(object obj) => obj is ItemRef other && this == other;

    public override int GetHashCode() => _handle;

    public override string ToString() => _slab is null ? "null" : $"{(IsGC ? "GC" : "Item")} {Id.Client}:{Id.Clock} ({Length})";
}
