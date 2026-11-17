using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Ycs;

/// <summary>
/// JavaScript's view of the values a Yjs document holds, where Yjs relies on it: the safe-integer
/// range, integers in JSON-like values, truthiness, <c>String(value)</c> and <c>===</c>.
/// </summary>
internal static class JsValue
{
    /// <summary>JavaScript's <c>Number.MAX_SAFE_INTEGER</c>, 2^53 - 1: the largest client id, clock or integer a Yjs peer holds exactly.</summary>
    public const long MaxSafeInteger = (1L << 53) - 1;

    /// <summary>
    /// JavaScript's <c>undefined</c> where Yjs tells it from null (a legacy JSON value, a member of a
    /// subdocument's options); C# has none, so this marker stands for it until it is read.
    /// </summary>
    public static readonly object Undefined = new();

    /// <summary>
    /// Whether <paramref name="value"/>, read from JSON or the 'any' encoding, is an integer: an
    /// <see cref="int"/>, a <see cref="long"/>, or a <see cref="double"/> without a fraction within
    /// the safe integers (JavaScript has one number type).
    /// </summary>
    public static bool IsInteger(object? value, out long integer)
    {
        switch (value)
        {
            case int i:
                integer = i;
                return true;
            case long l:
                integer = l;
                return true;
            case double d when Math.Floor(d) == d && Math.Abs(d) <= MaxSafeInteger:
                integer = (long)d;
                return true;
            default:
                integer = 0;
                return false;
        }
    }

    /// <summary>JavaScript's truthiness, for the values of the 'any' encoding.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length != 0,
        double d => d != 0 && !double.IsNaN(d),
        float f => f != 0 && !float.IsNaN(f),
        int i => i != 0,
        long l => l != 0,
        BigInteger bi => !bi.IsZero,
        _ => true,
    };

    /// <summary>
    /// JavaScript's <c>String(value)</c>, which Yjs' <c>toString</c> of the XML types uses for values:
    /// <c>null</c>, <c>true</c>, numbers as JavaScript prints them, an object as
    /// <c>[object Object]</c>, a list joined with commas (null as nothing), a text or an XML type
    /// as its string, any other shared type as an object.
    /// </summary>
    public static string ToJsString(object? value) => value switch
    {
        null => "null",
        string s => s,
        bool b => b ? "true" : "false",
        double d => JsNumber(d),
        float f => JsNumber(f),
        decimal m => JsNumber((double)m),
        System.Numerics.BigInteger i => i.ToString(CultureInfo.InvariantCulture),
        IFormattable n when n is int or long or short or sbyte or byte or ushort or uint or ulong => JsNumber(Convert.ToDouble(n, CultureInfo.InvariantCulture)),
        YText or YXmlFragment => value.ToString()!,
        IList list => JsJoin(list),
        _ => "[object Object]",
    };

    private static string JsJoin(IList list)
    {
        // A list nested as deep as the limits of an update allow would otherwise end the process.
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        var sb = new StringBuilder();
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            if (list[i] != null)
            {
                sb.Append(ToJsString(list[i]));
            }
        }

        return sb.ToString();
    }

    /// <summary>ECMAScript's Number::toString: the shortest round-trip digits, positional from 1e-6 to below 1e21.</summary>
    private static string JsNumber(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsInfinity(d))
        {
            return d > 0 ? "Infinity" : "-Infinity";
        }

        if (d == 0)
        {
            return "0";
        }

        // Digits and the position of the decimal point (n) of the shortest round-trip form.
        var shortest = Math.Abs(d).ToString("R", CultureInfo.InvariantCulture);
        var e = shortest.IndexOf('E');
        var mantissa = e < 0 ? shortest : shortest[..e];
        var exponent = e < 0 ? 0 : int.Parse(shortest[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var digits = point < 0 ? mantissa : mantissa.Remove(point, 1);
        var n = (point < 0 ? mantissa.Length : point) + exponent;
        var lead = digits.Length - digits.TrimStart('0').Length;
        digits = digits.Trim('0');
        n -= lead;
        var k = digits.Length;

        string result;
        if (k <= n && n <= 21)
        {
            result = digits + new string('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            result = digits[..n] + "." + digits[n..];
        }
        else if (-6 < n && n <= 0)
        {
            result = "0." + new string('0', -n) + digits;
        }
        else
        {
            var exp = n - 1;
            result = digits[..1] + (k > 1 ? "." + digits[1..] : string.Empty) + "e" + (exp >= 0 ? "+" : "-") + Math.Abs(exp).ToString(CultureInfo.InvariantCulture);
        }

        return d < 0 ? "-" + result : result;
    }

    /// <summary>
    /// JavaScript's <c>===</c> on the values an attribute can hold: numbers by value (whatever their
    /// CLR type), strings, booleans and big integers by value, maps, lists and byte arrays by reference.
    /// </summary>
    public static bool StrictEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        switch (a)
        {
            case null:
                return false;
            case string sa:
                return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
            case bool ba:
                return b is bool bb && ba == bb;
            case System.Numerics.BigInteger ia:
                return b is System.Numerics.BigInteger ib && ia == ib;
        }

        return TryGetNumber(a, out var na) && TryGetNumber(b, out var nb) && na == nb;

        static bool TryGetNumber(object? value, out double number)
        {
            switch (value)
            {
                case int i: number = i; return true;
                case long l: number = l; return true;
                case double d: number = d; return true;
                case float f: number = f; return true;
                case short s: number = s; return true;
                case uint u: number = u; return true;
                case ulong ul: number = ul; return true;
                case byte b: number = b; return true;
                case sbyte sb: number = sb; return true;
                case ushort us: number = us; return true;
                case decimal m: number = (double)m; return true;
                default: number = 0; return false;
            }
        }
    }
}
