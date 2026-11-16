namespace Ycs.Layout;

/// <summary>
/// What one item holds by kind, today and in the prototype: the documents of <c>bench/Ycs.Benchmarks sizes</c> (N items
/// that do not merge), the managed heap after a full collection before and after, divided by N. The strings and
/// values the caller passed in are shared, so neither side counts them; map keys are the caller's too.
/// </summary>
internal static class Sizes
{
    public static void Run(int n, Action<bool, string> check)
    {
        var keys = new string[n];
        for (var i = 0; i < n; i++)
        {
            keys[i] = "k" + i;
        }

        Console.WriteLine();
        Console.WriteLine("| kind (per operation) | B today | B prototype |");
        Console.WriteLine("| --- | ---: | ---: |");
        Measure("character, one item each (typed at the start)", n, check, doc =>
        {
            var text = doc.GetText("t");
            for (var i = 0; i < n; i++)
            {
                text.Insert(0, "x");
            }
        });

        Measure("array element (an integer), one item each", n, check, doc =>
        {
            var array = doc.GetArray("a");
            YValue value = 1L;
            for (var i = 0; i < n; i++)
            {
                array.Insert(0, value);
            }
        });

        Measure("map entry (distinct key, an integer)", n, check, doc =>
        {
            var map = doc.GetMap("m");
            YValue value = 1L;
            for (var i = 0; i < n; i++)
            {
                map[keys[i]] = value;
            }
        });

        Measure("array element and a deleted one (collected)", n, check, doc =>
        {
            var array = doc.GetArray("a");
            YValue value = 1L;
            for (var i = 0; i < n; i++)
            {
                array.Insert(0, value);
                array.Insert(0, value);
                array.RemoveRange(0, 1);
            }
        });

        System.GC.KeepAlive(keys);
        Console.WriteLine($"\nSlot: {SlotSize()} B (measured: an array of 1,000,000)");
    }

    private static long Held()
    {
        System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
        return System.GC.GetTotalMemory(forceFullCollection: true);
    }

    private static void Measure(string kind, int n, Action<bool, string> check, Action<YDoc> fill)
    {
        var m0 = Held();
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        fill(doc);
        var m1 = Held();
        var store = Builder.Build(doc);
        var weak = new WeakReference(doc);
        check(Builder.Check(store) == null, $"sizes, {kind}: {Builder.Check(store)}");
        doc = null;
        var m2 = Held();
        System.GC.KeepAlive(store);
        Console.WriteLine($"| {kind} | {(m1 - m0) / (double)n:F1} | {(m2 - m0) / (double)n:F1} |");
        check(!weak.IsAlive, $"sizes, {kind}: the document was still alive when the prototype was measured");
    }

    private static double SlotSize()
    {
        var m0 = Held();
        var slots = new Slot[1_000_000];
        var m1 = Held();
        System.GC.KeepAlive(slots);
        return (m1 - m0) / 1_000_000.0;
    }
}
