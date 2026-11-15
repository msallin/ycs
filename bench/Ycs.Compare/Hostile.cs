using System.Diagnostics;
using System.Text.Json;

namespace Ycs.Compare;

using YDotNet.Document;
using YDotNet.Document.Options;

/// <summary>A malformed or hostile update, applied to a document holding <see cref="BaseV1"/> (a v1 state, may be empty).</summary>
internal sealed record HostileCase(string Name, string Source, bool Gc, bool V2, byte[] BaseV1, byte[] Update, string[] TextRoots);

/// <summary>
/// Malformed and hostile updates from ycs' fuzz corpus (tests/Ycs.Fuzz: FixedFailures.cs, Hostile.cs), applied in a
/// child process per library and case, so that a native crash, a hang or a runaway allocation cannot take the suite
/// down. ycs runs at its default limits, as for a peer's update; YDotNet has no limits to set.
/// </summary>
internal static class Hostile
{
    public static IReadOnlyList<HostileCase> All { get; } = Make();

    private static HostileCase Pinned(string name, bool gc, bool v2, string baseState, string update, params string[] roots) =>
        new(name, "FixedFailures.cs", gc, v2, Convert.FromBase64String(baseState), Convert.FromBase64String(update), roots);

    private static List<HostileCase> Make() =>
    [
        Pinned("alloc-struct-count", true, false, "", "AYCAgIAC", "text"),
        Pinned("alloc-struct-count-below-heap-limit", true, false, "", "AYCAgAg="),
        Pinned("alloc-any-length", false, false, "", "AQEAAEgAAP///3g="),
        Pinned("alloc-json-length", false, false, "", "N/cAAACiAACcz405"),
        Pinned("nonatomic-truncated-delete-set", true, false, "", "AwEAAAQBBAAAAAABAAIAAAQBBAAAAAAEAAAAAAAAAAEAcQA=", "text"),
        Pinned("nonatomic-truncated-v2", true, true, "AgEFAMgEAAQBAncEYzEuMX0LAgQACAEFYXJyYXkBfQGBBAAHAQQBAQc=", "AAACBgQBDgABiAEAAAABAQEBAHcEcDJhn2Q="),
        Pinned("nonatomic-malformed-delete-set", true, false, "AgIMAAQBBHRleHQE8J+RjYEMAQIDCwAEAQR0ZXh0AsOpRAsAGPCfkY3wn4+98J+YgPCfkY3wn4+98J2EnsQLAAwADPCfmIDwn5GN8J+PvQEMAQIC", "AAELAgAB//////8A", "text"),
        Pinned("nonatomic-negative-delete-length", true, false, "AQQBAAQBBHRleHQCVGiEAQEBZcQBAQECAiBxhAECBHVpY2sA", "AQEBCYQBAAABAAEAAA==", "text"),
        Pinned("nonatomic-find-index-out-of-range", false, false, "", "AQcKAAcBAQADAQAABgQAChEEAAAAACgAAAAFAAAAAAABdwEABwAAAAAAAAAAAAAAAAAAAAAA"),
        Pinned("nonatomic-zero-length-struct", false, false, "", "AQMAAAABAAAAAA=="),
        Pinned("nonatomic-find-index-unexpected", true, false, "", "AQEAAAEBAAAAAAA=", "text"),
        Pinned("nonatomic-clean-end-unexpected", true, true, "", "AAADAkEDAwAAAAEABwQBxAAEAIQkAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQEAAAMBAAMAAQA=", "text"),
        Pinned("nonatomic-any-splice-out-of-range", false, true, "", "AAACQQYGEhcAAAQIAQARBACEAScAAAAnAAgAKACoAERIPAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAKCwoAAAAAAAAABQEBAAIAAgAABAEDAAABCgB3CgAAAAAAAAAAAAB4dwAAAAAA", "text"),
        new("truncated-state", "made here: a 1,000-character text's v1 state cut in half", true, false, [], Truncated(), ["text"]),
        new("gc-bomb-pending-2M", "Hostile.cs: 2^21 GC structs from clock 1 (never integrates), a few bytes of v2", true, true, [], GcRun(1 << 21, 1), ["t"]),
        new("deleted-items-524288", "Hostile.cs: a valid state of 524,288 deleted items, 4 times ycs' default struct limit", true, true, [], DeletedItems(4 * 131_072), ["t"]),
        new("conflict-2000", "Hostile.cs: 2,000 items without origins after 2,000 others: 4 million conflict steps", true, false, ItemsWithoutOrigins(5, 2_000), ItemsWithoutOrigins(9, 2_000), ["t"]),
    ];

    /// <summary>Prints one JSON object: what applying the case did, how long it took, whether the document is still readable.</summary>
    public static int Run(string lib, string name)
    {
        var c = All.Single(h => h.Name == name);
        var sw = Stopwatch.StartNew();
        var (outcome, readable, unchanged) = lib == "ycs" ? Ycs(c) : Yrs(c);
        sw.Stop();
        using var stdout = Console.OpenStandardOutput();
        using var w = new Utf8JsonWriter(stdout);
        w.WriteStartObject();
        w.WriteString("outcome", outcome);
        w.WriteString("readable", readable);
        w.WriteString("unchanged", unchanged);
        w.WriteNumber("ms", sw.Elapsed.TotalMilliseconds);
        w.WriteNumber("peakRssMiB", PeakRssMiB());
        w.WriteEndObject();
        return 0;
    }

