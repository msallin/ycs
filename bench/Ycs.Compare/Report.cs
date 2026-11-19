using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Ycs.Compare;

/// <summary>Renders <c>results.json</c> as the markdown tables of README.md.</summary>
internal static class Report
{
    public static string Render(JsonObject results)
    {
        var sb = new StringBuilder();
        var m = results["machine"]!.AsObject();
        sb.AppendLine(Invariant($"Run {results["date"]}: {results["passes"]} passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then {results["runs"]} timed runs per process (a heavy workload: 1 run, no warm-up); inputs {results["inputs"]}. Children: {results["children"]}."));
        sb.AppendLine();
        sb.AppendLine(Invariant($"Machine: {m["cpu"]}, {m["logicalCores"]} logical cores, {m["memory"]} RAM, {m["os"]}, {m["runtime"]}, YDotNet {m["ydotnet"]}. Load average at start: {m["loadAtStart"]}."));
        sb.AppendLine();

        if (results["yrsCarriedFrom"] is { } carriedFrom)
        {
            sb.AppendLine(Invariant($"YDotNet was not run again except for {string.Join(", ", results["yrsControls"]!.AsArray().Select(x => $"`{x}`"))}: its other measurements, marked (carried), and the content and states the checks read are those of {carriedFrom}."));
            sb.AppendLine();
        }

        var all = results["measurements"]!.AsArray().Select(x => x!.AsObject()).ToList();
        var checks = results["checks"]!.AsObject();
        var groups = Catalog.All.Where(c => all.Any(x => (string)x["case"] == c.Name)).GroupBy(c => c.Group);

        sb.AppendLine("Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.");
        sb.AppendLine();
        sb.AppendLine("| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |");
        sb.AppendLine("| --- | ---: | ---: | --- | ---: | --- |");
        foreach (var g in groups)
        {
            foreach (var c in g)
            {
                var ycs = Stats.Of(all, c.Name, "ycs");
                var yrs = Stats.Of(all, c.Name, "yrs");
                var verdict = checks[c.Name]!.AsArray().Select(x => (string)x).ToList();
                var ok = verdict.All(v => v.StartsWith("ok", StringComparison.Ordinal));
                var cell = ok ? "equal" + (verdict.All(v => v.EndsWith("v1 identical", StringComparison.Ordinal)) ? ", v1 bytes identical" : verdict.Any(v => v.EndsWith("v1 differs", StringComparison.Ordinal)) ? ", v1 bytes differ" : string.Empty) : string.Join("; ", verdict.Where(v => !v.StartsWith("ok", StringComparison.Ordinal)).Distinct());
                var mark = all.Any(x => (string)x["case"] == c.Name && (string)x["lib"] == "yrs" && x["carried"] is not null) ? " (carried)" : string.Empty;
                sb.AppendLine(Invariant($"| `{c.Name}` | {ycs?.TimeCell ?? ycs?.Failure} | {yrs?.TimeCell ?? yrs?.Failure}{mark} | {(ok && ycs?.Failure == null && yrs?.Failure == null ? Faster(ycs, yrs) : "not counted")} | {ycs?.ColdCell} / {yrs?.ColdCell} | {cell} |"));
            }
        }

        sb.AppendLine();
        sb.AppendLine("Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.");
        sb.AppendLine();
        sb.AppendLine("| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var g in groups)
        {
            foreach (var c in g)
            {
                var ycs = Stats.Of(all, c.Name, "ycs");
                var yrs = Stats.Of(all, c.Name, "yrs");
                if (ycs?.Failure != null || yrs?.Failure != null)
                {
                    sb.AppendLine(Invariant($"| `{c.Name}` | | | | | | |"));
                    continue;
                }

                sb.AppendLine(Invariant($"| `{c.Name}` | {Mib(ycs.Alloc)} | {Mib(yrs.Alloc)} | {Mib(ycs.HeldManaged)} + {Mib(ycs.HeldNative)} = {Mib(ycs.HeldManaged + ycs.HeldNative)} | {Mib(yrs.HeldManaged)} + {Mib(yrs.HeldNative)} = {Mib(yrs.HeldManaged + yrs.HeldNative)} | {ycs.V1:N0} / {yrs.V1:N0} | {ycs.V2:N0} / {yrs.V2:N0} |"));
            }
        }

        if (results["hostile"] is JsonArray hostile)
        {
            sb.AppendLine();
            sb.AppendLine("Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.");
            sb.AppendLine();
            sb.AppendLine("| case | bytes | ycs | YDotNet |");
            sb.AppendLine("| --- | ---: | --- | --- |");
            foreach (var h in hostile.Select(x => x!.AsObject()))
            {
                sb.AppendLine(Invariant($"| `{h["case"]}`{((bool)h["v2"]! ? " (v2)" : string.Empty)} | {(int)h["bytes"]!:N0} | {Outcome(h["ycs"]!.AsObject())} | {Outcome(h["yrs"]!.AsObject())} |"));
            }
        }

        return sb.ToString();
    }

    private static string Outcome(JsonObject o)
    {
        if (o["ms"] == null)
        {
            return $"**{o["outcome"]}**";
        }

        var readable = (string)o["readable"];
        var unchanged = (string)o["unchanged"];
        var outcome = (string)o["outcome"];
        var state = readable != "yes" ? $"document unreadable ({readable})" : outcome == "applied" ? "readable" : unchanged == "yes" ? "unchanged" : "**changed**";
        return Invariant($"{outcome}; {state}; {(double)o["ms"]!:F0} ms, {(long)o["peakRssMiB"]!} MiB");
    }

    private static string Faster(Stats ycs, Stats yrs)
    {
        if (ycs?.Failure != null || yrs?.Failure != null)
        {
            return "-";
        }

        var ratio = yrs.Median / ycs.Median;
        return ratio >= 1 ? Invariant($"ycs {ratio:F1}×") : Invariant($"YDotNet {1 / ratio:F1}×");
    }

    private static string Mib(double bytes) => Invariant($"{bytes / 1048576.0:F1}");

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private sealed record Stats(double Median, double Min, double Max, double PassSpread, double Cold, double Alloc, double HeldManaged, double HeldNative, long V1, long V2, string Failure)
    {
        public string ColdCell => Failure != null ? "-" : Fmt(Cold);

        public string TimeCell => Failure != null ? null : Invariant($"{Fmt(Median)} ({Fmt(Min)}–{Fmt(Max)}, ±{PassSpread:F0}%)");

        private static string Fmt(double ms) => ms >= 100 ? ms.ToString("F0", CultureInfo.InvariantCulture) : ms.ToString("F1", CultureInfo.InvariantCulture);

        public static Stats Of(List<JsonObject> all, string name, string lib)
        {
            var rows = all.Where(x => (string)x["case"] == name && (string)x["lib"] == lib).ToList();
            if (rows.Count == 0)
            {
                return null;
            }

            var failed = rows.FirstOrDefault(x => x["failure"] != null);
            if (failed != null)
            {
                return new Stats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, $"**{failed["failure"]}**");
            }

            var ms = rows.SelectMany(x => x["ms"]!.AsArray().Select(v => (double)v!)).ToList();
            var passMedians = rows.Select(x => MedianOf([.. x["ms"]!.AsArray().Select(v => (double)v!)])).ToList();
            var mid = MedianOf(passMedians);
            var spread = 100 * Math.Max(passMedians.Max() - mid, mid - passMedians.Min()) / mid;
            return new Stats(
                MedianOf(ms),
                ms.Min(),
                ms.Max(),
                spread,
                MedianOf([.. rows.Select(x => (double?)x["coldMs"] ?? 0)]),
                MedianOf([.. rows.SelectMany(x => x["alloc"]!.AsArray().Select(v => (double)v!))]),
                MedianOf([.. rows.Select(x => (double)x["heldManaged"]!)]),
                MedianOf([.. rows.Select(x => (double)x["heldNative"]!)]),
                (long)rows[0]["v1"]!,
                (long)rows[0]["v2"]!,
                null);
        }

        private static double MedianOf(List<double> values)
        {
            values.Sort();
            var n = values.Count;
            return n % 2 == 1 ? values[n / 2] : (values[(n / 2) - 1] + values[n / 2]) / 2;
        }
    }
}
