using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs;

[TestClass]
public class StableSortTests
{
    [TestMethod]
    [DataRow(5)]
    [DataRow(33)]
    [DataRow(1000)]
    public void Sort_ManyTies_KeepsTheOrderOfEqualElements(int count)
    {
        // Keys 0..6 repeating backwards; the value is the original position.
        var list = Enumerable.Range(0, count).Select(i => (Key: 6 - (i % 7), Position: i)).ToList();
        var expected = list.OrderBy(e => e.Key).ToList();

        StableSort.Sort(list, static (a, b) => a.Key.CompareTo(b.Key));

        CollectionAssert.AreEqual(expected, list);
    }

    /// <summary>
    /// The cases of interop/js/v8-sort.mjs (Fixtures/v8-sort.json): random comparisons, comparisons by key with a few
    /// random answers, by key, and by key with ties that answer -1 both ways, on 0 to 2049 elements; the order must be
    /// the one V8's sort gave.
    /// </summary>
    [TestMethod]
    public void Sort_ComparisonsConsistentOrNot_OrdersAsV8()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v8-sort.json")));
        var differing = new List<string>();
        foreach (var c in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var (n, kind, range, seed) = (c.GetProperty("n").GetInt32(), c.GetProperty("kind").GetInt32(), c.GetProperty("range").GetInt32(), (uint)c.GetProperty("seed").GetInt32());
            var keys = Keys(seed, n, range);
            var list = Enumerable.Range(0, n).ToList();

            StableSort.Sort(list, (a, b) => Compare(kind, seed, keys, a, b));

            if (Fnv(list) != c.GetProperty("hash").GetUInt32())
            {
                differing.Add($"n {n} kind {kind} range {range} seed {seed}");
            }
        }

        Assert.AreEqual("", string.Join("; ", differing));
    }

    [TestMethod]
    public void Sort_TieAnsweringLessBothWays_PutsTheLaterFirstAsV8()
    {
        // Yjs' comparison of a GC struct and an item at one clock.
        var list = new List<int> { 0, 1 };

        StableSort.Sort(list, static (_, _) => -1);

        Assert.AreEqual("1,0", string.Join(",", list));
    }

    // The functions of v8-sort.mjs.
    private static uint Mix32(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352d;
        x ^= x >> 15;
        x *= 0x846ca68b;
        return x ^ (x >> 16);
    }

    private static uint Pair(uint seed, int a, int b) => Mix32(Mix32(seed) + ((uint)a * 0x9e3779b1) + ((uint)b * 0x85ebca77));

    private static Func<uint> Mulberry32(uint seed)
    {
        var a = (int)seed;
        return () =>
        {
            a = unchecked(a + 0x6d2b79f5);
            var t = unchecked((a ^ (int)((uint)a >> 15)) * (1 | a));
            t = unchecked(t + ((t ^ (int)((uint)t >> 7)) * (61 | t))) ^ t;
            return (uint)(t ^ (int)((uint)t >> 14));
        };
    }

    private static List<int> Keys(uint seed, int n, int range)
    {
        var next = Mulberry32(seed);
        var keys = new List<int>(n);
        var r = (uint)range;
        while (keys.Count < n)
        {
            var len = 1 + (next() % 80);
            var shape = next() % 3;
            var k = next() % r;
            for (var i = 0; i < len && keys.Count < n; i++)
            {
                keys.Add((int)k);
                if (shape == 0)
                {
                    k = (k + (next() % 3)) % r;
                }
                else if (shape == 1)
                {
                    k = (k + r - (next() % 3)) % r;
                }
            }
        }

        return keys;
    }

    private static int Compare(int kind, uint seed, List<int> keys, int a, int b)
    {
        var r = Pair(seed, a, b);
        if (kind == 0)
        {
            return (int)(r % 3) - 1;
        }

        if (kind == 1 && r % 100 < 3)
        {
            return (int)((r >> 8) % 3) - 1;
        }

        if (keys[a] != keys[b])
        {
            return keys[a] - keys[b];
        }

        return kind == 3 && (Mix32(seed ^ (uint)a) & 1) != (Mix32(seed ^ (uint)b) & 1) ? -1 : 0;
    }

    private static uint Fnv(List<int> permutation)
    {
        var h = 0x811c9dc5u;
        foreach (var v in permutation)
        {
            for (var i = 0; i < 4; i++)
            {
                h = (h ^ (((uint)v >> (8 * i)) & 0xff)) * 0x01000193;
            }
        }

        return h;
    }
}
