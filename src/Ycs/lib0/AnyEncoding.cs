using System.Collections;
using System.Numerics;

namespace Ycs;

/// <summary>
/// The lib0 'any' encoding (<c>writeAny</c>/<c>readAny</c>), used for the values of
/// Y.Map and Y.Array, and for embeds and formatting attributes in the v2 format.
/// <para>
/// Values are a small closed set of CLR types; there is no reflection and no serializer.
/// Reading produces exactly:
/// <list type="table">
/// <item><term><c>null</c></term><description>null and undefined (126, 127)</description></item>
/// <item><term><see cref="bool"/></term><description>true/false (120, 121)</description></item>
/// <item><term><see cref="int"/></term><description>an integer (125); <see cref="long"/> if another writer used more than 32 bits</description></item>
/// <item><term><see cref="double"/></term><description>a float32 or float64 (124, 123), and a negative zero</description></item>
/// <item><term><see cref="BigInteger"/></term><description>a JavaScript BigInt (122)</description></item>
/// <item><term><see cref="string"/></term><description>a string (119)</description></item>
/// <item><term><c>byte[]</c></term><description>a Uint8Array (116)</description></item>
/// <item><term><see cref="FrozenList"/></term><description>an array (117)</description></item>
/// <item><term><see cref="FrozenMap"/></term><description>an object (118), its keys in the order they were written</description></item>
/// </list>
/// </para>
/// <para>
/// Writing accepts those, every built-in integer type, <see cref="float"/>,
/// <see cref="IDictionary{TKey, TValue}"/> and <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// with string keys, a non-generic <see cref="IDictionary"/> with string keys, and any
/// <see cref="IList"/> (arrays, <c>List&lt;T&gt;</c>); what a document stores has been copied by <see cref="ValueFreezer.Freeze"/>. Numbers follow JavaScript's rules, so the
/// bytes match what Yjs writes for the same value: an integer within ±(2^31 - 1) is a var-int,
/// any other number is a float32 if that is exact and a float64 otherwise. An integer beyond
/// ±(2^53 - 1) has no exact JavaScript number and is rejected; use <see cref="YValue.BigInt(long)"/>.
/// </para>
/// </summary>
internal static class AnyEncoding
{

    /// <summary>
    /// How deep <see cref="WriteAny"/> goes before it decides that a value contains itself. What a document
    /// holds was read or validated under a depth limit (<see cref="UpdateLimits.MaxDepth"/>), but a
    /// list or dictionary given to it can still be changed by the caller afterwards.
    /// </summary>
    private const int CycleDepth = 1 << 20;

    // The walks below are iterative: values come from untrusted input and may be nested as deeply as the
    // limits allow, which a recursion would pay for with the stack. A frame is one open list or object.
    private enum Kind : byte
    {
        List,
        Dictionary,
        ReadOnlyDictionary,
        LegacyDictionary,
    }

    private struct Frame(Kind kind, IEnumerator entries)
    {
        public readonly Kind Kind = kind;
        public readonly IEnumerator Entries = entries;
    }

    public static void WriteAny(this Lib0Writer writer, object? o)
    {
        List<Frame>? frames = null;
        if (!WriteValue(writer, o, ref frames))
        {
            return;
        }

        while (frames!.Count > 0)
        {
            var frame = frames[^1];
            if (!frame.Entries.MoveNext())
            {
                frames.RemoveAt(frames.Count - 1);
                continue;
            }

            if (frames.Count > CycleDepth)
            {
                throw TooDeep(CycleDepth, "value");
            }

            var value = Entry(frame, out var key);
            if (key != null)
            {
                writer.WriteVarString(key);
            }

            WriteValue(writer, value, ref frames);
        }
    }

    /// <summary>The value of the current entry of <paramref name="frame"/>, and its key for an object.</summary>
    private static object? Entry(Frame frame, out string? key)
    {
        switch (frame.Kind)
        {
            case Kind.Dictionary:
            case Kind.ReadOnlyDictionary:
                var kvp = (KeyValuePair<string, object?>)frame.Entries.Current!;
                key = kvp.Key;
                return kvp.Value;
            case Kind.LegacyDictionary:
                var entry = (DictionaryEntry)frame.Entries.Current!;
                key = entry.Key as string ?? throw NotAStringKey(entry.Key);
                return entry.Value;
            default:
                key = null;
                return frame.Entries.Current;
        }
    }

    /// <summary>Opens a frame for a list or an object (true), or returns false for any other value.</summary>
    private static bool Open(object? o, ref List<Frame>? frames, out int count)
    {
        Frame frame;
        switch (o)
        {
            // The containers of a document are walked as stored: no copy of a byte array on the way.
            case FrozenMap frozenMap:
                frame = new Frame(Kind.Dictionary, frozenMap.RawEnumerator());
                count = frozenMap.Count;
                break;
            case FrozenList frozenList:
                frame = new Frame(Kind.List, frozenList.RawEnumerator());
                count = frozenList.Count;
                break;
            case IDictionary<string, object?> map:
                frame = new Frame(Kind.Dictionary, map.GetEnumerator());
                count = map.Count;
                break;
            case IReadOnlyDictionary<string, object?> map:
                frame = new Frame(Kind.ReadOnlyDictionary, map.GetEnumerator());
                count = map.Count;
                break;
            case IDictionary dict: // e.g. Dictionary<string, string>
                frame = new Frame(Kind.LegacyDictionary, dict.GetEnumerator());
                count = dict.Count;
                break;
            case IList list when o is not byte[]:
                frame = new Frame(Kind.List, list.GetEnumerator());
                count = list.Count;
                break;
            default:
                count = 0;
                return false;
        }

        (frames ??= new List<Frame>()).Add(frame);
        return true;
    }

