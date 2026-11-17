using System.Text;

namespace Ycs.Properties;

/// <summary>Where Ycs' observations of a script first differ from Yjs'.</summary>
internal sealed record Mismatch(int Step, string Op, string Primary, IReadOnlyList<string> Differences, string Yjs, string Ycs)
{
    /// <summary>
    /// Identifies a mismatch across scripts: the operation of the first differing step, the most
    /// severe difference, and which side threw (an exception in only one of them is a different bug).
    /// </summary>
    public string Signature => $"{Op} / {Primary}{Thrower}";

    private string Thrower =>
        Differences.Any(d => d.StartsWith(".err: yjs true", StringComparison.Ordinal)) ? " / yjs threw"
        : Differences.Any(d => d.StartsWith(".err: yjs null", StringComparison.Ordinal)) ? " / ycs threw"
        : "";

    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"first difference at step {Step} ({Op}), {Primary}:");
        foreach (var d in Differences.Take(12))
        {
            sb.AppendLine("  " + d);
        }

        return sb.ToString();
    }
}

/// <summary>Runs a script on Ycs and compares its observations with Yjs'.</summary>
internal static class Differential
{
    /// <summary>The categories of difference, most severe first: what a user sees, then what peers see, then bytes.</summary>
    public static readonly string[] Severity =
    [
        "fatal", "crash", "roots", "err", "r", "ev:obs", "ev:deep", "ev:um", "ev:subdocs", "ev:update", "ev:atx", "ev",
        "pend", "sv", "svb", "ds", "v1", "v2", "other",
    ];

    /// <summary>The canonical observations of a script by Ycs; an exception escaping the adapter is a "crash".</summary>
    public static string RunYcs(Dictionary<string, object> script)
    {
        try
        {
            return Json.Canonical(ScriptRun.Execute(script));
        }
        catch (Exception e)
        {
            return Json.Canonical(Json.Obj(("crash", e.GetType().Name + ": " + e.Message)));
        }
    }

    /// <summary>
    /// The first step where the observations differ, or null. With <paramref name="normalize"/>, the
    /// normalizations of <see cref="KnownFailures"/> are applied to both sides first, so that a
    /// pinned divergence that shows in every script does not hide the others.
    /// </summary>
    public static Mismatch Compare(string yjs, string ycs, Dictionary<string, object> script, bool normalize = true, KnownFailure normalizeExcept = null)
    {
        if (yjs == ycs)
        {
            return null;
        }

        var a = Json.Parse(yjs);
        var b = Json.Parse(ycs);
        if (a is not List<object> la || b is not List<object> lb)
        {
            var primary = a is Dictionary<string, object> da && da.ContainsKey("fatal") ? "fatal" : "crash";
            return new Mismatch(-1, "-", primary, [$"yjs: {Trim(yjs)}", $"ycs: {Trim(ycs)}"], yjs, ycs);
        }

        foreach (var step in la.Concat(lb).OfType<Dictionary<string, object>>())
        {
            WireIdentityOfPosition(step);
            if (normalize)
            {
                KnownFailures.Normalize(step, normalizeExcept);
            }
        }

        var steps = script.List("steps");
        for (var i = 0; i < Math.Max(la.Count, lb.Count); i++)
        {
            var x = i < la.Count ? la[i] : null;
            var y = i < lb.Count ? lb[i] : null;
            if (Json.Canonical(x) == Json.Canonical(y))
            {
                continue;
            }

            var differences = new List<string>();
            var categories = new HashSet<string>();
            Diff(x, y, "", differences, categories);
            var op = i < steps.Count ? ((Dictionary<string, object>)steps[i]).Str("op") : "end";
            var primary = Severity.First(categories.Contains);
            return new Mismatch(i, op, primary, differences, Json.Canonical(x), Json.Canonical(y));
        }

        return null;
    }

    /// <summary>
    /// The recorded form of a script's observations: normalized (as for <see cref="Compare"/>), with
    /// the events and states of every step but the last replaced by a hash of their canonical JSON
    /// ("he", "hs"), so that a few hundred fixtures stay small. Results and errors stay as they are.
    /// </summary>
    public static string Compact(string observations)
    {
        if (Json.Parse(observations) is not List<object> steps)
        {
            return observations;
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = (Dictionary<string, object>)steps[i];
            KnownFailures.Normalize(step);
            if (i < steps.Count - 1)
            {
                step["he"] = Hash(Json.Canonical(step.Get("ev")));
                step["hs"] = Hash(Json.Canonical(step.Get("st")));
                step.Remove("ev");
                step.Remove("st");
            }
        }

        return Json.Canonical(steps);
    }

