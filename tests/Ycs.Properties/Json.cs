using System.Globalization;
using System.Text;

namespace Ycs.Properties;

/// <summary>A JSON number as written, so that "5" (an integer) and "5.0" (a double) stay apart.</summary>
internal readonly record struct JsonNumber(string Text)
{
    public bool IsInteger => Text.IndexOfAny(['.', 'e', 'E']) < 0;

    public long Long => long.Parse(Text, CultureInfo.InvariantCulture);

    public double Double => double.Parse(Text, CultureInfo.InvariantCulture);

    public override string ToString() => Text;
}

/// <summary>
/// The JSON of scripts and observations, as plain values: null, bool, string, <see cref="JsonNumber"/>
/// (read) or long (written), <c>List&lt;object&gt;</c> and <c>Dictionary&lt;string, object&gt;</c>
/// (keys in file order). Its own reader because scripts hold lone surrogates (\ud800), which
/// System.Text.Json refuses; its own writer because the canonical form must equal oracle.mjs'
/// <c>canon</c> character for character.
/// </summary>
internal static class Json
{
    public static object Parse(string text)
    {
        var i = 0;
        var value = ReadValue(text, ref i);
        SkipWhitespace(text, ref i);
        if (i != text.Length)
        {
            throw new FormatException($"Trailing characters at {i}.");
        }

        return value;
    }

    /// <summary>
    /// Canonical JSON: no whitespace, keys sorted by UTF-16 code units, every character outside
    /// 0x20-0x7e (and " and \) escaped as \uXXXX with lowercase hex, integers only.
    /// </summary>
    public static string Canonical(object value)
    {
        var sb = new StringBuilder();
        Write(sb, value);
        return sb.ToString();
    }

    public static void Write(StringBuilder sb, object value)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case string s:
                WriteString(sb, s);
                break;
            case int n:
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case long n:
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
                break;
            case JsonNumber n:
                sb.Append(n.Text);
                break;
            case double d:
                {
                    // Only scripts hold doubles (observations tag every non-integer number).
                    var r = d.ToString("R", CultureInfo.InvariantCulture);
                    sb.Append(r);
                    if (r.IndexOfAny(['.', 'E']) < 0)
                    {
                        sb.Append(".0");
                    }

                    break;
                }
            case Dictionary<string, object> map:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (var key in map.Keys.Order(StringComparer.Ordinal))
                    {
                        if (map[key] is Missing)
                        {
                            continue;
                        }

                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        WriteString(sb, key);
                        sb.Append(':');
                        Write(sb, map[key]);
                    }

