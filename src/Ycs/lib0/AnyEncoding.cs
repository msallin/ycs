// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

using System.Collections;

namespace Ycs;

/// <summary>
/// The lib0 'any' encoding (<c>writeAny</c>/<c>readAny</c>).
/// </summary>
internal static class AnyEncoding
{
    /// <summary>
    /// | Type                     | Ref    | Notes                              |
    /// | string                   | 119    | length-prefixed UTF-8              |
    /// | bool                     | 120/121| true/false                         |
    /// | double                   | 123    | float64, big endian                |
    /// | float                    | 124    | float32, big endian                |
    /// | int, long                | 125    | variable-length signed integer     |
    /// | null                     | 126    |                                    |
    /// | byte[]                   | 116    | length-prefixed bytes              |
    /// | IDictionary              | 118    | length, then key/value pairs       |
    /// | ICollection              | 117    | length, then values                |
    /// </summary>
    public static void WriteAny(this Lib0Writer writer, object o)
    {
        switch (o)
        {
            case string str: // TYPE 119: STRING
                writer.WriteByte(119);
                writer.WriteVarString(str);
                break;
            case bool b: // TYPE 120/121: boolean (true/false)
                writer.WriteByte((byte)(b ? 120 : 121));
                break;
            case double d: // TYPE 123: FLOAT64
                writer.WriteByte(123);
                writer.WriteFloat64(d);
                break;
            case float f: // TYPE 124: FLOAT32
                writer.WriteByte(124);
                writer.WriteFloat32(f);
                break;
            case int i: // TYPE 125: INTEGER
                writer.WriteByte(125);
                writer.WriteVarInt(i);
                break;
            case long l: // Special case: treat LONG as INTEGER.
                writer.WriteByte(125);
                writer.WriteVarInt(l);
                break;
            case null: // TYPE 126: null
                writer.WriteByte(126);
                break;
            case byte[] ba: // TYPE 116: ArrayBuffer
                writer.WriteByte(116);
                writer.WriteVarUint8Array(ba);
                break;
            case IDictionary dict: // TYPE 118: object (Dictionary<string, object>)
                writer.WriteByte(118);
                writer.WriteVarUint((uint)dict.Count);
                foreach (var key in dict.Keys)
                {
                    writer.WriteVarString(key.ToString());
                    writer.WriteAny(dict[key]);
                }
                break;
            case ICollection col: // TYPE 117: Array
                writer.WriteByte(117);
                writer.WriteVarUint((uint)col.Count);
                foreach (var item in col)
                {
                    writer.WriteAny(item);
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported object type: {o?.GetType()}");
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
            case 123: // Float64
                return reader.ReadFloat64();
            case 124: // Float32
                return reader.ReadFloat32();
            case 125: // integer
                return (int)reader.ReadVarInt().Value;
            case 126: // null
            case 127: // undefined
                return null;
            case 116: // ArrayBuffer
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
                throw new InvalidDataException($"Unknown object type: {type}");
        }
    }
}
