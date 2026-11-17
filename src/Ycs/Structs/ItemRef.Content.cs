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
    internal AbstractType TypeOf
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

    /// <summary>Appends the elements of the content (Yjs' <c>getContent</c>) to <paramref name="destination"/>.</summary>
    internal void AppendContentTo(List<object> destination) => destination.AddRange(Elements);

    /// <summary>A copy of the content that belongs to no item (what a redone item holds).</summary>
    internal IContentEx CopyContent() => Kind switch
    {
        SlotKind.Deleted => new ContentDeleted(Length),
        SlotKind.Type => TypeContent.Copy(this),
        _ => (IContentEx)Content.Copy(),
    };

    /// <summary>Deletes what the content of the item holds (a type's nested items); the item is marked deleted by the caller.</summary>
    internal void DeleteContent(Transaction transaction)
    {
        switch (Kind)
        {
            case SlotKind.Deleted:
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
            case SlotKind.Deleted:
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
            case SlotKind.Type:
                TypeContent.Integrate(transaction, this);
                break;
            default:
                Content.Integrate(transaction, this);
                break;
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
