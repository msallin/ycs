// The ONLY file in this project that touches the Ycs API. Everything else speaks bytes and
// plain CLR values (Dictionary<string, object>, List<object>, string, long, double, bool, null,
// byte[]), so when the library's API changes, this is the file to edit.

using System.Collections;

namespace Ycs.Corpus;

/// <summary>
/// One struct of a sequence's item list as the library holds it: a run of <see cref="Length"/>
/// elements with ids (Client, Clock) .. (Client, Clock + Length - 1). Origins are ids, null for
/// the start / end of the sequence. <see cref="Kind"/> is "Item" for an Item, else the struct type.
/// </summary>
internal readonly record struct ItemRun(
    long Client,
    long Clock,
    int Length,
    (long Client, long Clock)? Origin,
    (long Client, long Clock)? RightOrigin,
    bool Deleted,
    string Kind,
    bool LinkedBack,
    bool InParent)
{
    public const string ItemKind = "Item";
}

/// <summary>A document under test.</summary>
internal sealed class CorpusDoc
{
    private static readonly object Remote = new();
    private readonly YDoc _doc;

    private CorpusDoc(YDoc doc) => _doc = doc;

    /// <summary>A fresh document; <paramref name="clientId"/> pins the id for reproducible conflict resolution.</summary>
    public static CorpusDoc Create(bool gc = true, int? clientId = null)
    {
        return new CorpusDoc(new YDoc(new YDocOptions { GarbageCollection = gc, ClientId = clientId }));
    }

    /// <summary>Raised with the v1 update of every transaction made on this doc (not of applied remote updates).</summary>
    public event Action<byte[]> LocalUpdateV1
    {
        add => _doc.Updated += Forward(value);
        remove => throw new NotSupportedException();
    }

    private static EventHandler<UpdateEventArgs> Forward(Action<byte[]> handler) =>
        (_, e) =>
        {
            if (!ReferenceEquals(e.Origin, Remote))
            {
                handler(e.Update.ToArray());
            }
        };

    public void ApplyV1(byte[] update) => _doc.ApplyUpdate(update, Remote);

    public void ApplyV2(byte[] update) => _doc.ApplyUpdate(update, Remote, UpdateFormat.V2);

    public byte[] EncodeStateV1(byte[] stateVector = null) => _doc.EncodeStateAsUpdate(stateVector);

    public byte[] EncodeStateV2(byte[] stateVector = null) => _doc.EncodeStateAsUpdate(stateVector, format: UpdateFormat.V2);

    /// <summary>The state vector in the (format-independent) lib0 encoding Yjs' encodeStateVector uses.</summary>
    public byte[] EncodeStateVector() => _doc.EncodeStateVector();

    public string Text(string root) => _doc.GetText(root).ToString();

    public int TextLength(string root) => _doc.GetText(root).Length;

    public void InsertText(string root, int index, string text) => _doc.GetText(root).Insert(index, text);

    public void DeleteText(string root, int index, int length) => _doc.GetText(root).Remove(index, length);

    /// <summary>Makes the changes of one scripted step (gen.mjs' runOp) in one transaction.</summary>
    public void EditText(IReadOnlyList<TextOp> ops) => _doc.Transact(_ =>
    {
        foreach (var op in ops)
        {
            var text = _doc.GetText(op.Root);
            switch (op.Op)
            {
                case "insert":
                    text.Insert(op.At, op.Text, Attributes(op.Attributes));
                    break;
                case "delete":
                    text.Remove(op.At, op.Length);
                    break;
                case "format":
                    text.Format(op.At, op.Length, Attributes(op.Attributes));
                    break;
                case "embed":
                    text.InsertEmbed(op.At, YValue.FromObject(op.Embed), Attributes(op.Attributes));
                    break;
                default:
                    throw new ArgumentException($"Unknown text op '{op.Op}'.", nameof(ops));
            }
        }
    });

    private static Dictionary<string, YValue> Attributes(IDictionary<string, object> attributes) =>
        attributes?.ToDictionary(kvp => kvp.Key, kvp => YValue.FromObject(kvp.Value));

    /// <summary>Y.Text.toDelta() as plain values: a list of {insert, attributes?}.</summary>
    public object Delta(string root)
    {
        var result = new List<object>();
        foreach (var d in _doc.GetText(root).ToDelta())
        {
            var op = new Dictionary<string, object> { ["insert"] = Plain(((Delta.Insert)d).Value.ToObject()) };
            if (d.Attributes is { Count: > 0 })
            {
                op["attributes"] = Plain(d.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToObject()));
            }

            result.Add(op);
        }

