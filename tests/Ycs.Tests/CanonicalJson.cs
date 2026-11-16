// ------------------------------------------------------------------------------
//  <copyright company="Microsoft Corporation">
//      Copyright (c) Microsoft Corporation.  All rights reserved.
//  </copyright>
// ------------------------------------------------------------------------------

#nullable disable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ycs;

/// <summary>
/// Values as the generator's <c>canon</c> writes them: keys sorted, a shared type as
/// "&lt;TypeName&gt;", numbers by value, then one JSON text to compare.
/// </summary>
internal static class CanonicalJson
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    public static object FromJson(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt32(out var i) ? i : e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.Array => e.EnumerateArray().Select(FromJson).ToList(),
        _ => e.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value)),
    };

    public static string Write(object v)
    {
        var sb = new StringBuilder();
        Write(sb, v);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, object v)
    {
        switch (v)
        {
            case null: sb.Append("null"); break;
            case string s: Str(sb, s); break;
            case char c: Str(sb, c.ToString()); break;
            case bool b: sb.Append(b ? "true" : "false"); break;
            case YType t: Str(sb, $"<{t.GetType().Name}>"); break;
            case byte[] bytes: Write(sb, new Dictionary<string, object> { ["$bytes"] = Convert.ToHexString(bytes).ToLowerInvariant() }); break;
            case Delta d: Write(sb, d.ToPlain()); break;
            case IDictionary<string, object> m:
                {
                    sb.Append('{');
                    var first = true;
                    foreach (var k in m.Keys.OrderBy(k => k, StringComparer.Ordinal))
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        Str(sb, k);
                        sb.Append(':');
                        Write(sb, m[k]);
                    }

                    sb.Append('}');
                    break;
                }

            case IEnumerable e:
                {
                    sb.Append('[');
                    var first = true;
                    foreach (var x in e)
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }

                        first = false;
                        Write(sb, x);
                    }

                    sb.Append(']');
                    break;
                }

            case double dd:
                sb.Append(dd == Math.Floor(dd) && Math.Abs(dd) < 1e15 ? ((long)dd).ToString(CultureInfo.InvariantCulture) : dd.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float ff: Write(sb, (double)ff); break;
            case IConvertible n when n is int or long or short or uint or ulong: Write(sb, Convert.ToDouble(n, CultureInfo.InvariantCulture)); break;
            default: Str(sb, $"?{v.GetType().Name}:{v}"); break;
        }
    }

    private static void Str(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
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
}
