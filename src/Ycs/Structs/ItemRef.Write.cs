#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

internal readonly partial struct ItemRef
{
    public void Write(IUpdateEncoder encoder, int offset)
    {
        if (IsItem)
        {
            WriteItem(encoder, offset);
        }
        else
        {
            encoder.WriteInfo((byte)SlotKind.GC);
            encoder.WriteLength(Length - offset);
        }
    }

    private void WriteItem(IUpdateEncoder encoder, int offset)
    {
        // The encoder allocates no slot: the reference stays valid for the whole write.
        ref readonly var slot = ref Read();
        var clients = _slab.Clients;
        var id = new ItemId(clients[slot.Client], slot.Clock);
        ItemId? leftOrigin = slot.OriginClient < 0 ? null : new ItemId(clients[slot.OriginClient], slot.OriginClock);
        ItemId? rightOrigin = slot.ROriginClient < 0 ? null : new ItemId(clients[slot.ROriginClient], slot.ROriginClock);
        var kind = (SlotKind)slot.Kind;
        var parentSub = (string)_slab.ReadKey(_handle);
        string root = null;
        ItemId parentId = default;
        if (offset == 0 && leftOrigin == null && rightOrigin == null)
        {
            var parent = (slot.Parent < 0 ? null : _slab.Types[slot.Parent]) ?? throw new InvalidOperationException("An item without origins has no parent.");
            if (parent.IsRoot)
            {
                root = parent.FindRootTypeKey();
            }
            else
            {
                parentId = parent.ItemId;
            }
        }

        WriteItemHeader(encoder, (int)kind, offset, id, leftOrigin, rightOrigin, parentSub, root, parentId);
        switch (kind)
        {
            case SlotKind.Deleted:
                encoder.WriteLength(slot.Length - offset);
                break;
            case SlotKind.Type:
                TypeOf.Write(encoder);
                break;
            case SlotKind.String:
                encoder.WriteString(Chars[offset..]);
                break;
            case SlotKind.Any:
                WriteValues(encoder, offset);
                break;
            default:
                Cold.Write(encoder, offset);
                break;
        }
    }

    /// <summary>
    /// What the encoding of an item has before its content: the info byte (<paramref name="contentRef"/>, the origins
    /// and the parentSub flag), the origins, and the parent when neither origin is written (<paramref name="offset"/> 0
    /// and no origins): <paramref name="parentRoot"/> names a root type, or, when it is null, <paramref name="parentId"/>
    /// is the id of the type's item.
    /// </summary>
    internal static void WriteItemHeader(IUpdateEncoder encoder, int contentRef, int offset, ItemId id, ItemId? leftOrigin, ItemId? rightOrigin, string parentSub, string parentRoot, ItemId parentId)
    {
        var origin = offset > 0 ? new ItemId(id.Client, id.Clock + offset - 1) : leftOrigin;
        var info = (contentRef & Bits.Bits5) |
            (origin == null ? 0 : Bit.Bit8) |
            (rightOrigin == null ? 0 : Bit.Bit7) |
            (parentSub == null ? 0 : Bit.Bit6);
        encoder.WriteInfo((byte)info);

        if (origin != null)
        {
            encoder.WriteLeftId(origin.Value);
        }

        if (rightOrigin != null)
        {
            encoder.WriteRightId(rightOrigin.Value);
        }

        if (origin == null && rightOrigin == null)
        {
            if (parentRoot != null)
            {
                encoder.WriteParentInfo(true);
                encoder.WriteString(parentRoot);
            }
            else
            {
                encoder.WriteParentInfo(false);
                encoder.WriteLeftId(parentId);
            }

            if (parentSub != null)
            {
                encoder.WriteString(parentSub);
            }
        }
    }
}
