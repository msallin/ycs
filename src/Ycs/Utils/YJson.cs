// ------------------------------------------------------------------------------
//  Copyright (c) Microsoft Corporation. All rights reserved.
//  Licensed under the MIT License. See License.txt in the project root for license information.
// ------------------------------------------------------------------------------

using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Ycs;

/// <summary>
/// JSON the way Yjs writes and reads it: <c>JSON.stringify</c> / <c>JSON.parse</c>. The v1 update format
/// stores formatting attributes, embeds and legacy <see cref="ContentJson"/> as JSON strings, so the bytes
/// Ycs writes there should be the bytes Yjs would write for the same value.
///
/// <para>Replaces Newtonsoft.Json, which differed from Yjs in three ways: it wrote an integral double as
/// <c>1.0</c> (JavaScript writes <c>1</c>), it turned ISO-8601-looking strings into <see cref="DateTime"/>
/// on read (JavaScript keeps them strings), and it pulled a third-party dependency into every consumer.</para>
///
/// <para>Values read back are plain CLR values — <see cref="Dictionary{TKey, TValue}"/> of
/// <see cref="string"/> to <see cref="object"/>, <see cref="List{T}"/> of <see cref="object"/>,
/// <see cref="string"/>, <see cref="bool"/>, <see cref="long"/> for an integer literal that fits, otherwise
/// <see cref="double"/>, and <c>null</c> — which is what the rest of Ycs and the v2 'any' encoder expect.</para>
/// </summary>
internal static class YJson
{
    private static readonly JsonDocumentOptions ParseOptions = new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static string Stringify(object value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    public static object Parse(ReadOnlySpan<byte> utf8Json) => Parse(Encoding.UTF8.GetString(utf8Json));

    public static object Parse(string json)
    {
        using (var doc = JsonDocument.Parse(json, ParseOptions))
        {
            return ToClr(doc.RootElement);
        }
    }

    private static object ToClr(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in e.EnumerateObject())
                    {
                        dict[prop.Name] = ToClr(prop.Value); // a repeated key: the last one wins, as in JSON.parse
                    }
                    return dict;
                }
            case JsonValueKind.Array:
                {
                    var list = new List<object>(e.GetArrayLength());
                    foreach (var item in e.EnumerateArray())
                    {
                        list.Add(ToClr(item));
                    }
                    return list;
                }
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                {
                    var raw = e.GetRawText();
                    if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && e.TryGetInt64(out var l))
                    {
                        return l;
                    }
                    return e.GetDouble();
                }
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static void Write(StringBuilder sb, object value)
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
                // Via the float's own shortest text, so 0.1f writes 0.1, not 0.10000000149011612.
                WriteNumber(sb, double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
                return;
            case decimal m:
                WriteNumber(sb, (double)m);
                return;
            case long _:
            case int _:
            case short _:
            case sbyte _:
            case byte _:
            case ulong _:
            case uint _:
            case ushort _:
                sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            case char c:
                WriteString(sb, c.ToString());
                return;
            case byte[] bytes:
                // Not a JSON value in Yjs' sense (binary is ContentBinary); kept as base64 text, which is what
                // Ycs wrote here before.
                WriteString(sb, Convert.ToBase64String(bytes));
                return;
            case JsonElement element:
                WriteElement(sb, element);
                return;
            case IDictionary<string, object> map:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (var kv in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                }
            case IDictionary map:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (DictionaryEntry kv in map)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                        sb.Append(':');
                        Write(sb, kv.Value);
                    }
                    sb.Append('}');
                    return;
                }
            case IEnumerable items:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var item in items)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Write(sb, item);
                    }
                    sb.Append(']');
                    return;
                }
            default:
                // Any other object (a POCO, an anonymous type, a DateTime): its public shape as
                // System.Text.Json sees it, then written by the rules above.
                using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType())))
                {
                    WriteElement(sb, doc.RootElement);
                }
                return;
        }
    }

    private static void WriteElement(StringBuilder sb, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Number:
                {
                    var raw = e.GetRawText();
                    if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && e.TryGetInt64(out var l))
                    {
                        sb.Append(l.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        WriteNumber(sb, e.GetDouble());
                    }
                    return;
                }
            default:
                Write(sb, ToClr(e));
                return;
        }
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

        var r = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        var exp = 0;
        var ePos = r.IndexOfAny(new[] { 'E', 'e' });
        if (ePos >= 0)
        {
            exp = int.Parse(r.Substring(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            r = r.Substring(0, ePos);
        }

        var dot = r.IndexOf('.');
        var intPart = dot >= 0 ? r.Substring(0, dot) : r;
        var fracPart = dot >= 0 ? r.Substring(dot + 1) : string.Empty;
        var digits = intPart + fracPart;
        var n = intPart.Length + exp; // value = 0.digits × 10^n

        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') lead++;
        digits = digits.Substring(lead);
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
