using System;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Ycs.Benchmarks;

/// <summary>
/// Whether documents that nothing references survive a young collection: loads a state of n structs of one client
/// (11,000 by default, about the trace's state; much larger ones trigger collections while they load) into five
/// documents, drops them, and counts those still alive after a gen0, a gen1 and a gen2 collection. From 10,625 structs
/// the decoded list's array is on the large object heap, which only a gen2 collection frees; uncleared, it kept the
/// five alive until then (bench/Ycs.Compare/README.md, "Why").
/// </summary>
internal static class GcSurvive
{
    public static void Run(int n)
    {
        var source = new YDoc(new YDocOptions { ClientId = 1 });
        var text = source.GetText("t");
        for (var i = 0; i < n; i++)
        {
            // Typed backwards: one struct per character.
            text.Insert(0, "x");
        }

        var state = source.EncodeStateAsUpdate();
        for (var warm = 0; warm < 3; warm++)
        {
            Load(state);
        }

        System.GC.Collect(2, GCCollectionMode.Forced, true, true);
        var weak = Enumerable.Range(0, 5).Select(_ => Load(state)).ToList();
        var alive = new int[3];
        for (var gen = 0; gen <= 2; gen++)
        {
            System.GC.Collect(gen, GCCollectionMode.Forced, true, false);
            alive[gen] = weak.Count(w => w.IsAlive);
        }

        Console.WriteLine($"{n} structs, five documents loaded and dropped: alive after a gen0 collection {alive[0]}, gen1 {alive[1]}, gen2 {alive[2]}");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference Load(byte[] state)
    {
        var doc = new YDoc();
        doc.ApplyUpdate(state, null, limits: UpdateLimits.Unlimited);
        return new WeakReference(doc);
    }
}
