#nullable disable // Not annotated for nullable reference types yet.

using System.Runtime.CompilerServices;

namespace Ycs;

// The content of an item, read and written by its kind. The kind in Info equals the kind of the object in Refs at every
// write (New, Adopt, a split, gc, BecomeGC); the accessors are what the code outside this file reads, so that the storage
// under them can move.
internal readonly partial struct ItemRef
{
    /// <summary>What the slot holds: <see cref="SlotKind.GC"/> for a GC struct, else the kind of the item's content.</summary>
    internal SlotKind Kind
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (SlotKind)Read().Kind;
    }

    /// <summary>The type of a <see cref="SlotKind.Type"/> item, by the table index its slot holds.</summary>
    internal YType TypeOf
    {
        get
        {
            ref readonly var slot = ref Read();
            if (slot.Kind != (int)SlotKind.Type)
            {
                throw new InvalidOperationException($"The struct holds {(SlotKind)slot.Kind}, not {SlotKind.Type} content.");
            }

            return _slab.Types[(int)slot.Payload]
                ?? throw new InvalidOperationException($"The type of the struct {Id.Client}:{Id.Clock} was collected; its table entry is cleared.");
        }
    }

    /// <summary>The content object of a format, embed, binary, JSON or document item; null for any other kind.</summary>
    internal IContentEx Cold => Kind.IsCold() ? (IContentEx)_slab.ReadRef(HandleChecked()) : null;

    /// <summary>Appends the elements of the content (Yjs' <c>getContent</c>) to <paramref name="destination"/>; an Any's without a copy.</summary>
    internal void AppendContentTo(List<object> destination)
    {
        if (Kind != SlotKind.Any)
        {
            destination.AddRange(Elements);
            return;
        }

        foreach (var value in ValueSpan)
        {
            destination.Add(ValueFreezer.OnRead(value));
        }
    }

    /// <summary>A copy of the content that belongs to no item (what a redone item holds).</summary>
    internal IContentEx CopyContent() => Kind switch
    {
        SlotKind.Type => TypeContent.Copy(this),
        SlotKind.Deleted or SlotKind.String or SlotKind.Any => throw new InvalidOperationException($"The {Kind} content of an item is copied by NewCopyOf, not as an object."),
        _ => (IContentEx)Content.Copy(),
    };

    /// <summary>Deletes what the content of the item holds (a type's nested items); the item is marked deleted by the caller.</summary>
    internal void DeleteContent(Transaction transaction)
    {
        switch (Kind)
        {
            case SlotKind.Deleted or SlotKind.String or SlotKind.Any:
                break;
            case SlotKind.Type:
                TypeContent.Delete(transaction, this);
                break;
            default:
                Content.Delete(transaction);
                break;
        }
    }

    /// <summary>Collects what the content of the item holds; the item itself is replaced by the caller.</summary>
    internal void GcContent(StructStore store)
    {
        switch (Kind)
        {
            case SlotKind.Deleted or SlotKind.String or SlotKind.Any:
                break;
            case SlotKind.Type:
                TypeContent.Gc(store, this);
                break;
            default:
                Content.Gc(store);
                break;
        }
    }

    /// <summary>Integrates the content of an item that is in the store's list; collected content is deleted.</summary>
    internal void IntegrateContent(Transaction transaction)
    {
        switch (Kind)
        {
            case SlotKind.Deleted:
                transaction.DeleteSet.Add(Id.Client, Id.Clock, Length);
                MarkDeleted();
                transaction.Doc.Store.Refresh(this);
                break;
            case SlotKind.String or SlotKind.Any:
                break;
            case SlotKind.Type:
                TypeContent.Integrate(transaction, this);
                break;
            default:
                Content.Integrate(transaction, this);
                break;
        }
    }

    // Fills the content storage of the new slot at handle from the content object (not a String's or an Any's: StoreChars,
    // StoreValues) or, for a Type, from type or else the type of the content object; the slot holds the kind and the length.
    private static void StoreContent(Slab slab, int handle, SlotKind kind, IContentEx content, YType type)
    {
        if (kind == SlotKind.Type)
        {
            var typeIndex = (type ?? ((ContentType)content).Type).TableIndex(slab.Types);
            slab.Write(handle).Payload = typeIndex;
        }
        else if (kind.HasObject())
        {
            slab.SetRef(handle, content);
        }
    }

    // Splits the content at diff, which this item keeps the part before: the right part's object (null for Deleted), its
    // length and its payload; a Type cannot be split.
    private (long Payload, int Length) SplitContent(int diff, out IContentEx content)
    {
        switch (Kind)
        {
            case SlotKind.Deleted:
                content = null;
                return (0, Length - diff);
            case SlotKind.Type:
                throw TypeContent.Unsplittable();
            case SlotKind.String:
                content = null;
                return (SplitChars(diff), Length - diff);
            case SlotKind.Any:
                content = null;
                return (SplitValues(diff), Length - diff);
            default:
                content = (IContentEx)Content.Splice(diff);
                return (0, content.Length);
        }
    }

    // Takes the right neighbour's content into this item's; Length is not yet the sum (see ItemRef.Chars.cs).
    private void MergeContent(ItemRef right)
    {
        switch (Kind)
        {
            case SlotKind.Deleted:
                break;
            case SlotKind.String:
                MergeChars(right);
                break;
            case SlotKind.Any:
                MergeValues(right);
                break;
            default:
                MergeObjects(right);
                break;
        }
    }

    private void MergeObjects(ItemRef right)
    {
        if (!Content.MergeWith(right.Content))
        {
            throw new InvalidOperationException(
                $"The content of {right.Id.Client}:{right.Id.Clock} did not merge into its left neighbour's although its kind merges (a bug of " +
                "Ycs). Nothing was changed.");
        }
    }

    // What the slot holds in the arenas ends: the item is collected, or its slot is freed.
    private void ReleaseContent()
    {
        if (Kind == SlotKind.String)
        {
            ReleaseChars();
        }
        else if (Kind == SlotKind.Any)
        {
            ReleaseValues();
        }
    }

    private int HandleChecked()
    {
        Check();
        return _handle;
    }

    private T Typed<T>(SlotKind kind)
        where T : class, IContentEx
    {
        var actual = Kind;
        return actual == kind
            ? (T)Content
            : throw new InvalidOperationException($"The struct holds {actual}, not {kind} content.");
    }
}
