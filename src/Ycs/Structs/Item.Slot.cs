#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal partial class Item
{
    private const int SlotFlagMask = (int)(InfoEnum.Keep | InfoEnum.Countable | InfoEnum.Deleted | InfoEnum.Marker | InfoEnum.HasRedone);

    private protected override int SlotInfo => ((int)_info & SlotFlagMask) | (_content?.Ref ?? 0) << 8;

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