    private static (string, string, string) Ycs(HostileCase c)
    {
        var doc = new YDoc(new YDocOptions { Gc = c.Gc, ClientId = 1 });
        if (c.BaseV1.Length > 0)
        {
            doc.ApplyUpdateV1(c.BaseV1, null, UpdateLimits.Unlimited);
        }

        var before = doc.EncodeStateAsUpdateV1();
        string outcome;
        try
        {
            if (c.V2)
            {
                doc.ApplyUpdateV2(c.Update);
            }
            else
            {
                doc.ApplyUpdateV1(c.Update);
            }

            outcome = "applied";
        }
        catch (Exception e)
        {
            outcome = e is YUpdateException ye ? $"rejected: {e.GetType().Name}{(ye.PartlyApplied ? " (partly applied)" : string.Empty)}" : $"threw {e.GetType().Name}";
        }

        string readable;
        string unchanged;
        try
        {
            foreach (var r in c.TextRoots)
            {
                _ = doc.GetText(r).ToString();
            }

            var after = doc.EncodeStateAsUpdateV1();
            readable = "yes";
            unchanged = before.AsSpan().SequenceEqual(after) ? "yes" : "no";
        }
        catch (Exception e)
        {
            readable = $"no: {e.GetType().Name}";
            unchanged = "?";
        }

        return (outcome, readable, unchanged);
    }

    private static (string, string, string) Yrs(HostileCase c)
    {
        using var doc = new Doc(new DocOptions { Id = 1, SkipGarbageCollection = !c.Gc });
        var texts = c.TextRoots.Select(doc.Text).ToArray();
        if (c.BaseV1.Length > 0)
        {
            using var tx = doc.WriteTransaction();
            var r = tx.ApplyV1(c.BaseV1);
            if (r != YDotNet.Document.Transactions.TransactionUpdateResult.Ok)
            {
                return ($"base state rejected: {r}", "?", "?");
            }
        }

        var before = YrsCases.StateV1(doc);
        string outcome;
        try
        {
            using var tx = doc.WriteTransaction();
            var r = c.V2 ? tx.ApplyV2(c.Update) : tx.ApplyV1(c.Update);
            outcome = r == YDotNet.Document.Transactions.TransactionUpdateResult.Ok ? "applied" : $"rejected: {r}";
        }
        catch (Exception e)
        {
            outcome = $"threw {e.GetType().Name}";
        }

        string readable;
        string unchanged;
        try
        {
            foreach (var t in texts)
            {
                _ = YrsCases.Text(doc, t);
            }

            var after = YrsCases.StateV1(doc);
            readable = "yes";
            unchanged = before.AsSpan().SequenceEqual(after) ? "yes" : "no";
        }
        catch (Exception e)
        {
            readable = $"no: {e.GetType().Name}";
            unchanged = "?";
        }

        return (outcome, readable, unchanged);
    }

    private static long PeakRssMiB()
    {
        var line = File.ReadLines("/proc/self/status").First(l => l.StartsWith("VmHWM:", StringComparison.Ordinal));
        return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024;
    }

    private static byte[] Truncated()
    {
        var doc = new YDoc(new YDocOptions { ClientId = 7 });
        doc.GetText("text").Insert(0, string.Concat(Inputs.Typing.Take(1_000)));
        var v1 = doc.EncodeStateAsUpdateV1();
        return v1[..(v1.Length / 2)];
    }

    private static byte[] DeletedItems(int count)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 3 });
        var array = doc.GetArray("a");
        doc.Transact(_ =>
        {
            for (var i = 0; i < count; i++)
            {
                array.Insert(0, [1L]);
            }

            array.Delete(0, array.Length);
        });
        return doc.EncodeStateAsUpdateV2();
    }

    /// <summary>A v1 update of <paramref name="count"/> deleted items of <paramref name="client"/> in the root text "t", without origins.</summary>
    private static byte[] ItemsWithoutOrigins(long client, int count)
    {
        var bytes = new List<byte>();
        VarUint(bytes, 1);
        VarUint(bytes, (ulong)count);
        VarUint(bytes, (ulong)client);
        VarUint(bytes, 0);
        for (var i = 0; i < count; i++)
        {
            bytes.AddRange([1, 1, 1, (byte)'t', 1]);
        }

        VarUint(bytes, 0);
        return [.. bytes];
    }

    /// <summary><paramref name="count"/> GC structs of length 1 of client 1 from <paramref name="clock"/>, in a few bytes of v2.</summary>
    private static byte[] GcRun(long count, long clock)
    {
        var w = new List<byte> { 0 };
        void Column(params byte[] column)
        {
            VarUint(w, (ulong)column.Length);
            w.AddRange(column);
        }

        var lengths = new List<byte> { 0x41 };
        VarUint(lengths, (ulong)count - 2);
        Column();
        Column(1);
        Column();
        Column();
        Column(0);
        Column(0);
        Column();
        Column();
        Column([.. lengths]);
        VarUint(w, 1);
        VarUint(w, (ulong)count);
        VarUint(w, (ulong)clock);
        VarUint(w, 0);
        return [.. w];
    }

    private static void VarUint(List<byte> bytes, ulong value)
    {
        for (; value > 0x7F; value >>= 7)
        {
            bytes.Add((byte)(0x80 | (value & 0x7F)));
        }

        bytes.Add((byte)value);
    }
}
