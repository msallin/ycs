using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Ycs.Benchmarks;

/// <summary>
/// What an update at the limits of <see cref="UpdateLimits.Default"/> costs a document: for each shape
/// the bytes of the update, what applying it allocates, what the document keeps, and how long it
/// takes. The updates are valid (made through the public API, except the GC structs), so they also
/// show what an honest update of that many structs costs. The numbers behind docs/api.md, "Untrusted input".
/// </summary>
internal static class Limits
{
    private static readonly long N = UpdateLimits.Default.MaxStructsPerUpdate;

    public static void Run()
    {
        Console.WriteLine($"UpdateLimits.Default: {N} structs per update, {UpdateLimits.Default.MaxPendingStructs} pending structs, {UpdateLimits.Default.MaxPendingBytes} pending bytes");
        Console.WriteLine($"{"shape",-58} {"v1 B",9} {"v2 B",9} {"ms",7} {"alloc MiB",10} {"kept MiB",9} {"B/struct",9}");

        Integrated("characters (prepended, one item each)", a => a.Doc.GetText("t").Insert(0, "x"));
        Integrated("deleted items", a => a.Insert(0, 1), deleteAll: true);
        Integrated("YText", a => a.Insert(0, new YText()));
        Integrated("YArray", a => a.Insert(0, new YArray()));
        Integrated("YMap", a => a.Insert(0, new YMap()));
        Integrated("YXmlElement", a => a.Insert(0, new YXmlElement("p")));
        Integrated("subdocuments", a => a.Insert(0, new YDoc()));

        // Pending: N shared types that wait for the first struct of their client.
        var (first, waiting) = WaitingTypes(10);
        var doc = new YDoc();
        Report("YText, pending", Bytes(waiting), waiting.Length, N, () => doc.ApplyUpdate(waiting, format: UpdateFormat.V2), doc);
        Report("  EncodeStateAsUpdate with them pending", 0, 0, N, () => doc.EncodeStateAsUpdate(), doc);

        // The largest retry: N wait; one update lets them integrate and brings N more that wait.
        var (first2, waiting2) = WaitingTypes(11);
        var trigger = YUpdate.Merge([first, waiting2], format: UpdateFormat.V2);
        Report("  an update lets them integrate and brings N more", 0, trigger.Length, 2 * N, () => doc.ApplyUpdate(trigger, format: UpdateFormat.V2), doc);
        System.GC.KeepAlive(first2);

        var gc = GcRun(N, clock: 1);
        var gcDoc = new YDoc();
        Report("GC structs, pending (hand-written v2)", 0, gc.Length, N, () => gcDoc.ApplyUpdate(gc, format: UpdateFormat.V2), gcDoc);
        Report("  EncodeStateAsUpdate with them pending", 0, 0, N, () => gcDoc.EncodeStateAsUpdate(), gcDoc);
    }

    private static void Integrated(string shape, Action<YArray> insert, bool deleteAll = false)
    {
        var source = new YDoc(new YDocOptions { ClientId = 3 });
        var array = source.GetArray("a");
        source.Transact(_ =>
        {
            for (var i = 0; i < N; i++)
            {
                insert(array);
            }

            if (deleteAll)
            {
                array.RemoveRange(0, array.Count);
            }
        });

        var v2 = source.EncodeStateAsUpdate(format: UpdateFormat.V2);
        var doc = new YDoc();
        Report(shape, source.EncodeStateAsUpdate().Length, v2.Length, N, () => doc.ApplyUpdate(v2, format: UpdateFormat.V2), doc);
    }

    private static (byte[] First, byte[] Waiting) WaitingTypes(long client)
    {
        var source = new YDoc(new YDocOptions { ClientId = client });
        source.GetText("t").Insert(0, "x");
        var first = source.EncodeStateAsUpdate(format: UpdateFormat.V2);
        var sv = source.EncodeStateVector();
        var array = source.GetArray("a");
        source.Transact(_ =>
        {
            for (var i = 1; i < N; i++)
            {
                array.Insert(0, new YText());
            }
        });

        return (first, source.EncodeStateAsUpdate(sv, format: UpdateFormat.V2));
    }

    private static int Bytes(byte[] v2) => YUpdate.Convert(v2, UpdateFormat.V2, UpdateFormat.V1, limits: UpdateLimits.Unlimited).Length;

    /// <summary><paramref name="count"/> GC structs of length 1 of client 1 from <paramref name="clock"/>, in a few bytes of v2.</summary>
    internal static byte[] GcRun(long count, long clock)
    {
        var w = new List<byte> { 0 };
        void VarUint(ulong v)
        {
            while (v > 0x7F)
            {
                w.Add((byte)(0x80 | (v & 0x7F)));
                v >>= 7;
            }

            w.Add((byte)v);
        }

        void Column(params byte[] bytes)
        {
            VarUint((ulong)bytes.Length);
            w.AddRange(bytes);
        }

        var lengths = new List<byte> { 0x41 }; // var-int -1: the length 1, repeated
        var repeat = (ulong)count - 2;
        while (repeat > 0x7F)
        {
            lengths.Add((byte)(0x80 | (repeat & 0x7F)));
            repeat >>= 7;
        }

        lengths.Add((byte)repeat);
        Column(); // key clocks
        Column(1); // clients: 1
        Column(); // left clocks
        Column(); // right clocks
        Column(0); // info: GC, repeated
        Column(0); // strings
        Column(); // parent info
        Column(); // type refs
        Column(lengths.ToArray());
        VarUint(1);
        VarUint((ulong)count);
        VarUint((ulong)clock);
        VarUint(0);
        return w.ToArray();
    }

    private static void Report(string shape, int v1, int v2, long structs, Action apply, YDoc keep)
    {
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        System.GC.Collect();
        var before = System.GC.GetTotalMemory(true);
        var a0 = System.GC.GetTotalAllocatedBytes(true);
        var sw = Stopwatch.StartNew();
        string failure = null;
        try
        {
            apply();
        }
        catch (YUpdateException e)
        {
            failure = e.Message;
        }

        sw.Stop();
        var allocated = System.GC.GetTotalAllocatedBytes(true) - a0;
        var kept = System.GC.GetTotalMemory(true) - before;
        System.GC.KeepAlive(keep);
        Console.WriteLine($"{shape,-58} {v1,9} {v2,9} {sw.ElapsedMilliseconds,7} {allocated / 1048576.0,10:F1} {kept / 1048576.0,9:F1} {Math.Max(allocated, kept) / structs,9}"
            + (failure == null ? string.Empty : "  rejected: " + failure));
    }
}
