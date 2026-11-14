using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ycs.Corpus;

internal sealed record Root(string Name, string Type, bool Delta);

internal sealed record Update(byte[] V1, byte[] V2)
{
    public byte[] Get(bool v2) => v2 ? V2 : V1;
}

/// <summary>One recorded case of interop/corpus/cases (see interop/corpus/gen.mjs for the schema).</summary>
internal sealed class CorpusCase
{
    public required string Name { get; init; }
    public required bool Gc { get; init; }
    public required IReadOnlyList<Root> Roots { get; init; }
    public required IReadOnlyList<Update> Updates { get; init; }
    public required JsonElement ExpectedContent { get; init; }
    public required IReadOnlyDictionary<long, long> ExpectedStateVector { get; init; }
    public required IReadOnlyDictionary<long, IReadOnlyList<(long Clock, long Length)>> ExpectedDeleteSet { get; init; }
    public required Update ExpectedState { get; init; }
    public required byte[] ExpectedStateVectorBytes { get; init; }
    public required int DiffPrefix { get; init; }
    public required byte[] DiffStateVector { get; init; }
    public required IReadOnlyDictionary<long, long> DiffStateVectorMap { get; init; }
    public required Update Diff { get; init; }
    public required IReadOnlyDictionary<long, long> DiffFrom { get; init; }
    public required IReadOnlyDictionary<long, long> DiffTo { get; init; }
    public required Update Merged { get; init; }

    public override string ToString() => Name;

    public static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "cases");

    private static readonly Lazy<IReadOnlyList<CorpusCase>> LazyAll = new(() =>
        System.IO.Directory.GetFiles(Directory, "*.json").Order(StringComparer.Ordinal).Select(Load).ToList());

    public static IReadOnlyList<CorpusCase> All => LazyAll.Value;

    public static CorpusCase Named(string name) => All.Single(c => c.Name == name);

    /// <summary>MSTest data rows: one per case, by name (so the test list shows the case).</summary>
    public static IEnumerable<object[]> Names => All.Select(c => new object[] { c.Name });

    private static CorpusCase Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var r = json.RootElement.Clone();
        var expected = r.GetProperty("expected");
        var diff = r.GetProperty("diff");
        var merged = r.GetProperty("merged");
        return new CorpusCase
        {
            Name = r.GetProperty("name").GetString(),
            Gc = r.GetProperty("gc").GetBoolean(),
            Roots = r.GetProperty("roots").EnumerateArray()
                .Select(e => new Root(e.GetProperty("name").GetString(), e.GetProperty("type").GetString(), e.GetProperty("delta").GetBoolean()))
                .ToList(),
            Updates = r.GetProperty("updates").EnumerateArray().Select(ReadUpdate).ToList(),
            ExpectedContent = expected.GetProperty("content"),
            ExpectedStateVector = ReadClocks(expected.GetProperty("stateVector")),
            ExpectedDeleteSet = expected.GetProperty("deleteSet").EnumerateObject().ToDictionary(
                p => long.Parse(p.Name, CultureInfo.InvariantCulture),
                p => (IReadOnlyList<(long, long)>)p.Value.EnumerateArray().Select(a => (a[0].GetInt64(), a[1].GetInt64())).ToList()),
            ExpectedState = new Update(Bytes(expected, "stateV1"), Bytes(expected, "stateV2")),
            ExpectedStateVectorBytes = Bytes(expected, "stateVectorV1"),
            DiffPrefix = diff.GetProperty("prefix").GetInt32(),
            DiffStateVector = Bytes(diff, "stateVector"),
            DiffStateVectorMap = ReadClocks(diff.GetProperty("stateVectorMap")),
            Diff = new Update(Bytes(diff, "updateV1"), Bytes(diff, "updateV2")),
            DiffFrom = ReadClocks(diff.GetProperty("from")),
            DiffTo = ReadClocks(diff.GetProperty("to")),
            Merged = new Update(Bytes(merged, "v1"), Bytes(merged, "v2")),
        };
    }

    private static Update ReadUpdate(JsonElement e) => new(Bytes(e, "v1"), Bytes(e, "v2"));

    private static byte[] Bytes(JsonElement e, string property) => Convert.FromBase64String(e.GetProperty(property).GetString());

    private static IReadOnlyDictionary<long, long> ReadClocks(JsonElement e) =>
        e.EnumerateObject().ToDictionary(p => long.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetInt64());
}

