using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Ycs;

/// <summary>
/// One run of a scenario that logs what a user, a peer or a handler could see, in one of two modes: the candidate
/// (Ycs as it is: runs extended in place, the one-pass merge) or the reference (every insert an item of its own,
/// merged pair by pair, as Yjs; <see cref="YDoc.RunExtensionsDisabledForTests"/>, <see cref="StructStore.PairwiseMergeForTests"/>).
/// The two logs of a scenario must be equal (<see cref="RunExtensionScenarioTests"/>, <see cref="RunExtensionFuzzTests"/>).
/// </summary>
internal sealed class ReferenceRun
{
    private readonly Dictionary<YDoc, long> _ids = new();
    private long _nextId = 1000;

    public ReferenceRun(bool reference)
    {
        Reference = reference;
    }

    public bool Reference { get; }

    public StringBuilder Log { get; } = new();

    /// <summary>Runs <paramref name="scenario"/> in both modes; null when the logs agree, else where they first differ.</summary>
    public static string FirstDifference(Action<ReferenceRun> scenario)
    {
        var candidate = RunOne(scenario, reference: false).Split('\n');
        var reference = RunOne(scenario, reference: true).Split('\n');
        for (var i = 0; i < Math.Max(candidate.Length, reference.Length); i++)
        {
            var a = i < candidate.Length ? candidate[i] : "<end>";
            var b = i < reference.Length ? reference[i] : "<end>";
            if (a != b)
            {
                return $"line {i}:{Environment.NewLine}  candidate: {a}{Environment.NewLine}  reference: {b}";
            }
        }

        return null;
    }

    public static string RunOne(Action<ReferenceRun> scenario, bool reference)
    {
        var run = new ReferenceRun(reference);
        try
        {
            scenario(run);
        }
        catch (Exception e)
        {
            run.L("scenario threw " + e.GetType().Name + ": " + e.Message.Split('\n')[0]);
        }

        return run.Log.ToString();
    }

    public YDoc NewDoc(long client, bool gc = true, Predicate<ItemView> gcFilter = null)
    {
        var doc = new YDoc(new YDocOptions
        {
            ClientId = client,
            GarbageCollection = gc,
            GarbageCollectionFilter = gcFilter == null ? null : item => gcFilter(item),
        });
        if (Reference)
        {
            doc.RunExtensionsDisabledForTests = true;
            doc.Store.PairwiseMergeForTests = true;
        }

        _ids[doc] = client;
        return doc;
    }

    /// <summary>A client id the document drew at random (a non-local transaction changed its own state) is replaced by the same one in both modes.</summary>
    public void FixClientId(YDoc doc)
    {
        if (_ids[doc] != doc.ClientId)
        {
            doc.ClientId = _nextId;
            _ids[doc] = _nextId++;
        }
    }

    public void L(string line) => Log.Append(line).Append('\n');

    public static string Hex(byte[] bytes) => bytes.Length <= 48 ? Convert.ToHexString(bytes) : bytes.Length + ":" + Convert.ToHexString(SHA1.HashData(bytes), 0, 8);

    public static string D(IEnumerable<Delta> delta)
    {
        var sb = new StringBuilder("[");
        foreach (var d in delta)
        {
            switch (d)
            {
                case Delta.Insert insert:
                    sb.Append("ins(").Append(V(insert.Value.ToObject())).Append(')');
                    break;
                case Delta.Retain retain:
                    sb.Append("ret(").Append(retain.Length).Append(')');
                    break;
                case Delta.Delete delete:
                    sb.Append("del(").Append(delete.Length).Append(')');
                    break;
            }

            if (d.Attributes != null)
            {
                sb.Append('{').Append(string.Join(",", d.Attributes.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + Attribute(k.Key, k.Value)))).Append('}');
            }

            sb.Append(' ');
        }

