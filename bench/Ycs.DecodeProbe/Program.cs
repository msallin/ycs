using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ycs;
using Ycs.Properties;

// Differential probe of the decoders (README.md). Public entry points only: the same source builds against an old tree
// and the current one, and everything it prints must be identical. Usage:
//   Ycs.DecodeProbe <repository root: the fixtures> [mode ...] [--verbose]
// modes: corpus hostile yjs fuzz-v2 fuzz-gen (default: all). --verbose prints every outcome, to diff the first difference.
var root = args[0];
var verbose = args.Contains("--verbose");
var modes = args.Skip(1).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToHashSet();
bool On(string mode) => modes.Count == 0 || modes.Contains(mode);

var probe = new Probe(verbose);
if (On("corpus")) probe.Corpus(Path.Combine(root, "interop", "corpus", "cases"));
if (On("hostile")) probe.Hostile(Path.Combine(root, "bench", "Ycs.Compare", "Hostile.cs"));
if (On("yjs")) probe.Yjs(Path.Combine(root, "bench", "Ycs.DecodeProbe", "yjs-cases.tsv"));
if (On("fuzz-v2")) probe.FuzzV2Columns();
if (On("fuzz-gen")) probe.FuzzGenerator();
probe.Summary();
return 0;

enum Kind { Update, Snapshot, StateVector, RelativePosition }

sealed class Probe(bool verbose)
{
    static readonly byte[] Overwrites = [0xFF, 0x00, 0x80, 0xC0, 0xED, 0xF0, 0x7F, 0x01];

    readonly SortedDictionary<string, long> _histogram = new(StringComparer.Ordinal);
    long _cases;
    long _inputs;

    // What one worker collects; merged in the order of the inputs, so the output does not depend on the schedule.
    sealed class Sink
    {
        public readonly Dictionary<string, long> Histogram = new();
        public readonly StringBuilder Text = new();
        public long Cases;
        public long Inputs = 1;
    }

    sealed record Work(string Name, Kind Kind, byte[] Input, byte[] Base, UpdateFormat Format, bool Mutate);

    readonly List<Work> _work = [];

    void Add(string name, Kind kind, byte[] input, UpdateFormat fmt, bool mutate = true, byte[] @base = null) =>
        _work.Add(new Work(name, kind, input, @base, fmt, mutate));

    // Runs the collected sweeps on all cores and prints their lines in the order they were added.
    void Flush()
    {
        var sinks = new Sink[_work.Count];
        Parallel.For(0, _work.Count, i =>
        {
            var w = _work[i];
            sinks[i] = new Sink();
            Sweep(sinks[i], w);
        });
        foreach (var sink in sinks)
        {
            Console.Write(sink.Text);
            Fold(sink);
        }

        _work.Clear();
    }

    void Fold(Sink sink)
    {
        _inputs += sink.Inputs;
        _cases += sink.Cases;
        foreach (var (k, v) in sink.Histogram)
        {
            _histogram[k] = _histogram.GetValueOrDefault(k) + v;
        }
    }

    sealed class Fnv
    {
        ulong _h = 14695981039346656037UL;

        public void Add(string s)
        {
            foreach (var c in s)
            {
                _h ^= c;
                _h *= 1099511628211UL;
            }

            _h ^= '\n';
            _h *= 1099511628211UL;
        }

        public override string ToString() => _h.ToString("X16");
    }

    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(SHA256.HashData(b))[..16] + ":" + b.Length;

