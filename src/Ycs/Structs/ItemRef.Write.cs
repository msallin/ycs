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
            encoder.WriteInfo(GC.StructGCRefNumber);
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
            Item.WriteItemHeader(encoder, ContentDeleted._ref, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
            encoder.WriteLength(Length - offset);
            return;
        }

        if (Kind == SlotKind.Type)
        {
            Item.WriteItemHeader(encoder, ContentType._ref, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
            TypeOf.Write(encoder);
            return;
        }

        if (Kind == SlotKind.String)
        {
            Item.WriteItemHeader(encoder, ContentString._ref, offset, Id, LeftOrigin, RightOrigin, ParentSub, root, parentId);
            encoder.WriteString(Chars[offset..]);
            return;
        }

        Item.WriteItem(encoder, offset, Id, LeftOrigin, RightOrigin, ParentSub, Content, root, parentId);
    }
}
