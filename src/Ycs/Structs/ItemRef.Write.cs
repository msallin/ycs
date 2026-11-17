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
        string root = null;
        ID parentId = default;
        if (offset == 0 && LeftOrigin == null && RightOrigin == null)
        {
            var parent = Parent ?? throw new InvalidOperationException("An item without origins has no parent.");
            if (parent.IsRoot)
            {
                root = parent.FindRootTypeKey();
            }
            else
            {
                parentId = parent.ItemId;
            }
        }

        if (Kind == SlotKind.Deleted)
        {
            WriteItemHeader(encoder, (int)SlotKind.Deleted, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
            encoder.WriteLength(Length - offset);
            return;
        }

        if (Kind == SlotKind.Type)
        {
            WriteItemHeader(encoder, (int)SlotKind.Type, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
            TypeOf.Write(encoder);
            return;
        }

        if (Kind == SlotKind.String)
        {
            WriteItemHeader(encoder, (int)SlotKind.String, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
            encoder.WriteString(Chars[offset..]);
            return;
        }

        if (Kind == SlotKind.Any)
        {
            WriteItemHeader(encoder, (int)SlotKind.Any, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
            WriteValues(encoder, offset);
            return;
        }

        WriteItemHeader(encoder, (int)Kind, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
        Cold.Write(encoder, offset);
    }

    /// <summary>
    /// What the encoding of an item has before its content: the info byte (<paramref name="contentRef"/>, the origins
    /// and the parentSub flag), the origins, and the parent when neither origin is written (<paramref name="offset"/> 0
    /// and no origins): <paramref name="parentRoot"/> names a root type, or, when it is null, <paramref name="parentId"/>
    /// is the id of the type's item.
    /// </summary>
    internal static void WriteItemHeader(IUpdateEncoder encoder, int contentRef, int offset, ID id, ID? leftOrigin, ID? rightOrigin, string parentSub, string parentRoot, ID parentId)
    {
        var origin = offset > 0 ? new ID(id.Client, id.Clock + offset - 1) : leftOrigin;
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
