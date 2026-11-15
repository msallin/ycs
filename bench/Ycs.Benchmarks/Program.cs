// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace Ycs.Benchmarks;

/// <summary>
/// Replays a real text-editing trace (see get-trace.sh) and measures, with a plain
/// Stopwatch and the GC's allocation counter:
///   replay       - every trace transaction as one Ycs transaction on a local YText,
///                  collecting the v1 update each transaction emits;
///   apply-incr   - applying those per-transaction updates to a fresh document (what a
///                  server receiving keystrokes does);
///   encode-v1/v2 - encoding the final document as one update;
///   decode-v1/v2 - applying that update to a fresh document.
/// Each phase runs once to warm up and then [runs] times; the median is reported.
/// encode and decode loop 10 times per run.
/// <br/>
/// <c>limits</c> instead of a trace measures updates at the default limits (<see cref="Limits"/>),
/// <c>perf</c> the operations of <see cref="Perf"/>.
/// <br/>
/// <c>loop &lt;trace.json.gz&gt; &lt;phase&gt; &lt;iterations&gt;</c> repeats one operation on one document (encode-v1, encode-v2,
/// encode-diff: against the state vector halfway; decode-v1, decode-v2: into a fresh document) and prints its time and
/// allocations per operation: what to attach a sampling profiler to.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: Ycs.Benchmarks <trace.json.gz> [runs] | limits | perf");
            return 2;
        }

        if (args[0] == "limits")
        {
            Limits.Run();
            return 0;
        }

        if (args[0] == "perf")
        {
            Perf.Run();
            return 0;
        }

        if (args[0] == "loop")
        {
            return Loop(args[1], args[2], int.Parse(args[3]));
        }

        var runs = args.Length > 1 ? int.Parse(args[1]) : 5;
        var (patches, txnStarts, endContent) = LoadTrace(args[0]);
        Console.WriteLine($"trace: {txnStarts.Count - 1} transactions, {patches.Count} patches, {endContent.Length} chars final");

        List<byte[]> updates = null;
        byte[] fullV1 = null, fullV2 = null;

        Measure("replay", runs, () =>
        {
            var doc = new YDoc(new YDocOptions { ClientId = 123456789 });
            var collected = new List<byte[]>(txnStarts.Count);
            doc.Update += (s, e) => collected.Add(e.data);
            var text = doc.GetText("text");
            for (int t = 0; t + 1 < txnStarts.Count; t++)
            {
                var from = txnStarts[t];
                var to = txnStarts[t + 1];
                doc.Transact(tr =>
                {
                    for (int i = from; i < to; i++)
                    {
                        var (pos, del, ins) = patches[i];
                        if (del > 0) text.Delete(pos, del);
                        if (ins.Length > 0) text.Insert(pos, ins);
                    }
                });
            }

            Check(text.ToString(), endContent);
            updates = collected;
            fullV1 = doc.EncodeStateAsUpdateV1();
            fullV2 = doc.EncodeStateAsUpdateV2();
        });

        Console.WriteLine($"  {updates.Count} updates, {updates.Sum(u => (long)u.Length)} bytes; doc v1 {fullV1.Length} bytes, v2 {fullV2.Length} bytes");

        Measure("apply-incr", runs, () =>
        {
            var doc = new YDoc();
            foreach (var u in updates) doc.ApplyUpdateV1(u);
            Check(doc.GetText("text").ToString(), endContent);
        });

        Func<YDoc> loaded = () =>
        {
            var doc = new YDoc();
            doc.ApplyUpdateV1(fullV1);
            return doc;
        };

        Measure("encode-v1", runs, null, loaded, d => { for (int i = 0; i < 10; i++) _ = d.EncodeStateAsUpdateV1(); });
        Measure("encode-v2", runs, null, loaded, d => { for (int i = 0; i < 10; i++) _ = d.EncodeStateAsUpdateV2(); });

        Measure("decode-v1", runs, () =>
        {
            for (int i = 0; i < 10; i++)
            {
                var doc = new YDoc();
                doc.ApplyUpdateV1(fullV1);
            }
        });

        Measure("decode-v2", runs, () =>
        {
            for (int i = 0; i < 10; i++)
            {
                var doc = new YDoc();
                doc.ApplyUpdateV2(fullV2);
            }
        });

        return 0;
    }

    private static int Loop(string tracePath, string phase, int iterations)
    {
        var (patches, txnStarts, endContent) = LoadTrace(tracePath);
        var doc = new YDoc(new YDocOptions { ClientId = 123456789 });
        var text = doc.GetText("text");
        byte[] halfVector = null;
        for (int t = 0; t + 1 < txnStarts.Count; t++)
        {
            if (t == (txnStarts.Count - 1) / 2)
            {
                halfVector = doc.EncodeStateVectorV1();
            }

            doc.Transact(tr =>
            {
                for (int i = txnStarts[t]; i < txnStarts[t + 1]; i++)
                {
                    var (pos, del, ins) = patches[i];
                    if (del > 0) text.Delete(pos, del);
                    if (ins.Length > 0) text.Insert(pos, ins);
                }
            });
        }

        Check(text.ToString(), endContent);
        var v1 = doc.EncodeStateAsUpdateV1();
        var v2 = doc.EncodeStateAsUpdateV2();
        var loaded = new YDoc();
        loaded.ApplyUpdateV1(v1);
        Action op = phase switch
        {
            "encode-v1" => () => loaded.EncodeStateAsUpdateV1(),
            "encode-v2" => () => loaded.EncodeStateAsUpdateV2(),
            "encode-diff" => () => loaded.EncodeStateAsUpdateV1(halfVector),
            "decode-v1" => () => new YDoc().ApplyUpdateV1(v1),
            "decode-v2" => () => new YDoc().ApplyUpdateV2(v2),
            _ => throw new ArgumentException("unknown phase " + phase),
        };

        for (var i = 0; i < Math.Min(iterations, 50); i++)
        {
            op();
        }

        var a0 = System.GC.GetTotalAllocatedBytes(precise: true);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            op();
        }

        sw.Stop();
        var a1 = System.GC.GetTotalAllocatedBytes(precise: true);
        Console.WriteLine($"{phase}: {sw.Elapsed.TotalMilliseconds * 1000 / iterations:F1} µs and {(a1 - a0) / iterations} bytes per operation ({iterations} operations)");
        return 0;
    }

    private static void Check(string actual, string expected)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException("replayed text does not match the trace's endContent");
        }
    }

    private static void Measure(string name, int runs, Action action, Func<YDoc> inner = null, Action<YDoc> body = null)
    {
        var times = new List<double>();
        var allocs = new List<long>();
        for (int r = 0; r < runs + 1; r++)
        {
            YDoc prepared = inner?.Invoke();
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            System.GC.Collect();
            var a0 = System.GC.GetTotalAllocatedBytes(precise: true);
            var sw = Stopwatch.StartNew();
            if (body != null) body(prepared); else action();
            sw.Stop();
            var a1 = System.GC.GetTotalAllocatedBytes(precise: true);
            if (r == 0) continue; // warm-up
            times.Add(sw.Elapsed.TotalMilliseconds);
            allocs.Add(a1 - a0);
        }

        times.Sort();
        allocs.Sort();
        Console.WriteLine($"{name,-11} median {times[times.Count / 2],9:F1} ms  (min {times[0],9:F1})  alloc {allocs[allocs.Count / 2] / (1024.0 * 1024.0),9:F1} MiB");
    }

    private static (List<(int pos, int del, string ins)> patches, List<int> txnStarts, string endContent) LoadTrace(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var json = JsonDocument.Parse(gz);
        var patches = new List<(int, int, string)>();
        var txnStarts = new List<int>();
        foreach (var txn in json.RootElement.GetProperty("txns").EnumerateArray())
        {
            txnStarts.Add(patches.Count);
            foreach (var p in txn.GetProperty("patches").EnumerateArray())
            {
                patches.Add((p[0].GetInt32(), p[1].GetInt32(), p[2].GetString()));
            }
        }

        txnStarts.Add(patches.Count);
        return (patches, txnStarts, json.RootElement.GetProperty("endContent").GetString());
    }
}
