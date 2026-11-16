#nullable disable // Not annotated for nullable reference types yet.

namespace Ycs;

// What the property tests (a project without InternalsVisibleTo, reading through UnsafeAccessor) ask of a type, in
// primitives and public types: no struct or content class leaves.
public partial class AbstractType
{
    /// <summary>The id of the item that holds this type, or null for a root or a detached type.</summary>
    internal ID? OwnerIdForTests => IsRoot ? null : ItemId;

    /// <summary>The key and the item id of every entry of the type's map.</summary>
    internal IEnumerable<KeyValuePair<string, ID>> MapIdsForTests() => _map.Select(kv => new KeyValuePair<string, ID>(kv.Key, kv.Value.Id));

    /// <summary>Whether this type, or the type that holds it, or the one above, is deleted.</summary>
    internal bool InDeletedTypeForTests()
    {
        for (var type = this; !type.IsRoot; type = type.ParentType)
        {
            if (type.ItemDeleted)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The structs of the type's list from <c>_start</c> by <c>Right</c>, up to the first GC struct, which ends it:
    /// whether each item's <c>Left</c> is the struct before it, and whether its parent is this type with no key.
    /// </summary>
    internal List<(long Client, long Clock, int Length, ID? LeftOrigin, ID? RightOrigin, bool Deleted, string Kind, bool LinkedBack, bool InParent)> ListForTests()
    {
        var runs = new List<(long, long, int, ID?, ID?, bool, string, bool, bool)>();
        var previous = default(ItemRef);
        for (var s = _start; !s.IsNull; s = s.Right)
        {
            if (!s.IsItem)
            {
                runs.Add((s.Id.Client, s.Id.Clock, s.Length, null, null, s.Deleted, StructStore.KindNameForTests(s), true, false));
                break;
            }

            runs.Add((s.Id.Client, s.Id.Clock, s.Length, s.LeftOrigin, s.RightOrigin, s.Deleted, StructStore.KindNameForTests(s), s.Left == previous, ReferenceEquals(s.Parent, this) && s.ParentSub == null));
            previous = s;
        }

        return runs;
    }

    /// <summary>The length and the text of the type's list recomputed by walking the items (counted: not deleted, countable; the text of the string contents).</summary>
    internal (int Length, string Text) CountedForTests()
    {
        var length = 0;
        var text = new System.Text.StringBuilder();
        for (var s = _start.AsItem; !s.IsNull; s = s.Right.AsItem)
        {
            if (s.Deleted || !s.Countable)
            {
                continue;
            }

            length += s.Length;
            if (s.Content is ContentString cs)
            {
                text.Append(cs.GetString());
            }
        }

        return (length, text.ToString());
    }
}
