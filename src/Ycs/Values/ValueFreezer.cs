using System.Collections;
using System.Numerics;

namespace Ycs;

/// <summary>
/// How a value enters a document and leaves it. <see cref="Freeze"/> copies what a caller gives (lists, objects and byte
/// arrays) into <see cref="FrozenList"/>, <see cref="FrozenMap"/> and private arrays, so changing the original later changes
/// nothing; it also validates, as the encoding needs. <see cref="OnRead"/> is what every read passes a stored value through.
/// </summary>
internal static class ValueFreezer
{
    /// <summary>How many open lists and objects <see cref="Freeze"/> follows before it decides that a value contains itself.</summary>
    private const int CycleDepth = 1 << 20;

    private enum EntryKind : byte
    {
        List,
        Pairs,
        Legacy,
    }

    private struct Frame(EntryKind kind, IEnumerator entries, object container)
    {
        public readonly EntryKind Kind = kind;
        public readonly IEnumerator Entries = entries;
        public readonly object Container = container;
    }

    /// <summary>A value as stored: a byte array is copied, so the stored one stays private to the document.</summary>
    internal static object? OnRead(object? stored) => stored is byte[] bytes ? (byte[])bytes.Clone() : stored;

    /// <summary>The value of a type that is not in a document yet: <see cref="Freeze"/>, but a shared type or a subdocument stays itself.</summary>
    internal static object? Detach(object? value) => value is AbstractType or YDoc ? value : Freeze(value);

    internal static NotSupportedException Frozen() =>
        new("A list or object a document holds is read-only. Build a new value and insert it.");

    /// <summary>
    /// A deep copy of <paramref name="value"/> in the document's representation: lists and objects as frozen containers
    /// (an object keeps the order of its keys), byte arrays cloned, scalars as they are.
    /// </summary>
    /// <exception cref="NotSupportedException">A value of a type the encoding has no form for.</exception>
    /// <exception cref="ArgumentException">A value nested deeper than <paramref name="maxDepth"/>, or that contains itself, or an object with a key that is not a string.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An integer beyond ±(2^53 - 1), or a <see cref="BigInteger"/> that does not fit 64 bits.</exception>
    internal static object? Freeze(object? value, int maxDepth = UpdateLimits.DefaultMaxDepth)
    {
        var limit = Math.Min(maxDepth, CycleDepth);
        List<Frame>? frames = null;
        var root = Convert(value, limit, ref frames, out _);
        if (frames is null)
        {
            return root;
        }

        while (frames!.Count > 0)
        {
            var frame = frames[^1];
            if (!frame.Entries.MoveNext())
            {
                frames.RemoveAt(frames.Count - 1);
                if (frames.Count > 0)
                {
                    Raise(frames[^1].Container, DepthOf(frame.Container));
                }

                continue;
            }

            if (frames.Count > limit)
            {
                throw AnyEncoding.TooDeep(maxDepth, nameof(value));
            }

            object? child;
            string? key = null;
            switch (frame.Kind)
            {
                case EntryKind.Pairs:
                    var pair = (KeyValuePair<string, object?>)frame.Entries.Current!;
                    key = pair.Key;
                    child = pair.Value;
                    break;
                case EntryKind.Legacy:
                    var entry = (DictionaryEntry)frame.Entries.Current!;
                    key = entry.Key as string ?? throw new ArgumentException($"Object keys must be strings, not '{entry.Key?.GetType().Name ?? "null"}'.", nameof(value));
                    child = entry.Value;
                    break;
                default:
                    child = frame.Entries.Current;
                    break;
            }

            var converted = Convert(child, limit, ref frames, out var opened);
            if (frame.Container is FrozenMap map)
            {
                map.Set(key!, converted);
            }
            else
            {
                ((FrozenList)frame.Container).Append(converted);
            }

            if (!opened && converted is FrozenList or FrozenMap)
            {
                Raise(frame.Container, DepthOf(converted));
            }
        }

        return root;
    }

    /// <summary>A scalar as the document keeps it, a frozen value reused, or a new empty container with its frame opened.</summary>
    private static object? Convert(object? value, int limit, ref List<Frame>? frames, out bool opened)
    {
        opened = false;
        switch (value)
        {
            case null or string or bool or double or float or int or short or sbyte or byte or ushort or uint:
                return value;
            case long l:
                return l < -JsValue.MaxSafeInteger || l > JsValue.MaxSafeInteger ? throw AnyEncoding.OutOfRange(l) : value;
            case ulong ul:
                return ul > JsValue.MaxSafeInteger ? throw AnyEncoding.OutOfRange(ul) : value;
            case BigInteger bi:
                if (bi < long.MinValue || bi > long.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), bi, "A BigInt value must fit into 64 bits.");
                }

                return value;
            case byte[] bytes:
                return bytes.Clone();
            case FrozenList list when (frames?.Count ?? 0) + list.Depth <= limit:
                return list;
            case FrozenMap map when (frames?.Count ?? 0) + map.Depth <= limit:
                return map;
        }

        // Same order of tests as AnyEncoding.Open, so a value is read as it is written.
        Frame frame;
        switch (value)
        {
            case IDictionary<string, object?> dictionary:
                frame = new Frame(EntryKind.Pairs, dictionary.GetEnumerator(), new FrozenMap(dictionary.Count));
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                frame = new Frame(EntryKind.Pairs, dictionary.GetEnumerator(), new FrozenMap(dictionary.Count));
                break;
            case IDictionary dictionary:
                frame = new Frame(EntryKind.Legacy, dictionary.GetEnumerator(), new FrozenMap(dictionary.Count));
                break;
            case IList list:
                frame = new Frame(EntryKind.List, list.GetEnumerator(), new FrozenList(list.Count));
                break;
            default:
                throw AnyEncoding.Unsupported(value);
        }

        (frames ??= new List<Frame>()).Add(frame);
        opened = true;
        return frame.Container;
    }

    private static int DepthOf(object container) => container is FrozenList list ? list.Depth : ((FrozenMap)container).Depth;

    private static void Raise(object parent, int childDepth)
    {
        if (parent is FrozenList list)
        {
            list.Depth = Math.Max(list.Depth, childDepth + 1);
        }
        else
        {
            var map = (FrozenMap)parent;
            map.Depth = Math.Max(map.Depth, childDepth + 1);
        }
    }
}