    static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            sb.Append(c is >= ' ' and < (char)127 ? c.ToString() : $"\\u{(int)c:X4}");
        }

        return sb.ToString();
    }

    // The outcome of an operation: its result, or the exception's type, message and whether the document was left partly applied.
    static string Run(Func<string> f)
    {
        try
        {
            return "ok " + f();
        }
        catch (YUpdateException e)
        {
            return $"YUpdateException{(e.PartlyApplied ? "(partly)" : "")}: {Esc(e.Message)}";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {Esc(e.Message.Split('\n')[0])}";
        }
    }

    // The outcome without the hash of the document state that may follow it.
    static string Class(string outcome)
    {
        var c = Regex.Replace(outcome, @" [0-9A-F]{16}:\d+(/.*)?$", "");
        return c.StartsWith("ok", StringComparison.Ordinal) ? "ok" : c;
    }

    // Encoding the state can itself fail (pending structs beyond a limit): that is an outcome too.
    static string State(YDoc doc) =>
        Run(() => Hex(doc.EncodeStateAsUpdate()) + "/" + Hex(doc.EncodeStateAsUpdate(format: UpdateFormat.V2)) + "/" + Hex(doc.EncodeStateVector()));

    static string Show(object o) => o switch
    {
        null => "null",
        string t => "\"" + Esc(t) + "\"",
        byte[] b => Convert.ToHexString(b),
        System.Collections.IDictionary d => "{" + string.Join(",", d.Keys.Cast<object>().Select(k => k + ":" + Show(d[k]))) + "}",
        System.Collections.IEnumerable e => "[" + string.Join(",", e.Cast<object>().Select(Show)) + "]",
        _ => o.GetType().Name + ":" + Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture),
    };

    static string Dump(DecodedUpdate u)
    {
        var sb = new StringBuilder();
        foreach (var s in u.Structs)
        {
            sb.Append($"{s.Id} {s.Length} {s.Kind} {s.LeftOrigin} {s.RightOrigin} {Esc(s.ParentName ?? "")} {s.ParentId} {Esc(s.ParentSub ?? "")} {Show(s.Content)} {Esc(s.Text ?? "")} {Esc(s.FormatKey ?? "")} {Show(s.FormatValue)} {s.TypeRef} {s.TypeName} {s.DocGuid}\n");
        }

        foreach (var (client, ranges) in u.DeleteSet)
        {
            sb.Append($"ds {client} {string.Join(",", ranges.Select(r => r.Clock + "+" + r.Length))}\n");
        }

        return sb.ToString();
    }

    static string Meta(UpdateMeta m) =>
        string.Join(",", m.From.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value)) + "|" + string.Join(",", m.To.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));

    static byte[] VarUint(uint v)
    {
        var l = new List<byte>();
        while (v >= 0x80)
        {
            l.Add((byte)(v | 0x80));
            v >>= 7;
        }

        l.Add((byte)v);
        return [.. l];
    }

    static byte[] SyncMessage(uint type, byte[] payload) => [.. VarUint(type), .. VarUint((uint)payload.Length), .. payload];

    static UpdateFormat Other(UpdateFormat f) => f == UpdateFormat.V1 ? UpdateFormat.V2 : UpdateFormat.V1;

    static readonly UpdateLimits Tight = new() { MaxStructsPerUpdate = 3, MaxPendingStructs = 2, MaxPendingBytes = 64 };

    /// <summary>All outcomes of the entry points that decode <paramref name="input"/>; <paramref name="original"/> is the unmutated input.</summary>
    static IEnumerable<(string Op, string Outcome)> Ops(Kind kind, byte[] input, byte[] original, byte[] @base, UpdateFormat fmt)
    {
        switch (kind)
        {
            case Kind.Update:
                {
                    var doc = new YDoc();
                    var o = Run(() => { doc.ApplyUpdate(input.AsSpan(), fmt); return ""; });
                    yield return ("apply", o + " " + State(doc));
                    var doc2 = new YDoc();
                    yield return ("tryApply", Run(() => doc2.TryApplyUpdate(input.AsSpan(), fmt).ToString()) + " " + State(doc2));
                    var doc3 = new YDoc();
                    yield return ("applyTight", Run(() => { doc3.ApplyUpdate(input.AsSpan(), fmt, limits: Tight); return ""; }) + " " + State(doc3));
                    var doc4 = new YDoc();
                    Run(() => { doc4.ApplyUpdate(original.AsSpan(), fmt); return ""; });
                    yield return ("applyOver", Run(() => { doc4.ApplyUpdate(input.AsSpan(), fmt); return ""; }) + " " + State(doc4));
                    yield return ("decode", Run(() => Hex(Encoding.UTF8.GetBytes(Dump(YUpdate.Decode(input, fmt))))));
                    yield return ("describe", Run(() => Hex(Encoding.UTF8.GetBytes(YUpdate.Describe(input, fmt)))));
                    yield return ("meta", Run(() => Meta(YUpdate.ParseMeta(input, fmt))));
                    yield return ("stateVector", Run(() => Convert.ToHexString(YUpdate.EncodeStateVector(input, fmt))));
                    yield return ("convert", Run(() => Hex(YUpdate.Convert(input, fmt, Other(fmt)))));
                    yield return ("diffEmpty", Run(() => Hex(YUpdate.Diff(input, [0], fmt))));
                    yield return ("diffOriginal", Run(() => Hex(YUpdate.Diff(input, YUpdate.EncodeStateVector(original, fmt), fmt))));
                    yield return ("merge", Run(() => Hex(YUpdate.Merge(new ReadOnlySpan<ReadOnlyMemory<byte>>([input, original]), fmt))));
                    yield return ("mergeSelf", Run(() => Hex(YUpdate.Merge(new ReadOnlySpan<ReadOnlyMemory<byte>>([input]), fmt))));
                    yield return ("containsUpdate", Run(() =>
                    {
                        var d = new YDoc();
                        d.ApplyUpdate(original.AsSpan(), fmt);
                        return d.CreateSnapshot().ContainsUpdate(input, fmt).ToString();
                    }));
                    foreach (var type in new uint[] { 1, 2 })
                    {
                        var d = new YDoc();
                        var reply = new ArrayBufferWriter<byte>();
                        yield return ($"sync{type}", Run(() => Show(SyncProtocol.ReadSyncMessage(SyncMessage(type, input), reply, d, fmt))) + " " + State(d));
                    }

                    var d0 = new YDoc();
                    var reply0 = new ArrayBufferWriter<byte>();
                    yield return ("sync0", Run(() => Show(SyncProtocol.ReadSyncMessage(SyncMessage(0, input), reply0, d0, fmt))) + " " + Hex(reply0.WrittenSpan));
                    var d9 = new YDoc();
                    yield return ("syncRaw", Run(() => Show(SyncProtocol.ReadSyncMessage(input, new ArrayBufferWriter<byte>(), d9, fmt))) + " " + State(d9));
                    break;
                }

            case Kind.Snapshot:
                yield return ("snapshotDecode", Run(() => Hex(Snapshot.Decode(input, fmt).Encode(fmt))));
                yield return ("snapshotDecodeOther", Run(() => Hex(Snapshot.Decode(input, Other(fmt)).Encode(Other(fmt)))));
                yield return ("snapshotEqual", Run(() => Snapshot.Decode(input, fmt).Equals(Snapshot.Decode(original, fmt)).ToString()));
                break;

            case Kind.StateVector:
                {
                    var doc = new YDoc();
                    doc.ApplyUpdate(@base.AsSpan(), fmt);
                    yield return ("encodeStateAsUpdate", Run(() => Hex(doc.EncodeStateAsUpdate(input, fmt))));
                    yield return ("diffSv", Run(() => Hex(YUpdate.Diff(@base, input, fmt))));
                    var d = new YDoc();
                    var reply = new ArrayBufferWriter<byte>();
                    yield return ("sync0", Run(() => Show(SyncProtocol.ReadSyncMessage(SyncMessage(0, input), reply, d, fmt))) + " " + Hex(reply.WrittenSpan));
                    break;
                }

            case Kind.RelativePosition:
                yield return ("relpos", Run(() => RelativePosition.Decode(input).ToJsonString()));
                yield return ("relposRoundTrip", Run(() => Hex(RelativePosition.Decode(input).Encode())));
                break;
        }
    }

    // One input (a name, a kind and a format): its outcomes are folded into one line, and into the histogram of the whole run.
    static void Case(Sink sink, Fnv hash, string label, Kind kind, byte[] input, byte[] original, byte[] @base, UpdateFormat fmt, bool verbose)
    {
        foreach (var (op, outcome) in Ops(kind, input, original, @base, fmt))
        {
            var line = op + ": " + outcome;
            hash.Add(line);
            sink.Cases++;
            var key = $"{kind}/{fmt}/{op}: {Class(outcome)}";
            sink.Histogram[key] = sink.Histogram.GetValueOrDefault(key) + 1;
            if (verbose)
            {
                sink.Text.AppendLine($"  {label} {line}");
            }
        }
    }

    /// <summary>The input as it is, every prefix and every single-byte mutation (eight values) at every position.</summary>
    void Sweep(Sink sink, Work w)
    {
        var (name, kind, input, @base, fmt, mutate) = w;
        var hash = new Fnv();
        var count = 0;
        Case(sink, hash, name + " full", kind, input, input, @base, fmt, verbose);
        for (var len = 0; len < input.Length; len++)
        {
            Case(sink, hash, $"{name} prefix {len}", kind, input[..len], input, @base, fmt, verbose);
            count++;
        }

        if (mutate)
        {
            for (var i = 0; i < input.Length; i++)
            {
                foreach (var v in Overwrites)
                {
                    var m = (byte[])input.Clone();
                    m[i] = v == 0x01 ? (byte)(m[i] + 1) : v;
                    if (m[i] == input[i])
                    {
                        continue;
                    }

                    Case(sink, hash, $"{name} mutate {i}={m[i]:X2}", kind, m, input, @base, fmt, verbose);
                    count++;
                }
            }
        }

        sink.Text.Insert(0, $"{name} {kind}/{fmt} bytes={input.Length} variants={count} hash={hash}\n");
    }

    // Everything one valid update gives: the update itself, the snapshot and the state vector of the document it builds, a relative position into it.
    void Everything(string name, byte[] update, UpdateFormat fmt, bool mutate = true)
    {
        Add(name, Kind.Update, update, fmt, mutate);
        var doc = new YDoc();
        try
        {
            doc.ApplyUpdate(update.AsSpan(), fmt);
        }
        catch (YUpdateException)
        {
            return;
        }

        Add(name + " snapshot", Kind.Snapshot, doc.CreateSnapshot().Encode(fmt), fmt, mutate);
        Add(name + " state-vector", Kind.StateVector, doc.EncodeStateVector(), fmt, mutate, update);
        foreach (var root in new[] { "text", "t" })
        {
            YText text;
            try
            {
                text = doc.GetText(root);
            }
            catch (Exception)
            {
                continue;
            }


            foreach (var index in new[] { 0, text.Length / 2, text.Length })
            {
                Add($"{name} relpos:{root}@{index}", Kind.RelativePosition, RelativePosition.FromIndex(text, index).Encode(), fmt, mutate);
            }
        }
    }

    public void Corpus(string dir)
    {
        Console.WriteLine("## corpus (interop/corpus/cases): every update and the merged state, as v1 and v2");
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(x => x, StringComparer.Ordinal))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(f));
            var name = Path.GetFileNameWithoutExtension(f);
            var v1s = new List<byte[]>();
            var i = 0;
            foreach (var u in json.RootElement.GetProperty("updates").EnumerateArray())
            {
                var v1 = Convert.FromBase64String(u.GetProperty("v1").GetString());
                var v2 = Convert.FromBase64String(u.GetProperty("v2").GetString());
                v1s.Add(v1);
                Everything($"{name}[{i}]", v1, UpdateFormat.V1);
                Everything($"{name}[{i}]", v2, UpdateFormat.V2);
                i++;
            }

            var all = new YDoc();
            try
            {
                foreach (var u in v1s)
                {
                    all.ApplyUpdate(u.AsSpan());
                }
            }
            catch (YUpdateException)
            {
                continue;
            }

            Everything($"{name}[state]", all.EncodeStateAsUpdate(), UpdateFormat.V1);
            Everything($"{name}[state]", all.EncodeStateAsUpdate(format: UpdateFormat.V2), UpdateFormat.V2);
        }

        Flush();
    }

    public void Hostile(string source)
    {
        Console.WriteLine("## hostile fixtures (bench/Ycs.Compare/Hostile.cs, the pinned ones), swept like the corpus");
        foreach (Match m in Regex.Matches(File.ReadAllText(source), "Pinned\\(\"([^\"]+)\", (true|false), (true|false), \"([^\"]*)\", \"([^\"]*)\""))
        {
            var name = m.Groups[1].Value;
            var fmt = m.Groups[3].Value == "true" ? UpdateFormat.V2 : UpdateFormat.V1;
            var baseV1 = Convert.FromBase64String(m.Groups[4].Value);
            var update = Convert.FromBase64String(m.Groups[5].Value);
            var doc = new YDoc(new YDocOptions { GarbageCollection = m.Groups[2].Value == "true", ClientId = 1 });
            var outcome = Run(() =>
            {
                if (baseV1.Length > 0)
                {
                    doc.ApplyUpdate(baseV1.AsSpan(), limits: UpdateLimits.Unlimited);
                }

                doc.ApplyUpdate(update.AsSpan(), fmt);
                return "";
            });
            Console.WriteLine($"{name} pinned: {outcome} {State(doc)}");
            Add(name, Kind.Update, update, fmt);
            if (baseV1.Length > 0)
            {
                Add(name + " base", Kind.Update, baseV1, UpdateFormat.V1);
            }
        }

        Flush();
    }

    public void Yjs(string path)
    {
        Console.WriteLine("## updates written by Yjs 13.6.32 (yjs-cases.tsv)");
        foreach (var line in File.ReadAllLines(path))
        {
            var p = line.Split('\t');
            var fmt = p[1] == "v2" ? UpdateFormat.V2 : UpdateFormat.V1;
            Everything($"yjs:{p[0]}", Convert.FromBase64String(p[2]), fmt);
            var doc = new YDoc();
            var text = Run(() => { doc.ApplyUpdate(Convert.FromBase64String(p[2]).AsSpan(), fmt); return Esc(doc.GetText("t").ToString()); });
            Console.WriteLine($"yjs:{p[0]} {p[1]} text: {text}");
        }

        Flush();
    }

    // Documents whose text and keys hold lone surrogates, pairs, multi-byte characters and a BOM, in several items so that the v2
    // string column splits pairs; the update is then mutated at random positions (truncated, overwritten, a byte inserted or removed).
    public void FuzzV2Columns()
    {
        Console.WriteLine("## fuzzed v2 string columns: 20,000 documents, each valid and with random mutations");
        var rng = new Random(20261118);
        string[] alphabet = ["a", "z", " ", "é", "€", "﻿", "\U0001F600", "\U0001F601", "\uD83D", "\uDE00", "\uD83E", "\uDD14", "\uDFFF"];
        var sink = new Sink();
        var hash = new Fnv();
        for (var n = 0; n < 20_000; n++)
        {
            var doc = new YDoc(new YDocOptions { ClientId = n + 1 });
            var t = doc.GetText("t");
            var map = doc.GetMap("m");
            for (var k = rng.Next(1, 6); k > 0; k--)
            {
                var sb = new StringBuilder();
                for (var c = rng.Next(0, 7); c > 0; c--)
                {
                    sb.Append(alphabet[rng.Next(alphabet.Length)]);
                }

                var s = sb.ToString();
                if (rng.Next(5) == 0)
                {
                    map[s + "k"] = s;
                }
                else if (s.Length > 0)
                {
                    t.Insert(rng.Next(0, t.Length + 1), s);
                }
            }

            var fmt = n % 4 == 0 ? UpdateFormat.V1 : UpdateFormat.V2;
            var update = doc.EncodeStateAsUpdate(format: fmt);
            Case(sink, hash, $"fuzz{n} valid", Kind.Update, update, update, null, fmt, verbose);
            for (var m = 0; m < 4; m++)
            {
                var b = (byte[])update.Clone();
                switch (rng.Next(4))
                {
                    case 0:
                        b = b[..rng.Next(0, b.Length)];
                        break;
                    case 1:
                        for (var k = rng.Next(1, 4); k > 0; k--)
                        {
                            b[rng.Next(b.Length)] = (byte)rng.Next(256);
                        }

                        break;
                    case 2:
                        var at = rng.Next(b.Length + 1);
                        b = [.. b[..at], (byte)rng.Next(256), .. b[at..]];
                        break;
                    default:
                        var rm = rng.Next(b.Length);
                        b = [.. b[..rm], .. b[(rm + 1)..]];
                        break;
                }

                Case(sink, hash, $"fuzz{n} mutation {m}", Kind.Update, b, update, null, fmt, verbose);
            }

            sink.Inputs++;
        }

        Console.WriteLine($"fuzz-v2 hash={hash}");
        Fold(sink);
    }

    // The structure-aware generator of tests/Ycs.Properties (the one of bench/Ycs.Parity): every update, v1 and converted to v2, with random mutations.
    public void FuzzGenerator()
    {
        Console.WriteLine("## structure-aware fuzzer updates (tests/Ycs.Properties, seeds 1 to 400), v1 and v2, with random mutations");
        var rng = new Random(77);
        var sink = new Sink();
        var hash = new Fnv();
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var (_, updates) = UpdateFuzzer.Generate(seed);
            foreach (var u in updates)
            {
                var v1 = u.EncodeV1();
                byte[] v2;
                try
                {
                    v2 = YUpdate.Convert(v1, UpdateFormat.V1, UpdateFormat.V2);
                }
                catch (YUpdateException)
                {
                    v2 = null;
                }

                foreach (var (fmt, update) in new[] { (UpdateFormat.V1, v1), (UpdateFormat.V2, v2) })
                {
                    if (update == null)
                    {
                        continue;
                    }

                    Case(sink, hash, $"gen{seed} valid", Kind.Update, update, update, null, fmt, verbose);
                    for (var m = 0; m < 12 && update.Length > 0; m++)
                    {
                        var b = (byte[])update.Clone();
                        b[rng.Next(b.Length)] = (byte)rng.Next(256);
                        if (m % 4 == 3)
                        {
                            b = b[..rng.Next(0, b.Length)];
                        }

                        Case(sink, hash, $"gen{seed} mutation {m}", Kind.Update, b, update, null, fmt, verbose);
                    }

                    sink.Inputs++;
                }
            }
        }

        Console.WriteLine($"fuzz-gen hash={hash}");
        Fold(sink);
    }

    public void Summary()
    {
        Console.WriteLine($"## summary: {_inputs} inputs, {_cases} operation outcomes");
        foreach (var (k, v) in _histogram)
        {
            Console.WriteLine($"{v,10} {k}");
        }
    }
}
