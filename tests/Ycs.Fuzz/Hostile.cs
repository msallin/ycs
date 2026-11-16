namespace Ycs.Fuzz;

/// <summary>
/// Valid but hostile updates (the second review's js/hostile-gen.mjs, made here): run-length bombs of
/// GC structs, deleted items, shared types and subdocuments, beyond and at the harness' limits, and a
/// quadratic conflict. Each must apply or be rejected within the harness' ceilings; they are also
/// mutated with the other seeds.
/// </summary>
internal static class Hostile
{
    private static readonly Lazy<IReadOnlyList<Seed>> LazyAll = new(() => [.. Make()]);

    public static IReadOnlyList<Seed> All => LazyAll.Value;

    private static IEnumerable<Seed> Make()
    {
        var limit = (int)Harness.Limits.MaxStructsPerUpdate;
        yield return V2("gc-bomb-pending-2M", GcRun(1 << 21, clock: 1));
        yield return V2("gc-bomb-pending-at-limit", GcRun(limit, clock: 1));
        foreach (var n in new[] { limit, 4 * limit })
        {
            yield return V2($"deleted-items-{n}", Prepended(n, a => a.Insert(0, 1), deleteAll: true));
            yield return V2($"texts-{n}", Prepended(n, a => a.Insert(0, new YText())));
        }

        yield return V2("subdocuments-10000", Prepended(10_000, a => a.Insert(0, new YDoc())));

        // 2,000 items without origins of client 9 after 2,000 of client 5: 4 million conflict steps.
        yield return new Seed("conflict-2000", true, false, ItemsWithoutOrigins(5, 2_000), ItemsWithoutOrigins(9, 2_000), ["t"]);
    }

    private static Seed V2(string name, byte[] update) => new($"hostile/{name}", true, true, [], update, ["t"]);

    private static byte[] Prepended(int count, Action<YArray> insert, bool deleteAll = false)
    {
        var doc = new YDoc(new YDocOptions { ClientId = 3 });
        var array = doc.GetArray("a");
        doc.Transact(_ =>
        {
            for (var i = 0; i < count; i++)
            {
                insert(array);
            }

            if (deleteAll)
            {
                array.RemoveRange(0, array.Length);
            }
        });
        return doc.EncodeStateAsUpdate(format: UpdateFormat.V2);
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

        var lengths = new List<byte> { 0x41 }; // var-int -1: the length 1, repeated
        VarUint(lengths, (ulong)count - 2);
        Column(); // key clocks
        Column(1); // clients: 1
        Column(); // left clocks
        Column(); // right clocks
        Column(0); // info: GC, repeated
        Column(0); // strings
        Column(); // parent info
        Column(); // type refs
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