                    sb.Append('}');
                    break;
                }
            case List<object> list:
                {
                    sb.Append('[');
                    for (var k = 0; k < list.Count; k++)
                    {
                        if (k > 0)
                        {
                            sb.Append(',');
                        }

                        Write(sb, list[k]);
                    }

                    sb.Append(']');
                    break;
                }
            default:
                throw new ArgumentException($"Not a canonical JSON value: {value.GetType()}.", nameof(value));
        }
    }

    /// <summary>A key whose value is <see cref="Missing.Value"/> is left out (JavaScript's undefined).</summary>
    public sealed class Missing
    {
        public static readonly Missing Value = new();

        private Missing()
        {
        }
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            if (c == '"')
            {
                sb.Append("\\\"");
            }
            else if (c == '\\')
            {
                sb.Append("\\\\");
            }
            else if (c >= 0x20 && c < 0x7f)
            {
                sb.Append(c);
            }
            else
            {
                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        sb.Append('"');
    }

    private static void SkipWhitespace(string t, ref int i)
    {
        while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\n' || t[i] == '\r'))
        {
            i++;
        }
    }

    private static object ReadValue(string t, ref int i)
    {
        SkipWhitespace(t, ref i);
        if (i >= t.Length)
        {
            throw new FormatException("Unexpected end of JSON.");
        }

        switch (t[i])
        {
            case '{':
                {
                    i++;
                    var map = new Dictionary<string, object>();
                    SkipWhitespace(t, ref i);
                    if (t[i] == '}')
                    {
                        i++;
                        return map;
                    }

                    while (true)
                    {
                        SkipWhitespace(t, ref i);
                        var key = ReadString(t, ref i);
                        SkipWhitespace(t, ref i);
                        Expect(t, ref i, ':');
                        map[key] = ReadValue(t, ref i);
                        SkipWhitespace(t, ref i);
                        if (t[i] == ',')
                        {
                            i++;
                            continue;
                        }

                        Expect(t, ref i, '}');
                        return map;
                    }
                }
            case '[':
                {
                    i++;
                    var list = new List<object>();
                    SkipWhitespace(t, ref i);
                    if (t[i] == ']')
                    {
                        i++;
                        return list;
                    }

                    while (true)
                    {
                        list.Add(ReadValue(t, ref i));
                        SkipWhitespace(t, ref i);
                        if (t[i] == ',')
                        {
                            i++;
                            continue;
                        }

                        Expect(t, ref i, ']');
                        return list;
                    }
                }
            case '"':
                return ReadString(t, ref i);
            case 't':
                ExpectWord(t, ref i, "true");
                return true;
            case 'f':
                ExpectWord(t, ref i, "false");
                return false;
            case 'n':
                ExpectWord(t, ref i, "null");
                return null;
            default:
                {
                    var start = i;
                    while (i < t.Length && "+-0123456789.eE".Contains(t[i], StringComparison.Ordinal))
                    {
                        i++;
                    }

                    if (start == i)
                    {
                        throw new FormatException($"Unexpected '{t[i]}' at {i}.");
                    }

                    return new JsonNumber(t[start..i]);
                }
        }
    }

    private static string ReadString(string t, ref int i)
    {
        Expect(t, ref i, '"');
        var sb = new StringBuilder();
        while (t[i] != '"')
        {
            var c = t[i++];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            var e = t[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    sb.Append((char)int.Parse(t.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: throw new FormatException($"Bad escape at {i}.");
            }
        }

        i++;
        return sb.ToString();
    }

    private static void Expect(string t, ref int i, char c)
    {
        if (i >= t.Length || t[i] != c)
        {
            throw new FormatException($"Expected '{c}' at {i}.");
        }

        i++;
    }

    private static void ExpectWord(string t, ref int i, string word)
    {
        if (string.CompareOrdinal(t, i, word, 0, word.Length) != 0)
        {
            throw new FormatException($"Expected {word} at {i}.");
        }

        i += word.Length;
    }

    // ---- helpers for reading scripts

    public static Dictionary<string, object> Obj(params (string Key, object Value)[] entries)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in entries)
        {
            d[k] = v;
        }

        return d;
    }

    public static object Get(this Dictionary<string, object> o, string key) => o.TryGetValue(key, out var v) ? v : null;

    public static bool Has(this Dictionary<string, object> o, string key) => o.ContainsKey(key);

    public static string Str(this Dictionary<string, object> o, string key) => o.Get(key) as string;

    public static long Int(this Dictionary<string, object> o, string key, long fallback = 0) => o.Get(key) switch
    {
        JsonNumber n => n.IsInteger ? n.Long : (long)n.Double,
        long l => l,
        int i => i,
        _ => fallback,
    };

    public static bool Bool(this Dictionary<string, object> o, string key, bool fallback = false) => o.Get(key) is bool b ? b : fallback;

    public static List<object> List(this Dictionary<string, object> o, string key) => o.Get(key) as List<object>;

    /// <summary>A deep copy of a plain JSON value.</summary>
    public static object Clone(object v) => v switch
    {
        Dictionary<string, object> d => d.ToDictionary(kv => kv.Key, kv => Clone(kv.Value)),
        List<object> l => l.Select(Clone).ToList(),
        _ => v,
    };
}
