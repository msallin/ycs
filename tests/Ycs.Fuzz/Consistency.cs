using System.Runtime.CompilerServices;

namespace Ycs.Fuzz;

/// <summary>
/// Whether a document is a complete, encodable Yjs document after an update was applied, rejected or applied in
/// part: its state (v1 and v2) applies to a fresh document, which then (when nothing was pending) has the same
/// state vector; and every item list it and the fresh document hold, of the roots and of every type nested in them,
/// satisfies the representation invariants of tests/Ycs.Corpus/ItemListInvariants.cs (see <see cref="ListInvariants"/>).
/// </summary>
internal static class Consistency
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_share")]
    private static extern ref Dictionary<string, YType>? Share(YDoc doc);

    /// <summary>What is wrong with <paramref name="doc"/>, or null.</summary>
    public static string? Failure(YDoc doc)
    {
        try
        {
            return ListInvariants(doc) ?? RoundTrip(doc, v2: false) ?? RoundTrip(doc, v2: true);
        }
        catch (Exception e)
        {
            return $"checking it threw {e.GetType().Name}: {e.Message}";
        }
    }

    private static string? RoundTrip(YDoc doc, bool v2)
    {
        var format = v2 ? "v2" : "v1";
        var state = v2 ? doc.EncodeStateAsUpdate(format: UpdateFormat.V2) : doc.EncodeStateAsUpdate();
        var fresh = new YDoc(new YDocOptions { GarbageCollection = doc.GarbageCollection, ClientId = 2, Limits = UpdateLimits.Unlimited });
        try
        {
            if (v2)
            {
                fresh.ApplyUpdate(state, format: UpdateFormat.V2);
            }
            else
            {
                fresh.ApplyUpdate(state);
            }
        }
        catch (Exception e)
        {
            return $"its {format} state does not apply to a fresh document: {e.GetType().Name}: {e.Message}";
        }

        if (ListInvariants(fresh) is { } broken)
        {
            return $"a fresh document from its {format} state: {broken}";
        }

        // With structs pending, the state merges them in, and the merge can drop what they waited for (a slice of a
        // struct gets the struct before it as its origin), so the fresh document may integrate them: in Yjs too.
        // Not the texts either: where a crafted update gives an item origins YATA would not (a right origin left of
        // it), the order of its items depends on the order they integrate in, in Yjs too.
        if (doc.HasPending)
        {
            return null;
        }

        if (!doc.EncodeStateVector().AsSpan().SequenceEqual(fresh.EncodeStateVector()))
        {
            return $"a fresh document from its {format} state has another state vector";
        }

        return fresh.HasPending ? $"a fresh document from its {format} state has pending structs" : null;
    }

    // The invariants every list holds whatever the updates were, those of ItemListInvariants' representation part:
    // each struct is an item linked back to the one before it, of a positive length, and no id is in two of them.
    // Not its order invariants (origins to the left, right origins to the right, YATA's order), which hold for what
    // Yjs writes: a crafted update that Yjs applies too (a right origin left of the item, a slice of a struct that
    // lands next to its origin in another parent) breaks them in Yjs as well; nor its check that every item's parent
    // is the list, which such a slice breaks in Yjs too. Its expansion into elements would also allocate per
    // element, and a deleted run may be 2^31 - 1 long.
    private static string? ListInvariants(YDoc doc)
    {
        var open = new Stack<(string Path, YType Type)>((Share(doc) ?? []).Select(kv => (kv.Key, kv.Value)));
        var seen = new HashSet<YType>(ReferenceEqualityComparer.Instance);
        while (open.Count > 0)
        {
            var (path, type) = open.Pop();
            if (!seen.Add(type))
            {
                continue;
            }

            var ids = new List<(long Client, long Clock, long End)>();
            var previous = default(ItemRef);
            for (var item = type._start; item.IsItem; item = item.Right)
            {
                if (item.Left != previous)
                {
                    return $"the item list of '{path}': item {item.Id.Client}:{item.Id.Clock} is not linked back to the one before it";
                }

                if (item.Length <= 0)
                {
                    return $"the item list of '{path}': item {item.Id.Client}:{item.Id.Clock} has length {item.Length}";
                }

                ids.Add((item.Id.Client, item.Id.Clock, item.Id.Clock + item.Length));
                previous = item;
                if (item.Content is ContentType nested)
                {
                    open.Push(($"{path}[{ids.Count - 1}]", nested.Type));
                }
            }

            ids.Sort();
            for (var k = 1; k < ids.Count; k++)
            {
                if (ids[k].Client == ids[k - 1].Client && ids[k].Clock < ids[k - 1].End)
                {
                    return $"the item list of '{path}': id {ids[k].Client}:{ids[k].Clock} is in two items";
                }
            }

            foreach (var (key, held) in type._map)
            {
                for (var item = type.MapItem(held); item.IsItem; item = item.Left)
                {
                    if (item.Content is ContentType nested)
                    {
                        open.Push(($"{path}.{key}", nested.Type));
                    }
                }
            }
        }

        return null;
    }
}
