namespace Ycs;

// A root type of a document that an update mentioned and no one has defined (GetArray, GetMap ...) yet; the
// definition moves its content into the type asked for.
internal sealed class YTypePlaceholder : YType
{
    // Never in an item (it is a root type, and inserting a type needs one that is in no document), so it is never
    // copied for a redo, cloned, or written.
    internal override YType InternalCopy() => throw Unreachable();
    internal override YType InternalClone() => throw Unreachable();
    internal override void Write(IUpdateEncoder encoder) => throw Unreachable();

    private static InvalidOperationException Unreachable() => new("A root type that is not defined yet is in no item: it cannot be copied, cloned or written.");

    internal override object ToJsonTree() => throw new InvalidOperationException("The type is not defined yet: ask the document for it by its kind (GetArray, GetMap, GetText ...).");
}