    // Yjs' createRelativePositionFromTypeIndex sets the type (or the root name) next to the item, which its bytes drop;
    // RelativePosition.FromIndex sets what the bytes carry. The bytes ("b") are compared as they are, the JSON by the item.
    private static void WireIdentityOfPosition(Dictionary<string, object> step)
    {
        if (step.Get("r") is Dictionary<string, object> r && r.Get("j") is Dictionary<string, object> json && json.ContainsKey("item"))
        {
            json.Remove("type");
            json.Remove("tname");
        }
    }

    private static string Hash(string s) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16];

    /// <summary>Every differing step (after normalization), with its differences: for Explain.</summary>
    public static IEnumerable<string> AllDifferences(string yjs, string ycs)
    {
        if (Json.Parse(yjs) is not List<object> la || Json.Parse(ycs) is not List<object> lb)
        {
            yield return $"yjs {Trim(yjs)} | ycs {Trim(ycs)}";
            yield break;
        }

        foreach (var step in la.Concat(lb).OfType<Dictionary<string, object>>())
        {
            KnownFailures.Normalize(step);
        }

        for (var i = 0; i < Math.Max(la.Count, lb.Count); i++)
        {
            var differences = new List<string>();
            Diff(i < la.Count ? la[i] : null, i < lb.Count ? lb[i] : null, "", differences, new HashSet<string>());
            foreach (var d in differences)
            {
                yield return $"step {i}: {d}";
            }
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] + "..." : s;

    private static void Diff(object x, object y, string path, List<string> into, HashSet<string> categories)
    {
        if (Json.Canonical(x) == Json.Canonical(y))
        {
            return;
        }

        if (categories != null && path.EndsWith(".ev", StringComparison.Ordinal))
        {
            // The event list is one category (by the first record that differs); the lines show each record.
            categories.Add(Category(path, x, y));
            categories = null;
        }

        if (x is Dictionary<string, object> dx && y is Dictionary<string, object> dy)
        {
            foreach (var key in dx.Keys.Union(dy.Keys).Order(StringComparer.Ordinal))
            {
                Diff(dx.GetValueOrDefault(key), dy.GetValueOrDefault(key), path + "." + key, into, categories);
            }

            return;
        }

        if (x is List<object> lx && y is List<object> ly)
        {
            for (var i = 0; i < Math.Max(lx.Count, ly.Count); i++)
            {
                Diff(i < lx.Count ? lx[i] : "(missing)", i < ly.Count ? ly[i] : "(missing)", $"{path}[{i}]", into, categories);
            }

            return;
        }

        categories?.Add(Category(path, x, y));
        into.Add($"{path}: yjs {Trim(Json.Canonical(x))} | ycs {Trim(Json.Canonical(y))}");
    }

    private static string Category(string path, object x, object y)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return "other";
        }

        switch (parts[0])
        {
            case "st" when parts.Length >= 3:
                var field = parts[2].Split('[')[0];
                return Severity.Contains(field) ? field : "other";
            case "ev":
                var kind = EventKind(x, y);
                return kind == "other" ? "ev" : "ev:" + kind;
            case "r":
            case "err":
                return parts[0];
            case "he":
                return "ev";
            default:
                return parts[0].StartsWith("r[", StringComparison.Ordinal) ? "r" : "other";
        }
    }

    /// <summary>The kind of the first event record the two lists disagree on.</summary>
    private static string EventKind(object x, object y)
    {
        var lx = x as List<object> ?? [];
        var ly = y as List<object> ?? [];
        for (var i = 0; i < Math.Max(lx.Count, ly.Count); i++)
        {
            var ex = i < lx.Count ? lx[i] as Dictionary<string, object> : null;
            var ey = i < ly.Count ? ly[i] as Dictionary<string, object> : null;
            if (Json.Canonical(ex) == Json.Canonical(ey))
            {
                continue;
            }

            var w = (ex ?? ey)?.Str("w") ?? "?";
            return w switch
            {
                _ when w.StartsWith("obs:", StringComparison.Ordinal) || w.StartsWith("o:", StringComparison.Ordinal) => "obs",
                _ when w.StartsWith("deep", StringComparison.Ordinal) => "deep",
                "u1" or "u2" => "update",
                "um+" or "um-" or "umclear" => "um",
                "atx" => "atx",
                "subdocs" => "subdocs",
                _ => "other",
            };
        }

        return "other";
    }
}