/// <summary>What a document looks like, in a form that compares as a string.</summary>
internal static class Observed
{
    /// <summary>Every root's content (text, delta, JSON) as canonical JSON, the shape gen.mjs records.</summary>
    public static string Content(CorpusDoc doc, IReadOnlyList<Root> roots)
    {
        var content = new Dictionary<string, object>();
        foreach (var root in roots)
        {
            var entry = new Dictionary<string, object>();
            if (root.Type == "text")
            {
                entry["text"] = doc.Text(root.Name);
                if (root.Delta)
                {
                    entry["delta"] = doc.Delta(root.Name);
                }
            }
            else
            {
                entry["json"] = doc.Json(root.Name, root.Type);
            }

            content[root.Name] = entry;
        }

        return CanonicalJson.Write(content);
    }

    public static string Expected(CorpusCase c) => CanonicalJson.Write(CanonicalJson.FromJson(c.ExpectedContent));

    public static string StateVector(byte[] encoded) => Clocks(Lib0.DecodeStateVector(encoded));

    public static string Clocks(IReadOnlyDictionary<long, long> clocks) =>
        string.Join(", ", clocks.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}:{kvp.Value}"));

    /// <summary>A delete set as sorted, merged ranges: "client:[clock+len ...]; ...".</summary>
    public static string DeleteSet(IReadOnlyDictionary<long, IReadOnlyList<(long Clock, long Length)>> ds)
    {
        var parts = new List<string>();
        foreach (var (client, ranges) in ds.OrderBy(kvp => kvp.Key))
        {
            var merged = new List<(long Clock, long Length)>();
            foreach (var (clock, length) in ranges.OrderBy(r => r.Clock))
            {
                if (merged.Count > 0 && merged[^1].Clock + merged[^1].Length >= clock)
                {
                    var last = merged[^1];
                    merged[^1] = (last.Clock, Math.Max(last.Length, clock + length - last.Clock));
                }
                else if (length > 0)
                {
                    merged.Add((clock, length));
                }
            }

            if (merged.Count > 0)
            {
                parts.Add($"{client}:[{string.Join(" ", merged.Select(r => $"{r.Clock}+{r.Length}"))}]");
            }
        }

        return string.Join("; ", parts);
    }
}

/// <summary>Canonical JSON: sorted keys, bytes as {"$bytes": base64}, integral numbers without a fraction.</summary>
internal static class CanonicalJson
{
    public static object FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object when e.TryGetProperty("$bytes", out var b) && e.EnumerateObject().Count() == 1 => Convert.FromBase64String(b.GetString()),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    public static string Write(object value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d when d == Math.Floor(d) && Math.Abs(d) < 1e15:
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case byte[] bytes:
                sb.Append("{\"$bytes\":");
                WriteString(sb, Convert.ToBase64String(bytes));
                sb.Append('}');
                break;
            case IDictionary<string, object> dict:
                sb.Append('{');
                var first = true;
                foreach (var key in dict.Keys.Order(StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }

                    first = false;
                    WriteString(sb, key);
                    sb.Append(':');
                    Write(sb, dict[key]);
                }

                sb.Append('}');
                break;
            case IEnumerable<object> list:
                sb.Append('[');
                sb.AppendJoin(',', list.Select(Write));
                sb.Append(']');
                break;
            default:
                throw new NotSupportedException($"Not a plain value: {value.GetType()}.");
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case < ' ': sb.Append($"\\u{(int)ch:x4}"); break;
                default: sb.Append(ch); break;
            }
        }

        sb.Append('"');
    }
}

/// <summary>The lib0 state-vector encoding: varuint count, then (client, clock) varuint pairs.</summary>
internal static class Lib0
{
    public static IReadOnlyDictionary<long, long> DecodeStateVector(byte[] encoded)
    {
        var pos = 0;
        var count = ReadVarUint(encoded, ref pos);
        var result = new Dictionary<long, long>();
        for (ulong i = 0; i < count; i++)
        {
            var client = (long)ReadVarUint(encoded, ref pos);
            result[client] = (long)ReadVarUint(encoded, ref pos);
        }

        return result;
    }

    private static ulong ReadVarUint(byte[] buf, ref int pos)
    {
        ulong value = 0;
        for (var shift = 0; ; shift += 7)
        {
            var b = buf[pos++];
            value |= (ulong)(b & 0x7f) << shift;
            if (b < 0x80)
            {
                return value;
            }
        }
    }
}