        return sb.Append(']').ToString();
    }

    private static string Attribute(string key, YValue value) =>
        key == "ychange" && value.Kind == YValueKind.Object
            ? "ychange(" + (value["type"] == "added" ? "Added" : "Removed") + "," + (value.TryGetMember("user", out var user) ? V(user.ToObject()) : "") + ")"
            : V(value.ToObject());

    public static string V(object v) => v switch
    {
        null => "null",
        string s => "\"" + Esc(s) + "\"",
        YText t => "YText(" + Esc(t.ToString()) + ")",
        YArray a => "YArray(" + string.Join(",", a.ToArray().Select(V)) + ")",
        YMap => "YMap",
        System.Collections.IEnumerable e => "[" + string.Join(",", e.Cast<object>().Select(V)) + "]",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };

    public static string Esc(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s)
        {
            if (ch < 0x20 || ch > 0x7e)
            {
                sb.Append("\\u").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    public void State(YDoc doc, string label) =>
        L($"{label} v1={Hex(doc.EncodeStateAsUpdate())} v2={Hex(doc.EncodeStateAsUpdate(format: UpdateFormat.V2))} sv={Hex(doc.EncodeStateVector())}");

    /// <summary>
    /// The structs of every client (kind, clock, length, deletion, content, keep, origins, marker flag), then the
    /// search markers of the given lists. Between transactions the two modes have the same structs, and the same
    /// markers: a marker sits where Yjs' would.
    /// </summary>
    public static string Dump(YDoc doc, params YArrayBase[] lists)
    {
        var sb = new StringBuilder();
        foreach (var (client, structs) in doc.Store.Clients.OrderBy(kv => kv.Key))
        {
            sb.Append("  c").Append(client).Append(':');
            foreach (var s in structs)
            {
                sb.Append(' ').Append(s.IsItem ? "I" : StructStore.KindNameForTests(s)).Append(s.Id.Clock).Append('+').Append(s.Length);
                if (s.Deleted)
                {
                    sb.Append('d');
                }

                if (s.IsItem)
                {
                    sb.Append(s.Content is ContentString cs ? "\"" + Esc(cs.GetString()) + "\"" : StructStore.ContentKindNameForTests(s.Content).Replace("Content", "", StringComparison.Ordinal));
                    if (s.Keep)
                    {
                        sb.Append('k');
                    }

                    if (s.Marker)
                    {
                        sb.Append('m');
                    }

                    sb.Append('<').Append(s.LeftOrigin is ItemId lo ? lo.Client + ":" + lo.Clock : "-").Append('|').Append(s.RightOrigin is ItemId ro ? ro.Client + ":" + ro.Clock : "-").Append('>');
                }
            }

            sb.Append('\n');
        }

        foreach (var list in lists)
        {
            sb.Append("  markers ").Append(list.DescribeSearchMarkers()).Append('\n');
        }

        return sb.ToString();
    }

    public void LogUpdates(YDoc doc, string label, List<byte[]> sink = null)
    {
        doc.Updated += (_, e) =>
        {
            L($"{label} update {Hex(e.Update.ToArray())} local={e.Transaction.Local}");
            sink?.Add(e.Update.ToArray());
        };
        doc.UpdatedV2 += (_, e) => L($"{label} updateV2 {Hex(e.Update.ToArray())}");
    }

    public static string Items(IEnumerable<ItemView> items) =>
        string.Join(",", items.Select(i => i.Id.Client + ":" + i.Id.Clock + "+" + i.Length).OrderBy(x => x, StringComparer.Ordinal));

    public Action<YTextEvent> TextObserver(string label) => ev =>
    {
        L($"{label} delta={D(ev.Delta)} added={Items(ev.Changes.Added)} deleted={Items(ev.Changes.Deleted)}");
    };

    public Action<IReadOnlyList<YEvent>, Transaction> DeepObserver(string label) => (events, _) =>
    {
        foreach (var ev in events)
        {
            L($"{label} deep path=[{string.Join("/", ev.Path)}] delta={D(ev.Delta)} keys={string.Join(",", ev.Keys.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        }
    };
}
