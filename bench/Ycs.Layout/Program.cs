using System.Diagnostics;
using Ycs;
using Ycs.Compare;
using Ycs.Layout;

// dotnet run -c Release --project bench/Ycs.Layout -- <dir with trace.json.gz> [memory|sizes|small|ops|edit|all] [workload]
// run.sh runs each part in a process of its own (a process's earlier workloads move the heap figures of later ones).
// The workloads are the comparison's (bench/Ycs.Compare/YcsCases.cs, the same inputs); each document is checked against
// the one the comparison builds (its v1 state, byte for byte) and the prototype against the document (its content).
var dir = args.Length > 0 ? args[0] : throw new ArgumentException("the directory of trace.json.gz");
var mode = args.Length > 1 ? args[1] : "all";
var only = args.Length > 2 ? args[2] : null;
var fixtures = new Fixtures(dir);
var failures = 0;

void Check(bool ok, string what)
{
    if (!ok)
    {
        failures++;
        Console.WriteLine($"CHECK FAILED: {what}");
    }
}

static long Held()
{
    System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
    System.GC.WaitForPendingFinalizers();
    System.GC.Collect(System.GC.MaxGeneration, System.GCCollectionMode.Forced, blocking: true, compacting: true);
    return System.GC.GetTotalMemory(forceFullCollection: true);
}

static string MiB(long bytes) => (bytes / 1048576.0).ToString("F1");

if (mode is "header")
{
    Console.WriteLine("| workload | items (structs) | MiB today (edited) | MiB today (loaded from its state) | MiB prototype | prototype / today |");
    Console.WriteLine("| --- | ---: | ---: | ---: | ---: | ---: |");
    return 0;
}

if (mode is "memory" or "all")
{
    foreach (var w in Workloads.All(fixtures).Where(w => only == null || w.Name == only))
    {
        w.Warm();
        var m0 = Held();
        var docs = w.Build();
        var m1 = Held();
        var states = docs.Select(d => d.EncodeStateAsUpdate()).ToArray();
        var structs = docs.Sum(d => d.Store.Clients.Values.Sum(l => (long)l.Count));

        if (w.Reference != null)
        {
            Check(states[0].AsSpan().SequenceEqual(w.Reference()), $"{w.Name}: the document is not the comparison's");
        }

        var stores = docs.Select(Builder.Build).ToArray();
        for (var i = 0; i < docs.Length; i++)
        {
            Check(Builder.Check(stores[i]) == null, $"{w.Name}: {Builder.Check(stores[i])}");
            Check(w.Same(docs[i], stores[i]), $"{w.Name}: the prototype's content differs");
        }

        var weak = new WeakReference(docs[0]);
        docs = null;
        // The prototype: what dropping the stores frees (the encoders' thread-static buffers, the states and the inputs
        // stay alive on both sides of the difference).
        var m2 = Held();
        System.GC.KeepAlive(stores);
        Check(!weak.IsAlive, $"{w.Name}: the document was still alive when the prototype was measured");
        stores = null;

        var m3 = Held();
        var loaded = states.Select(s =>
        {
            var d = new YDoc();
            d.ApplyUpdate(s, null, limits: UpdateLimits.Unlimited);
            return d;
        }).ToArray();
        var loadedHeld = Held() - m3;
        System.GC.KeepAlive(loaded);
        System.GC.KeepAlive(states);

        Console.WriteLine($"| `{w.Name}` | {structs:N0} | {MiB(m1 - m0)} | {MiB(loadedHeld)} | {MiB(m2 - m3)} | {(m2 - m3) / (double)(m1 - m0):F2} |");
    }
}

if (mode is "sizes" or "all")
{
    Sizes.Run(100_000, Check);
}

if (mode is "small" or "all")
{
    Small.Run(Check);
}

if (mode is "ops" or "all")
{
    Ops.Run(fixtures, Check);
}

if (mode is "edit" or "all")
{
    Editing.Run(fixtures, Check);
}

Console.WriteLine(failures == 0 ? "all checks passed" : $"{failures} checks FAILED");
return failures == 0 ? 0 : 1;
