namespace Ycs.Properties;

/// <summary>
/// Delta debugging on scripts: removes steps (in halving chunks, then one by one), clients,
/// roots, the operations of transactions and the elements of every list, and simplifies values
/// (shorter strings, smaller numbers, fewer attributes), keeping each change while the script
/// still fails. Scripts are total (see Generator), so every candidate is a valid script.
/// </summary>
internal static class Shrinker
{
    // Keys whose values name things (or are names); changing them makes noise, not progress.
    private static readonly HashSet<string> Fixed = new(StringComparer.Ordinal) { "op", "c", "to", "from", "path", "id", "prev", "name", "kind", "clients", "$t", "$doc", "$f", "$n", "$i", "n", "g", "timeout", "via", "key", "u" };

    // Keys that may be left out altogether.
    private static readonly string[] Optional = ["attrs", "sanitize", "origins", "prev", "assoc", "origin", "diff", "compact", "a", "v", "s"];

    public static Dictionary<string, object> Shrink(Dictionary<string, object> script, Func<Dictionary<string, object>, bool> fails, int budget = 4000)
    {
        var current = script;
        var calls = 0;
        bool Try(Dictionary<string, object> candidate)
        {
            if (calls >= budget)
            {
                return false;
            }

            calls++;
            if (!fails(candidate))
            {
                return false;
            }

            current = candidate;
            return true;
        }

        var progress = true;
        while (progress && calls < budget)
        {
            progress = false;

            // Steps, in chunks.
            var steps = current.List("steps");
            for (var chunk = Math.Max(1, steps.Count / 2); chunk >= 1; chunk /= 2)
            {
                for (var start = 0; start < current.List("steps").Count;)
                {
                    var candidate = WithSteps(current, current.List("steps").Where((_, i) => i < start || i >= start + chunk).ToList());
                    if (Try(candidate))
                    {
                        progress = true;
                    }
                    else
                    {
                        start += chunk;
                    }
                }

                if (chunk == 1)
                {
                    break;
                }
            }

            // Clients.
            for (var k = current.List("clients").Count - 1; k >= 0 && current.List("clients").Count > 1; k--)
            {
                if (k < current.List("clients").Count && Try(RemoveClient(current, k)))
                {
                    progress = true;
                }
            }

            // Everything else, one simplification at a time (restarting after each success).
            foreach (var candidate in Simplifications(current, null))
            {
                if (Try((Dictionary<string, object>)candidate))
                {
                    progress = true;
                    break;
                }

                if (calls >= budget)
                {
                    break;
                }
            }
        }

        return current;
    }

    private static Dictionary<string, object> WithSteps(Dictionary<string, object> script, List<object> steps)
    {
        var copy = new Dictionary<string, object>(script) { ["steps"] = steps };
        return copy;
    }

    /// <summary>The script without client <paramref name="k"/>: its steps and deliveries go, later indexes shift down.</summary>
    public static Dictionary<string, object> RemoveClient(Dictionary<string, object> script, int k)
    {
        var copy = (Dictionary<string, object>)Json.Clone(script);
        copy.List("clients").RemoveAt(k);
        var steps = new List<object>();
        foreach (Dictionary<string, object> step in copy.List("steps"))
        {
            if (Refers(step, "c", k) || Refers(step, "to", k) || (step.Str("op") != "merged" && Refers(step, "from", k)))
            {
                continue;
            }

            Shift(step, "c", k);
            Shift(step, "to", k);
            if (step.Str("op") == "merged")
            {
                var pairs = step.List("from").Cast<List<object>>().Where(p => ((JsonNumber)p[0]).Long != k).ToList();
                foreach (var p in pairs)
                {
                    var i = ((JsonNumber)p[0]).Long;
                    if (i > k)
                    {
                        p[0] = new JsonNumber((i - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                }

                step["from"] = pairs.Cast<object>().ToList();
            }
            else
            {
                Shift(step, "from", k);
            }

            // An update delivered inside a transaction: dropped with its sender, else shifted as the steps are.
            if (step.Get("ops") is List<object> ops)
            {
                ops.RemoveAll(o => o is Dictionary<string, object> op && op.Str("op") == "deliver" && Refers(op, "from", k));
                foreach (var op in ops.OfType<Dictionary<string, object>>().Where(o => o.Str("op") == "deliver"))
                {
                    Shift(op, "from", k);
                    Shift(op, "to", k);
                }
            }

            steps.Add(step);
        }

        copy["steps"] = steps;
        return copy;
    }

    private static bool Refers(Dictionary<string, object> step, string key, int k) => step.Get(key) is JsonNumber n && n.Long == k;

    private static void Shift(Dictionary<string, object> step, string key, int k)
    {
        if (step.Get(key) is JsonNumber n && n.Long > k)
        {
            step[key] = new JsonNumber((n.Long - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Every script one simplification away from <paramref name="node"/> (lazily, structure first).</summary>
    private static IEnumerable<object> Simplifications(object node, string key)
    {
        switch (node)
        {
            case Dictionary<string, object> d:
                foreach (var optional in Optional)
                {
                    // A format without attributes throws (after splitting the text, in Yjs as in Ycs): a different failure.
                    if (d.ContainsKey(optional) && !(optional == "attrs" && d.Str("op") == "format"))
                    {
                        var without = new Dictionary<string, object>(d);
                        without.Remove(optional);
                        yield return without;
                    }
                }

                // A transaction of one operation: the operation alone.
                if (d.Str("op") == "transact" && d.List("ops") is { Count: 1 } single)
                {
                    var alone = new Dictionary<string, object>((Dictionary<string, object>)single[0]) { ["c"] = d["c"] };
                    yield return alone;
                }

                foreach (var k in d.Keys.ToList())
                {
                    if (Fixed.Contains(k) && !(k == "clients"))
                    {
                        continue;
                    }

                    foreach (var c in Simplifications(d[k], k))
                    {
                        yield return new Dictionary<string, object>(d) { [k] = c };
                    }
                }

                break;
            case List<object> l when key == "clients":
                for (var i = 0; i < l.Count; i++)
                {
                    if (l[i] is Dictionary<string, object> client && client.Get("gc") is true)
                    {
                        var copy = new List<object>(l) { [i] = new Dictionary<string, object>(client) { ["gc"] = false } };
                        yield return copy;
                    }
                }

                break;
            case List<object> l:
                if (key != "path")
                {
                    for (var i = 0; i < l.Count; i++)
                    {
                        var copy = new List<object>(l);
                        copy.RemoveAt(i);
                        yield return copy;
                    }
                }

                for (var i = 0; i < l.Count; i++)
                {
                    foreach (var c in Simplifications(l[i], key == "steps" ? null : key))
                    {
                        yield return new List<object>(l) { [i] = c };
                    }
                }

                break;
            case string s when s.Length > 1:
                // Never between the two halves of a surrogate pair: that would make a lone surrogate.
                var cut = s.Length / 2;
                if (char.IsHighSurrogate(s[cut - 1]) && char.IsLowSurrogate(s[cut]))
                {
                    cut--;
                }

                if (cut > 0)
                {
                    yield return s[..cut];
                }

                yield return "a";
                break;
            case string s when s.Length == 1 && s != "a" && key is "text" or "insert" or "s":
                yield return "a";
                break;
            case JsonNumber n when n.IsInteger && n.Long != 0:
                yield return new JsonNumber("0");
                if (Math.Abs(n.Long) > 1)
                {
                    yield return new JsonNumber((n.Long / 2).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                break;
            case JsonNumber n when !n.IsInteger:
                yield return new JsonNumber("1");
                break;
        }
    }
}
