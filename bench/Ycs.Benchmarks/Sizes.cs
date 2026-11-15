using System;
using System.Collections.Generic;

namespace Ycs.Benchmarks;

/// <summary>
/// What one item holds, by kind: N items made so that none merges with another, the managed heap after a full
/// collection before and after (the document alive), divided by N; and what making one allocated. The struct
/// store's list and the type's map count in (a reference per item, a dictionary entry per key); the strings and
/// values the caller passed in do not, as they are shared. <c>sizes</c> runs it.
/// </summary>
internal static class Sizes
{
    public static void Run(int n)
    {
        var N = n;
        Console.WriteLine($"{"kind",-44} {"held B/item",12} {"alloc B/op",11}");
        Measure("character, one item each (typed at the start)", N, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var text = doc.GetText("t");
            return (doc, i => text.Insert(0, "x"));
        });

        Measure("run of 16 characters, one item each", N / 4, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var text = doc.GetText("t");
            const string run = "abcdefghijklmnop";
            return (doc, i => text.Insert(0, run));
        });

        Measure("array element (an integer), one item each", N, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var array = doc.GetArray("a");
            var value = new object[] { 1L };
            return (doc, i => array.Insert(0, value));
        });

        var keys = new string[N];
        for (var i = 0; i < N; i++)
        {
            keys[i] = "k" + i;
        }

        Measure("map entry (distinct key, an integer)", N, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var map = doc.GetMap("m");
            object value = 1L;
            return (doc, i => map.Set(keys[i], value));
        });

        Measure("array element and a deleted one (collected)", N, () =>
        {
            // An array, not a text: a text insert at the start goes after the deleted items there (as in Yjs), which
            // would make this quadratic. Each deleted element sits between two live ones, so none merges.
            var doc = new YDoc(new YDocOptions { ClientId = 1 });
            var array = doc.GetArray("a");
            var value = new object[] { 1L };
            return (doc, i =>
            {
                array.Insert(0, value);
                array.Insert(0, value);
                array.Delete(0, 1);
            });
        });
    }

    private static void Measure(string kind, int n, Func<(YDoc Doc, Action<int> Op)> setup)
    {
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
        var before = System.GC.GetTotalMemory(forceFullCollection: true);
        var (doc, op) = setup();
        var allocated0 = System.GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
        {
            op(i);
        }

        var allocated = System.GC.GetAllocatedBytesForCurrentThread() - allocated0;
        var after = System.GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"{kind,-44} {(after - before) / (double)n,12:F1} {allocated / (double)n,11:F1}");
        System.GC.KeepAlive(doc);
    }
}
