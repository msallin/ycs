// ------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
// ------------------------------------------------------------------------------

using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Ycs;

/// <summary>
/// JSON the way Yjs writes and reads it: <c>JSON.stringify</c> / <c>JSON.parse</c>. The v1 update format
/// stores formatting attributes, embeds and legacy <see cref="ContentJson"/> as JSON strings, so the bytes
/// Ycs writes there should be the bytes Yjs would write for the same value.
///
/// <para>Values are the closed set of <see cref="AnyEncoding"/>; there is no reflection, so this works
/// under trimming and Native AOT. Parsing uses <see cref="Utf8JsonReader"/> straight on the UTF-8 bytes
/// and returns <see cref="Dictionary{TKey, TValue}"/> of <see cref="string"/> to <see cref="object"/>,
/// <see cref="List{T}"/> of <see cref="object"/>, <see cref="string"/>, <see cref="bool"/>,
/// <see cref="int"/> for an integer literal that fits (<see cref="long"/> up to ±(2^53 - 1)), otherwise
/// <see cref="double"/>, and
/// <c>null</c>.</para>
///
/// <para>Writing is done by hand rather than with <see cref="Utf8JsonWriter"/>, because the bytes must be
/// those of <c>JSON.stringify</c>: ECMAScript number formatting (<c>1e+21</c>, <c>1.5e-7</c>, no <c>.0</c>)
/// and its minimal string escaping, which <see cref="Utf8JsonWriter"/>'s encoders do not reproduce.</para>
/// </summary>
internal static class YJson
{

    public static string Stringify(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    public static object? Parse(string json, int maxDepth = UpdateLimits.DefaultMaxDepth) => Parse(Encoding.UTF8.GetBytes(json), maxDepth);

    /// <summary>Parses JSON read from an update, nested at most <paramref name="maxDepth"/> levels; malformed JSON is a <see cref="YUpdateException"/>.</summary>
    public static object? Parse(ReadOnlySpan<byte> utf8Json, int maxDepth = UpdateLimits.DefaultMaxDepth)
    {
        try
        {
            return ParseCore(utf8Json, maxDepth);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw YUpdateException.Wrap(e);
        }
    }

    private static object? ParseCore(ReadOnlySpan<byte> utf8Json, int maxDepth)
    {
        // Utf8JsonReader counts the open lists and objects and rejects a deeper one (as a JsonException); a value at
        // depth maxDepth may itself be a list or an object, as in AnyEncoding.
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = maxDepth == int.MaxValue ? maxDepth : maxDepth + 1 });
        if (!reader.Read())
        {
            throw new JsonException("Empty JSON text.");
        }

        var value = ReadValue(ref reader);
        if (reader.Read())
        {
            throw new JsonException("Unexpected data after the JSON value.");
        }

