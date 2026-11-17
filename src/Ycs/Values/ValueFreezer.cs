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

    [ThreadStatic]
    private static Frame[]? t_stack;

    private enum EntryKind : byte
    {
        List,
        CopiedMap,
        Pairs,
        Legacy,
    }

    // One open list or object. A list is read by index; an object of IDictionary<string, object?> has its keys and values copied
    // into the arrays of its frozen map at once, and the values are replaced by their copies in place; the other objects are enumerated.
    private struct Frame
    {
        public EntryKind Kind;
        public object Container;
        public IList? List;
        public IEnumerator? Entries;
        public int Index;
        public int Count;
    }

    /// <summary>A value as stored: a byte array is copied, so the stored one stays private to the document.</summary>
    internal static object? OnRead(object? stored) => stored is byte[] bytes ? (byte[])bytes.Clone() : stored;

    /// <summary>The value of a type that is not in a document yet: <see cref="Freeze"/>, but a shared type or a subdocument stays itself.</summary>
    internal static object? Detach(object? value) => value is YType or YDoc ? value : Freeze(value);

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
        // The stack of the last call that finished is reused: an insert of many values would allocate one per value. A call
        // that fails leaves it null (it is dirty), and so does one that runs inside another (a collection that inserts).
        var stack = t_stack;
        t_stack = null;
        var top = 0;
        var root = Convert(value, limit, ref stack, ref top, out _);
        while (top > 0)
        {
            ref var frame = ref stack![top - 1];
            var container = frame.Container;
            object? child;
            string? key = null;
            var slot = -1;
            switch (frame.Kind)
            {
                case EntryKind.List:
                    if (frame.Index < frame.Count)
                    {
                        child = frame.List![frame.Index++];
                        break;
                    }

                    Close(stack, ref top);
                    continue;
                case EntryKind.CopiedMap:
                    if (frame.Index < frame.Count)
                    {
                        slot = frame.Index++;
                        child = ((FrozenMap)container).RawValues[slot];
                        break;
                    }

                    Close(stack, ref top);
                    continue;
                case EntryKind.Pairs:
                    if (frame.Entries!.MoveNext())
                    {
                        var pair = (KeyValuePair<string, object?>)frame.Entries.Current!;
                        key = pair.Key;
                        child = pair.Value;
                        break;
                    }

                    Close(stack, ref top);
                    continue;
                default:
                    if (frame.Entries!.MoveNext())
                    {
                        var entry = (DictionaryEntry)frame.Entries.Current!;
                        key = entry.Key as string ?? throw new ArgumentException($"Object keys must be strings, not '{entry.Key?.GetType().Name ?? "null"}'.", nameof(value));
                        child = entry.Value;
                        break;
                    }

                    Close(stack, ref top);
                    continue;
            }

            if (top > limit)
            {
                throw AnyEncoding.TooDeep(maxDepth, nameof(value));
            }

            var converted = Convert(child, limit, ref stack, ref top, out var opened);
            if (container is FrozenList list)
            {
                list.Append(converted);
            }
            else if (slot >= 0)
            {
                ((FrozenMap)container).SetRawAt(slot, converted);
            }
            else
            {
                ((FrozenMap)container).Set(key!, converted);
            }

            if (!opened && converted is FrozenList or FrozenMap)
            {
                Raise(container, DepthOf(converted));
            }
        }

        if (stack != null && stack.Length <= 64)
        {
            t_stack = stack;
        }

        return root;
    }

    private static void Close(Frame[] stack, ref int top)
    {
        var closed = stack[--top].Container;
        stack[top] = default;
        if (top > 0)
        {
            Raise(stack[top - 1].Container, DepthOf(closed));
        }
    }

    /// <summary>A scalar as the document keeps it, a frozen value reused, or a new container with its frame opened.</summary>
    private static object? Convert(object? value, int limit, ref Frame[]? stack, ref int top, out bool opened)
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
            case FrozenList list when top + list.Depth <= limit:
                return list;
            case FrozenMap map when top + map.Depth <= limit:
                return map;
        }

        // Same order of tests as AnyEncoding.Open, so a value is read as it is written.
        var frame = new Frame();
        switch (value)
        {
            case IDictionary<string, object?> dictionary:
                {
                    var count = dictionary.Count;
                    var keys = new string[count];
                    var values = new object?[count];
                    dictionary.Keys.CopyTo(keys, 0);
                    dictionary.Values.CopyTo(values, 0);
                    var map = new FrozenMap(0);
                    map.Adopt(keys, values, count);
                    frame.Kind = EntryKind.CopiedMap;
                    frame.Container = map;
                    frame.Count = count;
                    break;
                }

            case IReadOnlyDictionary<string, object?> dictionary:
                frame.Kind = EntryKind.Pairs;
                frame.Container = new FrozenMap(dictionary.Count);
                frame.Entries = dictionary.GetEnumerator();
                break;
            case IDictionary dictionary:
                frame.Kind = EntryKind.Legacy;
                frame.Container = new FrozenMap(dictionary.Count);
                frame.Entries = dictionary.GetEnumerator();
                break;
            case IList list:
                frame.Kind = EntryKind.List;
                frame.Container = new FrozenList(list.Count);
                frame.List = list;
                frame.Count = list.Count;
                break;
            default:
                throw AnyEncoding.Unsupported(value);
        }

        stack ??= new Frame[8];
        if (top == stack.Length)
        {
            Array.Resize(ref stack, top * 2);
        }

        stack[top++] = frame;
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
