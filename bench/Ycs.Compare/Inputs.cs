using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Ycs.Compare;

/// <summary>SplitMix64: a fixed algorithm, so the inputs do not depend on the runtime's <see cref="Random"/>.</summary>
internal sealed class Rng(ulong seed)
{
    private ulong _state = seed;

    public ulong Next()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A value in [0, bound).</summary>
    public int Below(int bound) => (int)(Next() % (ulong)bound);
}

/// <summary>A text edit: delete <see cref="Delete"/> characters at <see cref="Pos"/>, then insert <see cref="Insert"/> there.</summary>
internal readonly record struct Edit(int Pos, int Delete, string Insert);

/// <summary>A replayed editing trace: <see cref="Edits"/>, grouped into transactions by <see cref="TxnStarts"/>.</summary>
internal sealed record Trace(Edit[] Edits, int[] TxnStarts, string EndContent)
{
    public int Transactions => TxnStarts.Length - 1;
}

/// <summary>
/// Every input of every workload, generated from <see cref="Seed"/> by the same code in every process, so both
/// libraries get identical inputs; <see cref="Hash"/> fingerprints them and is recorded with each result.
/// </summary>
internal static class Inputs
{
    public const ulong Seed = 20261115;

    public const int TypingN = 100_000;
    public const int RandomTypingN = 100_000;
    public const int MapN = 100_000;
    public const int ArrayN = 100_000;
    public const int ArrayInsertN = 50_000;
    public const int ArrayDeleteN = 50_000;
    public const int UndoN = 20_000;
    public const int Clients = 20;
    public const int EditsPerClient = 250;
    public const int BaseLength = 1_000;

    /// <summary>The client id every single-writer workload uses on both libraries.</summary>
    public const long ClientId = 123456789;

    private static readonly string[] Letters = [.. Enumerable.Range('a', 26).Select(c => ((char)c).ToString())];

    private static readonly Lazy<string[]> LazyTyping = new(() =>
    {
        var rng = new Rng(Seed + 1);
        return [.. Enumerable.Range(0, TypingN).Select(_ => Letters[rng.Below(26)])];
    });

    private static readonly Lazy<Edit[]> LazyRandomTyping = new(() =>
    {
        var rng = new Rng(Seed + 2);
        var edits = new Edit[RandomTypingN];
        for (var i = 0; i < edits.Length; i++)
        {
            edits[i] = new Edit(rng.Below(i + 1), 0, Letters[rng.Below(26)]);
        }

        return edits;
    });

    private static readonly Lazy<string[]> LazyKeys = new(() => [.. Enumerable.Range(0, MapN).Select(i => "key" + i)]);

    private static readonly Lazy<int[]> LazyArrayInserts = new(() =>
    {
        var rng = new Rng(Seed + 3);
        return [.. Enumerable.Range(0, ArrayInsertN).Select(i => rng.Below(i + 1))];
    });

    private static readonly Lazy<int[]> LazyArrayDeletes = new(() =>
    {
        var rng = new Rng(Seed + 4);
        return [.. Enumerable.Range(0, ArrayDeleteN).Select(i => rng.Below(ArrayN - i))];
    });

    private static readonly Lazy<Edit[][]> LazyClientEdits = new(() =>
    {
        var rng = new Rng(Seed + 5);
        var all = new Edit[Clients][];
        for (var c = 0; c < Clients; c++)
        {
            var length = BaseLength;
            var edits = new Edit[EditsPerClient];
            for (var e = 0; e < edits.Length; e++)
            {
                if (rng.Below(5) == 0 && length > 10)
                {
                    var del = 1 + rng.Below(5);
                    edits[e] = new Edit(rng.Below(length - del + 1), del, string.Empty);
                    length -= del;
                }
                else
                {
                    var run = 1 + rng.Below(10);
                    var sb = new StringBuilder(run);
                    for (var k = 0; k < run; k++)
                    {
                        sb.Append(Letters[rng.Below(26)]);
                    }

                    edits[e] = new Edit(rng.Below(length + 1), 0, sb.ToString());
                    length += run;
                }
            }

            all[c] = edits;
        }

        return all;
    });

    /// <summary>One single-character string per keystroke of the typing workloads.</summary>
    public static string[] Typing => LazyTyping.Value;

    /// <summary>A single-character insert at a random position of the growing text, per keystroke.</summary>
    public static Edit[] RandomTyping => LazyRandomTyping.Value;

    public static string[] Keys => LazyKeys.Value;

    /// <summary>Insert positions in the growing array.</summary>
    public static int[] ArrayInserts => LazyArrayInserts.Value;

    /// <summary>Delete positions in the shrinking array of <see cref="ArrayN"/> values.</summary>
    public static int[] ArrayDeletes => LazyArrayDeletes.Value;

    /// <summary>The offline edits of each client of the concurrent workload, against its own copy of the base text.</summary>
    public static Edit[][] ClientEdits => LazyClientEdits.Value;

    /// <summary>The text every client of the concurrent workload starts from.</summary>
    public static string BaseText => string.Concat(Typing.Take(BaseLength));

    public static string Hash()
    {
        var sb = new StringBuilder();
        sb.Append(string.Concat(Typing));
        foreach (var e in RandomTyping)
        {
            sb.Append(e.Pos).Append(e.Insert);
        }

        sb.AppendJoin(',', ArrayInserts).AppendJoin(',', ArrayDeletes);
        foreach (var c in ClientEdits)
        {
            foreach (var e in c)
            {
                sb.Append(e.Pos).Append(',').Append(e.Delete).Append(e.Insert);
            }
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static Trace LoadTrace(string path)
    {
        using var file = File.OpenRead(path);
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var json = JsonDocument.Parse(gz);
        var edits = new List<Edit>();
        var starts = new List<int>();
        foreach (var txn in json.RootElement.GetProperty("txns").EnumerateArray())
        {
            starts.Add(edits.Count);
            foreach (var p in txn.GetProperty("patches").EnumerateArray())
            {
                edits.Add(new Edit(p[0].GetInt32(), p[1].GetInt32(), p[2].GetString()));
            }
        }

        starts.Add(edits.Count);
        return new Trace([.. edits], [.. starts], json.RootElement.GetProperty("endContent").GetString());
    }

    /// <summary>Length-prefixed updates, as the prepare step writes them.</summary>
    public static void WriteUpdates(string path, IReadOnlyList<byte[]> updates)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(updates.Count);
        foreach (var u in updates)
        {
            w.Write(u.Length);
            w.Write(u);
        }
    }

    public static byte[][] ReadUpdates(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        var result = new byte[r.ReadInt32()][];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = r.ReadBytes(r.ReadInt32());
        }

        return result;
    }

    /// <summary>The expected text after applying <paramref name="edits"/> to <paramref name="start"/>.</summary>
    public static string Replay(string start, IEnumerable<Edit> edits)
    {
        // A gap-free List<char> is quadratic but only runs once per suite, outside any measurement.
        var chars = new List<char>(start);
        foreach (var e in edits)
        {
            chars.RemoveRange(e.Pos, e.Delete);
            chars.InsertRange(e.Pos, e.Insert);
        }

        return new string([.. chars]);
    }
}
