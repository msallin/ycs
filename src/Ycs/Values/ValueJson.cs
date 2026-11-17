using System.Buffers;
using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace Ycs;

/// <summary>
/// Writes a tree of stored values (what a document holds: scalars, <see cref="FrozenList"/>, <see cref="FrozenMap"/>, the lists and
/// dictionaries of <see cref="YType.ToJsonTree"/>, subdocuments) to a <see cref="Utf8JsonWriter"/>, without recursion and without
/// reflection. The mapping is that of Yjs' <c>toJSON()</c> followed by <c>JSON.stringify</c>, but for the two values JSON.stringify
/// cannot write, see <see cref="YValue.WriteTo"/>.
/// </summary>
internal static class ValueJson
{
    // The frames of the open lists and objects of a write; a value that contains itself (a subdocument that holds its own
    // parent) is cut here.
    private const int CycleDepth = 1 << 20;

    private struct Frame
    {
        public IEnumerator Entries;
        public bool IsObject;
    }

    internal static string ToString(object? tree, JsonWriterOptions options)
    {
        options.Encoder ??= JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        if (options.MaxDepth == 0)
        {
            options.MaxDepth = int.MaxValue;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            Write(writer, tree);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static void Write(Utf8JsonWriter writer, object? root)
    {
        var open = new List<Frame>();
        WriteValue(writer, root, open);
        while (open.Count > 0)
        {
            var frame = open[^1];
            if (!frame.Entries.MoveNext())
            {
                if (frame.IsObject)
                {
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteEndArray();
                }

                open.RemoveAt(open.Count - 1);
                continue;
            }

            if (open.Count > CycleDepth)
            {
                throw new InvalidOperationException("The value is nested too deep or contains itself, and cannot be written as JSON.");
            }

            var entry = frame.Entries.Current;
            if (frame.IsObject)
            {
                string key;
                (key, entry) = entry switch
                {
                    KeyValuePair<string, object?> kv => (kv.Key, kv.Value),
                    DictionaryEntry de => ((string)de.Key, de.Value),
                    _ => throw new InvalidOperationException(),
                };
                WritePropertyName(writer, key);
            }

            WriteValue(writer, entry, open);
        }
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value, List<Frame> open)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case string s:
                WriteString(writer, s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            case double d:
                WriteNumber(writer, d);
                return;
            case int i:
                writer.WriteNumberValue(i);
                return;
            case long l:
                writer.WriteNumberValue(l);
                return;
            case float f:
                WriteNumber(writer, f);
                return;
            case short or sbyte or byte or ushort or uint:
                writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong ul:
                writer.WriteNumberValue(ul);
                return;
            case BigInteger big:
                writer.WriteRawValue(big.ToString(CultureInfo.InvariantCulture), skipInputValidation: true);
                return;
            case byte[] bytes:
                writer.WriteBase64StringValue(bytes);
                return;
            case FrozenMap frozenMap:
                writer.WriteStartObject();
                open.Add(new Frame { Entries = frozenMap.RawEnumerator(), IsObject = true });
                return;
            case FrozenList frozenList:
                writer.WriteStartArray();
                open.Add(new Frame { Entries = frozenList.RawEnumerator(), IsObject = false });
                return;
            case IDictionary<string, object?> map:
                writer.WriteStartObject();
                open.Add(new Frame { Entries = map.GetEnumerator(), IsObject = true });
                return;
            case IList list:
                writer.WriteStartArray();
                open.Add(new Frame { Entries = list.GetEnumerator(), IsObject = false });
                return;
            case YDoc doc:
                // A document is inserted once and never into itself or its own subdocuments (YDoc.CheckCanInsertInto), so no
                // document is its own descendant.
                writer.WriteStartObject();
                open.Add(new Frame { Entries = doc.JsonRoots().GetEnumerator(), IsObject = true });
                return;
            default:
                if (ReferenceEquals(value, JsValue.Undefined))
                {
                    writer.WriteNullValue();
                    return;
                }

                throw AnyEncoding.Unsupported(value);
        }
    }

    /// <summary>A number as JSON.stringify writes it: an integer without a fraction, the shortest digits that round-trip, <c>null</c> for NaN and the infinities.</summary>
    internal static void WriteNumber(Utf8JsonWriter writer, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            writer.WriteNullValue();
        }
        else if (Math.Floor(d) == d && Math.Abs(d) <= JsValue.MaxSafeInteger)
        {
            writer.WriteNumberValue((long)d);
        }
        else
        {
            var sb = new StringBuilder();
            YJson.WriteNumber(sb, d);
            writer.WriteRawValue(sb.ToString(), skipInputValidation: true);
        }
    }

    // A lone surrogate cannot be encoded as UTF-8, and the writer refuses it. A value is written as the escape JSON.stringify
    // writes; a property name cannot be written raw, and gets U+FFFD.
    private static void WriteString(Utf8JsonWriter writer, string s)
    {
        if (!HasLoneSurrogate(s))
        {
            writer.WriteStringValue(s);
            return;
        }

        var sb = new StringBuilder(s.Length + 8).Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else if (c is '"' or '\\')
            {
                sb.Append('\\').Append(c);
            }
            else if (c < 0x20)
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        writer.WriteRawValue(sb.Append('"').ToString(), skipInputValidation: true);
    }

    private static void WritePropertyName(Utf8JsonWriter writer, string key)
    {
        if (!HasLoneSurrogate(key))
        {
            writer.WritePropertyName(key);
            return;
        }

        var chars = key.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (char.IsHighSurrogate(chars[i]) && i + 1 < chars.Length && char.IsLowSurrogate(chars[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(chars[i]))
            {
                chars[i] = '�';
            }
        }

        writer.WritePropertyName(chars);
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(s[i]))
            {
                return true;
            }
        }

        return false;
    }
}
