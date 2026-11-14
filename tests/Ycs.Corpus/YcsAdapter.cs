// The ONLY file in this project that touches the Ycs API. Everything else speaks bytes and
// plain CLR values (Dictionary<string, object>, List<object>, string, long, double, bool, null,
// byte[]), so when the library's API changes, this is the file to edit.

using System.Collections;
using System.Reflection;

namespace Ycs.Corpus;

/// <summary>A document under test.</summary>
internal sealed class CorpusDoc
{
    private static readonly object Remote = new();
    private readonly YDoc _doc;

    private CorpusDoc(YDoc doc) => _doc = doc;

    /// <summary>A fresh document; <paramref name="clientId"/> pins the id for reproducible conflict resolution.</summary>
    public static CorpusDoc Create(bool gc = true, int? clientId = null)
    {
        var doc = new YDoc(new YDocOptions { Gc = gc });
        if (clientId is int id)
        {
            // ClientId has an internal setter; tests need a fixed id so a seed reproduces a run.
            typeof(YDoc).GetProperty(nameof(YDoc.ClientId), BindingFlags.Instance | BindingFlags.Public)!.SetValue(doc, id);
        }

        return new CorpusDoc(doc);
    }

    /// <summary>Raised with the v1 update of every transaction made on this doc (not of applied remote updates).</summary>
    public event Action<byte[]> LocalUpdateV1
    {
        add => _doc.UpdateV1 += Forward(value);
        remove => throw new NotSupportedException();
    }

    private static EventHandler<(byte[] data, object origin, Transaction transaction)> Forward(Action<byte[]> handler) =>
        (_, e) =>
        {
            if (!ReferenceEquals(e.origin, Remote))
            {
                handler(e.data);
            }
        };

    public void ApplyV1(byte[] update) => _doc.ApplyUpdateV1(update, Remote);

    public void ApplyV2(byte[] update) => _doc.ApplyUpdateV2(update, Remote);

    public byte[] EncodeStateV1(byte[] stateVector = null) => _doc.EncodeStateAsUpdateV1(stateVector);

    public byte[] EncodeStateV2(byte[] stateVector = null) => _doc.EncodeStateAsUpdateV2(stateVector);

    /// <summary>The state vector in the (format-independent) lib0 encoding Yjs' encodeStateVector uses.</summary>
    public byte[] EncodeStateVector() => _doc.EncodeStateVectorV1();

    public string Text(string root) => _doc.GetText(root).ToString();

    public int TextLength(string root) => _doc.GetText(root).Length;

    public void InsertText(string root, int index, string text) => _doc.GetText(root).Insert(index, text);

    public void DeleteText(string root, int index, int length) => _doc.GetText(root).Delete(index, length);

    /// <summary>Y.Text.toDelta() as plain values: a list of {insert, attributes?}.</summary>
    public object Delta(string root)
    {
        var result = new List<object>();
        foreach (var d in _doc.GetText(root).ToDelta())
        {
            var op = new Dictionary<string, object> { ["insert"] = Plain(d.Insert) };
            if (d.Attributes is { Count: > 0 })
            {
                op["attributes"] = Plain(d.Attributes);
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

    private static object Plain(object value) => value switch
    {
        null => null,
        AbstractType t => Plain(t.ToJson()),
        string or bool or byte[] => value,
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
        v2 ? UpdateOperations.MergeUpdatesV2(updates) : UpdateOperations.MergeUpdates(updates);

    public static byte[] Diff(byte[] update, byte[] stateVector, bool v2) =>
        v2 ? UpdateOperations.DiffUpdateV2(update, stateVector) : UpdateOperations.DiffUpdate(update, stateVector);

    /// <summary>Per client, the first clock and the clock after the last struct in the update.</summary>
    public static (IReadOnlyDictionary<long, long> From, IReadOnlyDictionary<long, long> To) Meta(byte[] update, bool v2)
    {
        var meta = v2 ? UpdateOperations.ParseUpdateMetaV2(update) : UpdateOperations.ParseUpdateMeta(update);
        return (meta.From, meta.To);
    }

    /// <summary>The delete set carried by an update, as (clock, length) ranges per client.</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<(long Clock, long Length)>> DeleteSet(byte[] update, bool v2)
    {
        var decoded = v2 ? UpdateOperations.DecodeUpdateV2(update) : UpdateOperations.DecodeUpdate(update);
        return decoded.DeleteSet.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<(long, long)>)kvp.Value.Select(r => (r.Clock, r.Length)).ToList());
    }
}