    /// <summary>Writes a value; for a list or an object only its header, and opens its frame (true).</summary>
    private static bool WriteValue(Lib0Writer writer, object? o, ref List<Frame>? frames)
    {
        switch (o)
        {
            case null: // TYPE 126: null
                writer.WriteByte(126);
                return false;
            case string str: // TYPE 119: STRING
                writer.WriteByte(119);
                writer.WriteVarString(str);
                return false;
            case bool b: // TYPE 120/121: boolean (true/false)
                writer.WriteByte((byte)(b ? 120 : 121));
                return false;
            case double d:
                WriteNumber(writer, d);
                return false;
            case float f:
                WriteNumber(writer, f);
                return false;
            case int i:
                WriteInteger(writer, i);
                return false;
            case long l:
                WriteInteger(writer, l);
                return false;
            case short sh:
                WriteInteger(writer, sh);
                return false;
            case sbyte sb:
                WriteInteger(writer, sb);
                return false;
            case byte ub:
                WriteInteger(writer, ub);
                return false;
            case ushort us:
                WriteInteger(writer, us);
                return false;
            case uint ui:
                WriteInteger(writer, ui);
                return false;
            case ulong ul:
                WriteInteger(writer, ul <= JsValue.MaxSafeInteger ? (long)ul : throw OutOfRange(ul));
                return false;
            case BigInteger bi: // TYPE 122: BigInt
                writer.WriteByte(122);
                writer.WriteBigInt64(ToInt64(bi));
                return false;
            case byte[] ba: // TYPE 116: Uint8Array
                writer.WriteByte(116);
                writer.WriteVarUint8Array(ba);
                return false;
        }

        if (!Open(o, ref frames, out var count))
        {
            throw Unsupported(o!);
        }

        // TYPE 117: Array, TYPE 118: object
        writer.WriteByte((byte)(frames![^1].Kind == Kind.List ? 117 : 118));
        writer.WriteVarUint((uint)count);
        return true;
    }

    /// <summary>Reads a value as lib0's <c>readAny</c>, nested at most <see cref="Lib0Reader.MaxDepth"/> levels.</summary>
    public static object? ReadAny(this Lib0Reader reader) => ReadAny(reader, reader.ReadByte(), 0);

    /// <summary>
    /// Reads a value as <see cref="ReadAny(Lib0Reader)"/> does, but a member of an object that is
    /// <c>undefined</c> (not null) is read as <see cref="JsValue.Undefined"/>: where Yjs destructures
    /// the object, such a member counts as absent.
    /// </summary>
    public static object? ReadAnyMarkingUndefined(this Lib0Reader reader)
    {
        var type = reader.ReadByte();
        if (type != 118)
        {
            return ReadAny(reader, type, 0);
        }

        var len = reader.ReadVarUint();
        var obj = new FrozenMap(Limits.Capacity(len, reader.Remaining / 2));
        for (long i = 0; i < len; i++)
        {
            var key = reader.ReadVarString();
            var memberType = reader.ReadByte();
            var member = memberType == 127 ? JsValue.Undefined : ReadAny(reader, memberType, 1);
            obj.Set(key, member);
            if (member is FrozenList or FrozenMap)
            {
                obj.Depth = Math.Max(obj.Depth, DepthOf(member) + 1);
            }
        }

        return obj;
    }

    /// <summary>Reads a value of <paramref name="type"/> that is nested <paramref name="depth"/> levels deep already.</summary>
    private static object? ReadAny(Lib0Reader reader, byte type, int depth)
    {
        var maxDepth = reader.MaxDepth;
        CheckDepth(depth, maxDepth);
        if (type is not (117 or 118))
        {
            return ReadScalar(reader, type);
        }

        // Each open list or object with the number of its elements still to read.
        var open = new List<(object Container, long Remaining)>();
        var root = OpenContainer(reader, type, open);
        while (open.Count > 0)
        {
            var (container, remaining) = open[^1];
            if (remaining == 0)
            {
                open.RemoveAt(open.Count - 1);
                if (open.Count > 0)
                {
                    RaiseDepth(open[^1].Container, container);
                }

                continue;
            }

            open[^1] = (container, remaining - 1);
            var obj = container as FrozenMap;
            var key = obj != null ? reader.ReadVarString() : null;
            var childType = reader.ReadByte();
            CheckDepth(depth + open.Count, maxDepth);
            var child = childType is 117 or 118 ? OpenContainer(reader, childType, open) : ReadScalar(reader, childType);
            if (obj != null)
            {
                obj.Set(key!, child);
            }
            else
            {
                ((FrozenList)container).Append(child);
            }
        }

