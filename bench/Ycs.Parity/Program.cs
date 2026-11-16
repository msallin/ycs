using System.Security.Cryptography;
using System.Text.Json;
using Ycs;
using Ycs.Properties;

// Usage: Ycs.Parity <output file> <fuzzer seeds> <corpus cases directory>
var outPath = args[0];
var seeds = int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
var lines = new List<string>();

// Rejecting an input is a YUpdateException; any other exception type (an InvalidOperationException from the cleanup, say)
// is a bug, listed with the prefix UNEXPECTED and counted.
var unexpected = 0;
var current = "";

void Note(Exception e)
{
    if (e is not YUpdateException)
    {
        unexpected++;
        lines.Add($"UNEXPECTED {current} {e.GetType().Name}");
    }
}

string H(Func<byte[]> f)
{
    try
    {
        return Convert.ToHexString(SHA256.HashData(f()));
    }
    catch (Exception e)
    {
        Note(e);
        return "EX:" + e.GetType().Name;
    }
}

string Show(object o) => o switch
{
    null => "null",
    string t => "\"" + t + "\"",
    byte[] b => Convert.ToHexString(b),
    System.Collections.IDictionary d => "{" + string.Join(",", d.Keys.Cast<object>().Select(k => k + ":" + Show(d[k]))) + "}",
    System.Collections.IEnumerable e => "[" + string.Join(",", e.Cast<object>().Select(Show)) + "]",
    _ => o.GetType().Name + ":" + Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture),
};

string Dump(DecodedUpdate u)
{
    var sb = new System.Text.StringBuilder();
    foreach (var s in u.Structs)
    {
        sb.Append($"{s.Id} {s.Length} {s.Kind} {s.LeftOrigin} {s.RightOrigin} {s.ParentName} {s.ParentId} {s.ParentSub} {Show(s.Content)} {s.Text} {s.FormatKey} {Show(s.FormatValue)} {s.TypeRef} {s.TypeName} {s.DocGuid}\n");
    }

    foreach (var (client, ranges) in u.DeleteSet)
    {
        sb.Append($"ds {client} {string.Join(",", ranges.Select(r => r.Clock + "+" + r.Length))}\n");
    }

    return sb.ToString();
}

string Meta(UpdateMeta m) => string.Join(",", m.From.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value)) + "|" + string.Join(",", m.To.OrderBy(p => p.Key).Select(p => p.Key + ":" + p.Value));

string HS(Func<string> f)
{
    try
    {
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(f())));
    }
    catch (Exception e)
    {
        Note(e);
        return "EX:" + e.GetType().Name;
    }
}