        return result;
    }

    /// <summary>toJSON() of a root type ("map" or "array") as plain values.</summary>
    public object Json(string root, string type) => type switch
    {
        "map" => Plain(_doc.GetMap(root).ToJson()),
        "array" => Plain(_doc.GetArray(root).ToJson()),
        "text" => _doc.GetText(root).ToString(),
        _ => throw new ArgumentException($"Unknown root type '{type}'.", nameof(type)),
    };

    /// <summary>
    /// The item list of every sequence (Y.Text, Y.Array, Y.Xml*) reachable from the given roots:
    /// the roots of type "text" and "array" themselves, plus sequences nested (visibly) in map and
    /// array roots, keyed by a path such as "root.list[0].inner". Read-only.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ItemRun>> Sequences(IEnumerable<(string Name, string Type)> roots)
    {
        var result = new Dictionary<string, IReadOnlyList<ItemRun>>();
        foreach (var (name, type) in roots)
        {
            YType root = type switch
            {
                "text" => _doc.GetText(name),
                "array" => _doc.GetArray(name),
                "map" => _doc.GetMap(name),
                _ => throw new ArgumentException($"Unknown root type '{type}'.", nameof(roots)),
            };
            Collect(root, name, result);
        }

        return result;
    }

    /// <summary>The item list of one root sequence ("text" or "array").</summary>
    public IReadOnlyList<ItemRun> Items(string root, string type) =>
        ItemsOf(type == "text" ? _doc.GetText(root) : _doc.GetArray(root));

    private static void Collect(YType type, string path, Dictionary<string, IReadOnlyList<ItemRun>> into)
    {
        switch (type)
        {
            case YArrayBase seq:
                into[path] = ItemsOf(seq);
                var i = 0;
                foreach (var value in seq)
                {
                    if (value is YType nested)
                    {
                        Collect(nested, $"{path}[{i}]", into);
                    }

                    i++;
                }

                break;
            case YMap map:
                foreach (var (key, value) in map)
                {
                    if (value is YType nested)
                    {
                        Collect(nested, $"{path}.{key}", into);
                    }
                }

                break;
        }
    }

    private static List<ItemRun> ItemsOf(YType type)
    {
        var runs = new List<ItemRun>();
        var previous = default(ItemRef);
        for (var s = type._start; !s.IsNull; s = s.Right)
        {
            if (!s.IsItem)
            {
                runs.Add(new ItemRun(s.Id.Client, s.Id.Clock, s.Length, null, null, s.Deleted, StructStore.KindNameForTests(s), LinkedBack: true, InParent: false));
                break;
            }

            runs.Add(new ItemRun(
                s.Id.Client,
                s.Id.Clock,
                s.Length,
                s.LeftOrigin is ItemId o ? (o.Client, o.Clock) : null,
                s.RightOrigin is ItemId r ? (r.Client, r.Clock) : null,
                s.Deleted,
                ItemRun.ItemKind,
                LinkedBack: s.Left == previous,
                InParent: ReferenceEquals(s.Parent, type) && s.ParentSub == null));
            previous = s;
        }

        return runs;
    }

    private static object Plain(object value) => value switch
    {
        null => null,
        YType t => Plain(t.ToJson()),
        string or bool or byte[] => value,
        IDictionary<string, object> map => map.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
        IDictionary dict => dict.Keys.Cast<object>().ToDictionary(k => (string)k, k => Plain(dict[k])),
        IEnumerable list => list.Cast<object>().Select(Plain).ToList(),
        float f => (double)f,
        double => value,
        IConvertible c => c.ToInt64(System.Globalization.CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Unexpected value of type {value.GetType()}."),
    };
}

/// <summary>Operations on encoded updates, without a document.</summary>
internal static class CorpusUpdates
{
    public static byte[] Merge(IList<byte[]> updates, bool v2) =>
        v2 ? YUpdate.Merge([.. updates], format: UpdateFormat.V2) : YUpdate.Merge([.. updates], format: UpdateFormat.V1);

    public static byte[] Diff(byte[] update, byte[] stateVector, bool v2) =>
        v2 ? YUpdate.Diff(update, stateVector, format: UpdateFormat.V2) : YUpdate.Diff(update, stateVector, format: UpdateFormat.V1);

    /// <summary>Per client, the first clock and the clock after the last struct in the update.</summary>
    public static (IReadOnlyDictionary<long, long> From, IReadOnlyDictionary<long, long> To) Meta(byte[] update, bool v2)
    {
        var meta = v2 ? YUpdate.ParseMeta(update, format: UpdateFormat.V2) : YUpdate.ParseMeta(update, format: UpdateFormat.V1);
        return (meta.From, meta.To);
    }

    /// <summary>The delete set carried by an update, as (clock, length) ranges per client.</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<(long Clock, long Length)>> DeleteSet(byte[] update, bool v2)
    {
        var decoded = v2 ? YUpdate.Decode(update, format: UpdateFormat.V2) : YUpdate.Decode(update, format: UpdateFormat.V1);
        return decoded.DeleteSet.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<(long, long)>)kvp.Value.Select(r => (r.Clock, r.Length)).ToList());
    }
}