        return root;
    }

    private static int DepthOf(object? container) => container is FrozenList list ? list.Depth : ((FrozenMap)container!).Depth;

    /// <summary>Counts the levels of <paramref name="child"/> into those of <paramref name="parent"/>, when the child is closed.</summary>
    private static void RaiseDepth(object parent, object child)
    {
        if (parent is FrozenList list)
        {
            list.Depth = Math.Max(list.Depth, DepthOf(child) + 1);
        }
        else
        {
            var map = (FrozenMap)parent;
            map.Depth = Math.Max(map.Depth, DepthOf(child) + 1);
        }
    }

    private static void CheckDepth(int depth, int maxDepth)
    {
        if (depth > maxDepth)
        {
            throw new YUpdateException($"A value is nested deeper than {maxDepth} levels (UpdateLimits.MaxDepth).");
        }
    }

    private static object OpenContainer(Lib0Reader reader, byte type, List<(object Container, long Remaining)> open)
    {
        var len = reader.ReadVarUint();
        object container = type == 117
            ? new FrozenList(Limits.Capacity(len, reader.Remaining))
            : new FrozenMap(Limits.Capacity(len, reader.Remaining / 2));
        open.Add((container, len));
        return container;
    }

    private static object? ReadScalar(Lib0Reader reader, byte type)
    {
        switch (type)
        {
            case 119: // String
                return reader.ReadVarString();
            case 120: // boolean true
                return true;
            case 121: // boolean false
                return false;
            case 122: // BigInt
                return new BigInteger(reader.ReadBigInt64());
            case 123: // Float64
                return reader.ReadFloat64();
            case 124: // Float32, widened: JavaScript has a single number type.
                return (double)reader.ReadFloat32();
            case 125: // integer: Yjs only writes 31-bit integers this way, which fit an int.
                {
                    var (value, sign) = reader.ReadVarInt();
                    if (value == 0 && sign < 0)
                    {
                        return -0.0;
                    }

                    return value >= int.MinValue && value <= int.MaxValue ? (object)(int)value : value;
                }
            case 126: // null
            case 127: // undefined
                return null;
            case 116: // Uint8Array
                return reader.ReadVarUint8Array();
            default:
                throw new YUpdateException($"Unknown 'any' type: {type}.");
        }
    }

    /// <summary>lib0 <c>writeAny</c> for a JavaScript number.</summary>
    private static void WriteNumber(Lib0Writer writer, double d)
    {
        if (Math.Abs(d) <= int.MaxValue && Math.Floor(d) == d)
        {
            // TYPE 125: INTEGER. A negative zero keeps its sign, as in lib0.
            writer.WriteByte(125);
            writer.WriteVarInt((long)d, negativeZero: double.IsNegative(d));
        }
        else if ((double)(float)d == d)
        {
            // TYPE 124: FLOAT32. NaN fails the comparison and is written as a float64, as in lib0.
            writer.WriteByte(124);
            writer.WriteFloat32((float)d);
        }
        else
        {
            // TYPE 123: FLOAT64. .NET's NaN has the sign bit set; write JavaScript's canonical NaN.
            writer.WriteByte(123);
            writer.WriteFloat64(double.IsNaN(d) ? BitConverter.Int64BitsToDouble(0x7FF8000000000000) : d);
        }
    }

    private static void WriteInteger(Lib0Writer writer, long l)
    {
        if (l >= -int.MaxValue && l <= int.MaxValue)
        {
            writer.WriteByte(125);
            writer.WriteVarInt(l);
        }
        else if (l >= -JsValue.MaxSafeInteger && l <= JsValue.MaxSafeInteger)
        {
            // Beyond 31 bits a JavaScript number is written as a float.
            WriteNumber(writer, l);
        }
        else
        {
            throw OutOfRange(l);
        }
    }

    private static long ToInt64(BigInteger bi) =>
        bi >= long.MinValue && bi <= long.MaxValue
            ? (long)bi
            : throw new ArgumentOutOfRangeException(nameof(bi), bi, "A BigInt value must fit into 64 bits.");

    internal static ArgumentOutOfRangeException OutOfRange(object value) =>
        new(nameof(value), value, "Integers beyond ±(2^53 - 1) have no exact JavaScript number. Use System.Numerics.BigInteger for a BigInt.");

    private static ArgumentException NotAStringKey(object? key) =>
        new($"Object keys must be strings, not '{key?.GetType().Name ?? "null"}'.", nameof(key));

    internal static ArgumentException TooDeep(int maxDepth, string paramName) =>
        new($"Values nested deeper than {maxDepth} levels (or a value that contains itself) cannot be stored in a Yjs document.", paramName);

    internal static NotSupportedException Unsupported(object value) =>
        new($"Values of type '{value.GetType().Name}' cannot be stored in a Yjs document. Use null, bool, a number, " +
            "string, byte[], BigInteger, a list of such values, or a dictionary with string keys.");
}