void Do(string label, IList<byte[]> v1)
{
    current = label;
    var v2 = v1.Select(u => { try { return UpdateOperations.ConvertUpdateFormatV1ToV2(u); } catch (Exception e) { Note(e); return null; } }).ToList();
    for (var i = 0; i < v1.Count; i++)
    {
        var u = v1[i];
        lines.Add($"{label} conv12[{i}] {H(() => UpdateOperations.ConvertUpdateFormatV1ToV2(u))}");
        if (v2[i] != null)
        {
            lines.Add($"{label} conv21[{i}] {H(() => UpdateOperations.ConvertUpdateFormatV2ToV1(v2[i]))}");
        }
    }

    for (var i = 0; i < v1.Count; i++)
    {
        var u = v1[i];
        lines.Add($"{label} describe1[{i}] {HS(() => UpdateOperations.DescribeUpdate(u))}");
        lines.Add($"{label} decode1[{i}] {HS(() => Dump(UpdateOperations.DecodeUpdate(u)))}");
        lines.Add($"{label} meta1[{i}] {HS(() => Meta(UpdateOperations.ParseUpdateMeta(u)))}");
        lines.Add($"{label} sv1[{i}] {H(() => UpdateOperations.EncodeStateVectorFromUpdate(u))}");
        if (v2[i] != null)
        {
            var u2 = v2[i];
            lines.Add($"{label} describe2[{i}] {HS(() => UpdateOperations.DescribeUpdateV2(u2))}");
            lines.Add($"{label} decode2[{i}] {HS(() => Dump(UpdateOperations.DecodeUpdateV2(u2)))}");
            lines.Add($"{label} meta2[{i}] {HS(() => Meta(UpdateOperations.ParseUpdateMetaV2(u2)))}");
            lines.Add($"{label} sv2[{i}] {H(() => UpdateOperations.EncodeStateVectorFromUpdateV2(u2))}");
        }
    }

    lines.Add($"{label} merge {H(() => UpdateOperations.MergeUpdates(v1))}");
    var rev = v1.Reverse().ToList();
    lines.Add($"{label} mergeRev {H(() => UpdateOperations.MergeUpdates(rev))}");
    if (v2.All(x => x != null))
    {
        lines.Add($"{label} merge2 {H(() => UpdateOperations.MergeUpdatesV2(v2))}");
        var r2 = v2.AsEnumerable().Reverse().ToList();
        lines.Add($"{label} merge2Rev {H(() => UpdateOperations.MergeUpdatesV2(r2))}");
    }

    for (var i = 0; i + 1 < v1.Count; i++)
    {
        var pair = new List<byte[]> { v1[i], v1[i + 1] };
        lines.Add($"{label} mergePair[{i}] {H(() => UpdateOperations.MergeUpdates(pair))}");
    }

    byte[] merged = null;
    try { merged = UpdateOperations.MergeUpdates(v1); } catch (Exception e) { Note(e); }

    // State vectors of every prefix, applied in order and reversed (the reversed ones leave structs pending).
    var svs = new List<byte[]> { new byte[] { 0 } };
    foreach (var order in new[] { v1.ToList(), rev })
    {
        for (var k = 1; k <= order.Count; k++)
        {
            var d = new YDoc();
            try { foreach (var u in order.Take(k)) { d.ApplyUpdate(u); } } catch (Exception e) { Note(e); continue; }
            svs.Add(d.EncodeStateVector());
        }
    }

    for (var s = 0; s < svs.Count; s++)
    {
        var sv = svs[s];
        if (merged != null)
        {
            lines.Add($"{label} diff[{s}] {H(() => UpdateOperations.DiffUpdate(merged, sv))}");
            byte[] m2;
            try { m2 = UpdateOperations.ConvertUpdateFormatV1ToV2(merged); } catch (Exception e) { Note(e); continue; }
            lines.Add($"{label} diff2[{s}] {H(() => UpdateOperations.DiffUpdateV2(m2, sv))}");
        }

        for (var i = 0; i < v1.Count; i++)
        {
            var u = v1[i];
            lines.Add($"{label} diffU[{s},{i}] {H(() => UpdateOperations.DiffUpdate(u, sv))}");
        }
    }

    // The rest store: structs that wait for what they refer to, encoded.
    var orders = new List<List<byte[]>> { v1.ToList(), rev };
    if (v1.Count > 2)
    {
        orders.Add(v1.Where((_, i) => i % 2 == 0).ToList());
        orders.Add(v1.Where((_, i) => i % 2 == 1).ToList());
        orders.Add(v1.Skip(1).ToList());
    }

    for (var o = 0; o < orders.Count; o++)
    {
        var d = new YDoc();
        try { foreach (var u in orders[o]) { d.ApplyUpdate(u); } } catch (Exception e) { Note(e); lines.Add($"{label} rest[{o}] applyEX"); continue; }
        lines.Add($"{label} rest[{o}] v1 {H(() => d.EncodeStateAsUpdateV1())}");
        lines.Add($"{label} rest[{o}] v2 {H(() => d.EncodeStateAsUpdateV2())}");
        foreach (var (x, i) in svs.Take(6).Select((x, i) => (x, i)))
        {
            lines.Add($"{label} rest[{o}] v1sv{i} {H(() => d.EncodeStateAsUpdateV1(x))}");
            lines.Add($"{label} rest[{o}] v2sv{i} {H(() => d.EncodeStateAsUpdateV2(x))}");
        }

        var d2 = new YDoc();
        try
        {
            foreach (var u in orders[o]) { d2.ApplyUpdateV2(UpdateOperations.ConvertUpdateFormatV1ToV2(u)); }
            lines.Add($"{label} rest[{o}] via2 {H(() => d2.EncodeStateAsUpdateV1())}");
        }
        catch (Exception e)
        {
            Note(e);
        }
    }
}

for (ulong seed = 1; seed <= (ulong)seeds; seed++)
{
    var (_, ups) = UpdateFuzzer.Generate(seed);
    Do($"fuzz{seed}", ups.Select(u => u.EncodeV1()).ToList());
}

var corpus = 0;
foreach (var f in Directory.GetFiles(args[2], "*.json").OrderBy(x => x, StringComparer.Ordinal))
{
    using var doc = JsonDocument.Parse(File.ReadAllText(f));
    var ups = doc.RootElement.GetProperty("updates").EnumerateArray().Select(e => Convert.FromBase64String(e.GetProperty("v1").GetString())).ToList();
    Do("corpus:" + Path.GetFileName(f), ups);
    corpus++;
}

File.WriteAllLines(outPath, lines);
Console.WriteLine($"corpus cases {corpus}, fuzzer seeds {seeds}, lines {lines.Count}, unexpected exceptions {unexpected}");
return unexpected == 0 ? 0 : 1;
