namespace Ycs;

/// <summary>
/// What a struct is, as Yjs' filters test with <c>instanceof</c>: an item by its content, a GC struct, a Skip. The
/// values are the reference numbers of the update format, so that a mapping needs no table.
/// </summary>
public enum ItemKind
{
    GC = 0,
    Deleted = 1,
    Json = 2,
    Binary = 3,
    String = 4,
    Embed = 5,
    Format = 6,
    Type = 7,
    Any = 8,
    Doc = 9,
    Skip = 10,
}
