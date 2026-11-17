
namespace Ycs;

internal sealed partial class StructStore
{
    /// <summary>
    /// What is wrong with the value arena, or null: every Any slot's range lies in the used part of a held page, the ranges
    /// are disjoint (from each other and from the reservations), the dead count equals a recount, the tail table names
    /// only the ends of live ranges (<see cref="Arena{T}.Violations"/>), and no element outside a live range holds a
    /// value (every exit of a range clears it). A store of more than 4,096 slots is checked every count/32nd time; that a
    /// range lies in a held page is checked by <see cref="SlotViolation(ItemRef)"/>.
    /// </summary>
    private string? ValuesViolation()
    {
        if (Slab.LiveCount > 4096 && _checks % (Slab.LiveCount / 32) != 0)
        {
            return null;
        }

        var ranges = new List<(long Offset, int Length)>();
        foreach (var list in Clients.Values)
        {
            foreach (var s in list)
            {
                if (s.IsItem && s.Kind == SlotKind.Any)
                {
                    ranges.Add((Slab.Read(s.Handle).Payload, s.Length));
                }
            }
        }

        var values = Slab.ValuesOrNull;
        if (values == null)
        {
            return ranges.Count == 0 ? null : "Any items hold ranges of an arena that was never made";
        }

        if (values.Violations(ranges).FirstOrDefault() is { } violation)
        {
            return violation;
        }

        var kept = values.UnclearedOutside(ranges);
        return kept == 0 ? null : $"{kept} elements outside the live ranges still hold a value";
    }
}
