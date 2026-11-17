namespace Ycs;

// A root type of a document that an update mentioned and no one has defined (GetArray, GetMap ...) yet; the
// definition moves its content into the type asked for.
internal sealed class YTypePlaceholder : YType
{
    internal override object ToJsonTree() => throw new InvalidOperationException("The type is not defined yet: ask the document for it by its kind (GetArray, GetMap, GetText ...).");
}