        return value;
    }

    /// <summary>The value at the reader, read without recursion (its nesting is bounded only by the reader's MaxDepth).</summary>
    private static object? ReadValue(ref Utf8JsonReader reader)
    {
        var open = new List<object>();
        object? root = null;
        string? key = null;
        while (true)
        {
            object? value;
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    value = new Dictionary<string, object?>();
                    break;
                case JsonTokenType.StartArray:
                    value = new List<object?>();
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    open.RemoveAt(open.Count - 1);
                    if (open.Count == 0)
                    {
                        return root;
                    }

                    reader.Read();
                    continue;
                case JsonTokenType.PropertyName:
                    key = GetString(ref reader);
                    reader.Read();
                    continue;
                case JsonTokenType.String:
                    value = GetString(ref reader);
                    break;
                case JsonTokenType.Number:
                    {
                        if (reader.ValueSpan.IndexOfAny((byte)'.', (byte)'e', (byte)'E') < 0
                            && reader.TryGetInt64(out var l)
                            && l >= -JsValue.MaxSafeInteger && l <= JsValue.MaxSafeInteger)
                        {
                            value = l >= int.MinValue && l <= int.MaxValue ? (object)(int)l : l;
                        }
                        else
                        {
                            value = reader.GetDouble();
                        }

                        break;
                    }
                case JsonTokenType.True:
                    value = true;
                    break;
                case JsonTokenType.False:
                    value = false;
                    break;
                case JsonTokenType.Null:
                    value = null;
                    break;
                default:
                    throw new JsonException($"Unexpected JSON token: {reader.TokenType}.");
            }

            if (open.Count == 0)
            {
                root = value;
            }
            else if (open[^1] is Dictionary<string, object?> dict)
            {
                dict[key!] = value; // a repeated key: the last one wins, as in JSON.parse
            }
            else
            {
                ((List<object?>)open[^1]).Add(value);
            }

            if (value is Dictionary<string, object?> or List<object?>)
            {
                open.Add(value);
            }
            else if (open.Count == 0)
            {
                return root;
            }

            reader.Read();
        }
    }

    /// <summary>
    /// The string or property name at the reader. JSON.parse accepts an escaped lone surrogate
    /// (<c>"\ud83d"</c>, which JSON.stringify writes for one) and keeps it; Utf8JsonReader.GetString
    /// rejects it, so such a string is unescaped here.
    /// </summary>
    private static string GetString(ref Utf8JsonReader reader)
    {
        if (!reader.ValueIsEscaped)
        {
            return reader.GetString()!;
        }

        // The reader runs over a span, so the value is never a sequence.
        var raw = reader.ValueSpan;
        if (raw.IndexOf("\\u"u8) < 0)
        {
            return reader.GetString()!;
        }

        return Unescape(Encoding.UTF8.GetString(raw));
    }

    // The reader has validated the escapes: a backslash is followed by one of "\/bfnrt or u and four hex digits.
    private static string Unescape(string escaped)
    {
        var sb = new StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            c = escaped[++i];
            switch (c)
            {
                case 'b':
                    sb.Append('\b');
                    break;
                case 'f':
                    sb.Append('\f');
                    break;
                case 'n':
                    sb.Append('\n');
                    break;
                case 'r':
                    sb.Append('\r');
                    break;
                case 't':
                    sb.Append('\t');
                    break;
                case 'u':
                    sb.Append((char)int.Parse(escaped.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }

    // A value that contains itself: see AnyEncoding.WriteAny.
    private const int CycleDepth = 1 << 20;

    /// <summary>Writes a value as JSON.stringify, without recursion: one open list or object per frame.</summary>
    private static void Write(StringBuilder sb, object? value)
    {
        // Per open list or object: its entries, whether it is an object (keys written), and whether an entry was written.
        var open = new List<(IEnumerator Entries, bool Object, bool Any)>();
        WriteValue(sb, value, open);
        while (open.Count > 0)
        {
            var (entries, isObject, any) = open[^1];
            if (!entries.MoveNext())
            {
                sb.Append(isObject ? '}' : ']');
                open.RemoveAt(open.Count - 1);
                continue;
            }

            if (open.Count > CycleDepth)
            {
                throw AnyEncoding.TooDeep(CycleDepth);
            }

            open[^1] = (entries, isObject, true);
            if (any)
            {
                sb.Append(',');
            }

            object? entry = entries.Current;
            if (isObject)
            {
                string key;
                (key, entry) = entry switch
                {
                    KeyValuePair<string, object?> kv => (kv.Key, kv.Value),
                    DictionaryEntry de => (de.Key as string ?? throw new ArgumentException("Object keys must be strings.", nameof(value)), de.Value),
                    _ => throw new InvalidOperationException(),
                };
                WriteString(sb, key);
                sb.Append(':');
            }

            WriteValue(sb, entry, open);
        }
    }

    /// <summary>Writes a scalar, or the opening bracket of a list or an object and opens its frame.</summary>
    private static void WriteValue(StringBuilder sb, object? value, List<(IEnumerator Entries, bool Object, bool Any)> open)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                WriteString(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case double d:
                WriteNumber(sb, d);
                return;
            case float f:
                WriteNumber(sb, f);
                return;
            case long or int or short or sbyte or byte or ushort or uint:
                WriteInteger(sb, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            case ulong ul:
                WriteInteger(sb, ul <= JsValue.MaxSafeInteger ? (long)ul : throw AnyEncoding.OutOfRange(ul));
                return;
            case BigInteger:
                // JSON.stringify throws a TypeError for a BigInt.
                throw new NotSupportedException("A BigInt cannot be written as JSON.");
            case byte[] bytes:
                // JSON.stringify writes a Uint8Array as an object of its indices.
                sb.Append('{');
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(i.ToString(CultureInfo.InvariantCulture)).Append("\":")
                        .Append(bytes[i].ToString(CultureInfo.InvariantCulture));
                }
                sb.Append('}');
                return;
            case IDictionary<string, object?> map:
                sb.Append('{');
                open.Add((map.GetEnumerator(), true, false));
                return;
            case IReadOnlyDictionary<string, object?> map:
                sb.Append('{');
                open.Add((map.GetEnumerator(), true, false));
                return;
            case IDictionary map:
                sb.Append('{');
                open.Add((map.GetEnumerator(), true, false));
                return;
            case IList items:
                sb.Append('[');
                open.Add((items.GetEnumerator(), false, false));
                return;
            default:
                throw AnyEncoding.Unsupported(value);
        }
    }

    private static void WriteInteger(StringBuilder sb, long l)
    {
        if (l < -JsValue.MaxSafeInteger || l > JsValue.MaxSafeInteger)
        {
            throw AnyEncoding.OutOfRange(l);
        }

        sb.Append(l.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// ECMAScript Number::toString on the shortest round-trip digits: plain notation for a decimal exponent
    /// in (-7, 21], exponent notation (<c>1e+21</c>, <c>1.5e-7</c>) outside it. NaN and the infinities are
    /// <c>null</c>, as <c>JSON.stringify</c> writes them; <c>-0</c> is <c>0</c>.
    /// </summary>
    internal static void WriteNumber(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            sb.Append("null");
            return;
        }

        if (d == 0)
        {
            sb.Append('0');
            return;
        }

        // "R" gives the shortest digits that round-trip, as ECMAScript requires.
        var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        var exp = 0;
        var ePos = r.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            exp = int.Parse(r.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r[..ePos];
        }

        var dot = r.IndexOf('.');
        var intPart = dot >= 0 ? r[..dot] : r;
        var fracPart = dot >= 0 ? r[(dot + 1)..] : string.Empty;
        var digits = intPart + fracPart;
        var n = intPart.Length + exp; // value = 0.digits × 10^n

        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits[lead..];
        n -= lead;
        digits = digits.TrimEnd('0');
        var k = digits.Length;

        if (d < 0) sb.Append('-');
        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            var e = n - 1;
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            sb.Append('e').Append(e >= 0 ? '+' : '-').Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>JSON.stringify's string quoting (ES2019 well-formed): only <c>"</c>, <c>\</c>, control
    /// characters and lone surrogates are escaped; everything else, non-ASCII included, is written as is.</summary>
    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        AppendUnicodeEscape(sb, c);
                    }
                    else if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                    {
                        sb.Append(c).Append(s[++i]);
                    }
                    else if (char.IsSurrogate(c))
                    {
                        AppendUnicodeEscape(sb, c); // a lone surrogate
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c) =>
        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
}
