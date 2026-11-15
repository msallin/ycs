using System.Runtime.CompilerServices;
using Ycs.Corpus;

namespace Ycs.Fuzz;

/// <summary>
/// Whether a document is a complete, encodable Yjs document after an update was applied, rejected or applied in
/// part: its state (v1 and v2) applies to a fresh document, which then (when nothing was pending) has the same
/// state vector; and every item list it and the fresh document hold, of the roots and of every type nested in them,
/// satisfies the structural part of <see cref="ItemListInvariants"/> (see <see cref="Structural"/>).
/// </summary>
internal static class Consistency
{
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_share")]
    private static extern ref Dictionary<string, AbstractType> Share(YDoc doc);

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
        var state = v2 ? doc.EncodeStateAsUpdateV2() : doc.EncodeStateAsUpdateV1();
        var fresh = new YDoc(new YDocOptions { Gc = doc.Gc, ClientId = 2, Limits = UpdateLimits.Unlimited });
        try
        {
            if (v2)
            {
                fresh.ApplyUpdateV2(state);
            }
            else
            {
                fresh.ApplyUpdateV1(state);
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

    // The invariants every list holds whatever the updates were: its links, parents, lengths and ids. The order
    // invariants (origins to the left, right origins to the right, YATA's order) hold for what Yjs writes; a crafted
    // update that Yjs applies too (a right origin left of the item, origins in another parent) breaks them in both.
    private static bool Structural(string violation) =>
        violation.StartsWith("Item ", StringComparison.Ordinal) || violation.StartsWith("uniqueId:", StringComparison.Ordinal);

    private static string? ListInvariants(YDoc doc)
    {
        var open = new Stack<(string Path, AbstractType Type)>(Share(doc).Select(kv => (kv.Key, kv.Value)));
        var seen = new HashSet<AbstractType>(ReferenceEqualityComparer.Instance);
        while (open.Count > 0)
        {
            var (path, type) = open.Pop();
            if (!seen.Add(type))
            {
                continue;
            }

            var runs = new List<ItemRun>();
            AbstractStruct? previous = null;
            for (AbstractStruct? s = type._start; s != null; s = (s as Item)?.Right)
            {
                if (s is not Item item)
                {
                    runs.Add(new ItemRun(s.Id.Client, s.Id.Clock, s.Length, null, null, s.Deleted, s.GetType().Name, LinkedBack: true, InParent: false));
                    break;
                }

                runs.Add(new ItemRun(
                    item.Id.Client,
                    item.Id.Clock,
                    item.Length,
                    item.LeftOrigin is ID o ? (o.Client, o.Clock) : null,
                    item.RightOrigin is ID r ? (r.Client, r.Clock) : null,
                    item.Deleted,
                    ItemRun.ItemKind,
                    LinkedBack: ReferenceEquals(item.Left, previous),
                    InParent: ReferenceEquals(item.Parent, type) && item.ParentSub == null));
                previous = item;
                if (item.Content is ContentType nested)
                {
                    open.Push(($"{path}[{runs.Count - 1}]", nested.Type));
                }
            }

            var broken = ItemListInvariants.Check(runs, full: runs.Count < 200).Violations.Where(Structural).ToList();
            if (broken.Count > 0)
            {
                return $"the item list of '{path}' breaks ItemListInvariants: {string.Join("; ", broken)}";
            }

            foreach (var (key, value) in type._map)
            {
                for (var item = value; item != null; item = item.Left as Item)
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
