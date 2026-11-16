#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

// The shadow of the slab: a struct of a store has one live slot, written whenever one of its scalars changes. Reads
// still come from the object. A struct that is in no store (decoded, not yet integrated, merged away) has no handle.
internal abstract partial class AbstractStruct
{
    /// <summary>The store whose slab holds this struct's slot; null while <see cref="Handle"/> is -1.</summary>
    internal StructStore SlotStore;

    /// <summary>The handle of the live slot of this struct, or -1: the struct is not in a store, or left it.</summary>
    internal int Handle = -1;

    // The deleted flag of the slot's info, which is Item's own bit.
    private protected const int SlotDeleted = 1 << 2;

    // A Skip is never in a store. The kind (bits 8..15) is the content's reference number, 0 for a GC struct; flags as Item's info bits.
    private protected virtual int SlotInfo => 0;

    internal void AttachSlot(StructStore store)
    {
        if (Handle >= 0)
        {
            throw new InvalidOperationException("The struct has a slot already: a struct enters a store once.");
        }

        SlotStore = store;
        Handle = store.Slab.Allocate();
        WriteSlot();
    }

    /// <summary><paramref name="old"/>, which this struct replaces in its list, hands over its slot: the handle stays the struct's.</summary>
    internal void TakeSlotFrom(AbstractStruct old)
    {
        if (old.Handle < 0 || Handle >= 0)
        {
            throw new InvalidOperationException("A struct takes the slot of a struct that has one, and has none itself; the store's lists are unchanged.");
        }

        SlotStore = old.SlotStore;
        Handle = old.Handle;
        old.SlotStore = null;
        old.Handle = -1;
        SlotStore.Slab.At(Handle) = Slot.Empty;
        WriteSlot();
    }

    /// <summary>Frees the slot of a struct that left every list; nothing when it has none (a replaced struct gave its slot away).</summary>
    internal void FreeSlot()
    {
        if (Handle >= 0)
        {
            SlotStore.Slab.Free(Handle);
            SlotStore = null;
            Handle = -1;
        }
    }

    internal void WriteInfo()
    {
        if (Handle >= 0)
        {
            SlotStore.Slab.At(Handle).Info = SlotInfo;
        }
    }

    private protected void WriteId()
    {
        var index = SlotStore.ClientTable.GetOrAdd(Id.Client);
        ref var slot = ref SlotStore.Slab.At(Handle);
        slot.Clock = Id.Clock;
        slot.Client = index;
    }

    private protected virtual void WriteOrigins()
    {
    }

    private protected virtual void WriteLinks()
    {
    }

    private void WriteSlot()
    {
        WriteId();
        WriteOrigins();
        WriteLinks();
        ref var slot = ref SlotStore.Slab.At(Handle);
        slot.Length = Length;
        slot.Info = SlotInfo;
    }
}
