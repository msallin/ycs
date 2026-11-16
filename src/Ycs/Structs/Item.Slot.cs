#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal partial class Item
{
    private const int SlotFlagMask = (int)(InfoEnum.Keep | InfoEnum.Countable | InfoEnum.Deleted | InfoEnum.Marker | InfoEnum.HasRedone);

    private protected override int SlotInfo => ((int)_info & SlotFlagMask) | (_content?.Ref ?? 0) << 8;

    // The slot's links are the neighbours' handles, -1 for none. A neighbour that had no slot when the link was written is
    // -1 there: when this struct gets its slot, the neighbours that point at it are written too.
    private protected override void WriteLinks()
    {
        ref var slot = ref SlotStore.Slab.At(Handle);
        slot.Left = _left?.Handle ?? -1;
        slot.Right = _right?.Handle ?? -1;
        WriteParent();
        if (_left is Item { Handle: >= 0 } left && left._right == this && left.SlotStore == SlotStore)
        {
            SlotStore.Slab.At(left.Handle).Right = Handle;
        }

        if (_right is Item { Handle: >= 0 } right && right._left == this && right.SlotStore == SlotStore)
        {
            SlotStore.Slab.At(right.Handle).Left = Handle;
        }
    }

    // The parent is its index in the store's type table; a root's name, an id and null (not integrated) are -1.
    private void WriteParent() =>
        SlotStore.Slab.At(Handle).Parent = _parent is AbstractType type ? type.TableIndex(SlotStore.Types) : -1;

    // An origin is its client's index and clock; no origin is client -1.
    private protected override void WriteOrigins()
    {
        var left = LeftOrigin;
        var right = RightOrigin;
        var leftClient = left is ID l ? SlotStore.ClientTable.GetOrAdd(l.Client) : -1;
        var rightClient = right is ID r ? SlotStore.ClientTable.GetOrAdd(r.Client) : -1;
        ref var slot = ref SlotStore.Slab.At(Handle);
        slot.OriginClient = leftClient;
        slot.OriginClock = left?.Clock ?? 0;
        slot.ROriginClient = rightClient;
        slot.ROriginClock = right?.Clock ?? 0;
    }
}
