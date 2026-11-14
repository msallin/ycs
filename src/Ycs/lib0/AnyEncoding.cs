// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

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
/// <item><term><c>List&lt;object&gt;</c></term><description>an array (117)</description></item>
/// <item><term><c>Dictionary&lt;string, object&gt;</c></term><description>an object (118)</description></item>
/// </list>
/// </para>
/// <para>
/// Writing accepts those, every built-in integer type, <see cref="float"/>,
/// <see cref="IDictionary{TKey, TValue}"/> and <see cref="IReadOnlyDictionary{TKey, TValue}"/>
/// with string keys, a non-generic <see cref="IDictionary"/> with string keys, and any
/// <see cref="IList"/> (arrays, <c>List&lt;T&gt;</c>). Numbers follow JavaScript's rules, so the
/// bytes match what Yjs writes for the same value: an integer within ±(2^31 - 1) is a var-int,
/// any other number is a float32 if that is exact and a float64 otherwise. An integer beyond
/// ±(2^53 - 1) has no exact JavaScript number and is rejected; use <see cref="BigInteger"/>.
/// </para>
/// </summary>
internal static class AnyEncoding
{
    private const long MaxSafeInteger = (1L << 53) - 1;

    public static void WriteAny(this Lib0Writer writer, object o)
    {
        switch (o)
        {
            case null: // TYPE 126: null
                writer.WriteByte(126);
                break;
            case string str: // TYPE 119: STRING
                writer.WriteByte(119);
                writer.WriteVarString(str);
                break;
            case bool b: // TYPE 120/121: boolean (true/false)
                writer.WriteByte((byte)(b ? 120 : 121));
                break;
            case double d:
                WriteNumber(writer, d);
                break;
            case float f:
                WriteNumber(writer, f);
                break;
            case int i:
                WriteInteger(writer, i);
                break;
            case long l:
                WriteInteger(writer, l);
                break;
            case short s:
                WriteInteger(writer, s);
                break;
            case sbyte sb:
                WriteInteger(writer, sb);
                break;
            case byte ub:
                WriteInteger(writer, ub);
                break;
            case ushort us:
                WriteInteger(writer, us);
                break;
            case uint ui:
                WriteInteger(writer, ui);
                break;
            case ulong ul:
                WriteInteger(writer, ul <= MaxSafeInteger ? (long)ul : throw OutOfRange(ul));
                break;
            case BigInteger bi: // TYPE 122: BigInt
                writer.WriteByte(122);
                writer.WriteBigInt64(ToInt64(bi));
                break;
            case byte[] ba: // TYPE 116: Uint8Array
                writer.WriteByte(116);
                writer.WriteVarUint8Array(ba);
                break;
            case IDictionary<string, object> map: // TYPE 118: object
                writer.WriteByte(118);
                writer.WriteVarUint((uint)map.Count);
                foreach (var kvp in map)
                {
                    writer.WriteVarString(kvp.Key);
                    writer.WriteAny(kvp.Value);
                }
                break;
            case IReadOnlyDictionary<string, object> map: // TYPE 118: object
                writer.WriteByte(118);
                writer.WriteVarUint((uint)map.Count);
                foreach (var kvp in map)
                {
                    writer.WriteVarString(kvp.Key);
                    writer.WriteAny(kvp.Value);
                }
                break;
            case IDictionary dict: // TYPE 118: object, e.g. Dictionary<string, string>
                writer.WriteByte(118);
                writer.WriteVarUint((uint)dict.Count);
                foreach (DictionaryEntry entry in dict)
                {
                    writer.WriteVarString(entry.Key as string ?? throw NotAStringKey(entry.Key));
                    writer.WriteAny(entry.Value);
                }
                break;
            case IList list: // TYPE 117: Array
                writer.WriteByte(117);
                writer.WriteVarUint((uint)list.Count);
                foreach (var item in list)
                {
                    writer.WriteAny(item);
                }
                break;
            default:
                throw Unsupported(o);
        }
    }

    public static object ReadAny(this Lib0Reader reader)
    {
        var type = reader.ReadByte();
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
            case 117: // Array<object>
                {
                    var len = (int)reader.ReadVarUint();
                    var arr = new List<object>(Math.Min(len, reader.Remaining));
                    for (int i = 0; i < len; i++)
                    {
                        arr.Add(reader.ReadAny());
                    }

                    return arr;
                }
            case 118: // object (Dictionary<string, object>)
                {
                    var len = (int)reader.ReadVarUint();
                    var obj = new Dictionary<string, object>(Math.Min(len, reader.Remaining));
                    for (int i = 0; i < len; i++)
                    {
                        var key = reader.ReadVarString();
                        obj[key] = reader.ReadAny();
                    }

                    return obj;
                }
            default:
                throw new InvalidDataException($"Unknown 'any' type: {type}");
        }
    }

    /// <summary>
    /// Throws if <paramref name="value"/>, or anything nested in it, is not one of the types
    /// <see cref="WriteAny"/> accepts. Called when a value enters a document, so a bad value
    /// fails at the call that inserted it rather than at the next encode.
    /// </summary>
    public static void Validate(object value)
    {
        switch (value)
        {
            case null or string or bool or double or float or int or long or short or sbyte or byte or ushort or uint or byte[]:
                return;
            case ulong ul:
                if (ul > MaxSafeInteger)
                {
                    throw OutOfRange(ul);
                }
                return;
            case BigInteger bi:
                ToInt64(bi);
                return;
            case IDictionary<string, object> map:
                foreach (var kvp in map)
                {
                    Validate(kvp.Value);
                }
                return;
            case IReadOnlyDictionary<string, object> map:
                foreach (var kvp in map)
                {
                    Validate(kvp.Value);
                }
                return;
            case IDictionary dict:
                foreach (DictionaryEntry entry in dict)
                {
                    if (entry.Key is not string)
                    {
                        throw NotAStringKey(entry.Key);
                    }

                    Validate(entry.Value);
                }
                return;
            case IList list:
                foreach (var item in list)
                {
                    Validate(item);
                }
                return;
            default:
                throw Unsupported(value);
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
        else if (l >= -MaxSafeInteger && l <= MaxSafeInteger)
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
            : throw new ArgumentOutOfRangeException("value", bi, "A BigInt value must fit into 64 bits.");

    internal static ArgumentOutOfRangeException OutOfRange(object value) =>
        new("value", value, "Integers beyond ±(2^53 - 1) have no exact JavaScript number. Use System.Numerics.BigInteger for a BigInt.");

    private static ArgumentException NotAStringKey(object key) =>
        new($"Object keys must be strings, not '{key?.GetType().Name ?? "null"}'.", "value");

    internal static NotSupportedException Unsupported(object value) =>
        new($"Values of type '{value.GetType().Name}' cannot be stored in a Yjs document. Use null, bool, a number, " +
            "string, byte[], BigInteger, a list of such values, or a dictionary with string keys.");
}
