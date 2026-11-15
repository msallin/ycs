using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ycs.Compare;

/// <summary>
/// The driver: prepares the fixtures (in <c>workDir</c>, with what each run left for the checks), runs every workload on both libraries in a child process each, in
/// <c>passes</c> passes (the library that goes first alternates), checks every result, runs the hostile updates,
/// and writes <c>results.json</c> and <c>tables.md</c> into the output directory.
/// </summary>
internal static class Suite
{
    private const long WorkloadRssLimit = 8L << 30;
    private const long HostileRssLimit = 4L << 30;

    /// <param name="carryYrs">
    /// When set, YDotNet runs only the workloads in <paramref name="yrsControls"/> (to see how far the machine drifted):
    /// for the others its measurements are taken from <c>carryYrs/results.json</c> (marked carried) and the content
    /// and state it wrote then from <paramref name="carryYrsWork"/>'s pass directories, for the checks.
    /// </param>
    public static int Run(string tracePath, string outDir, string workDir, int passes, int runs, string only, bool skipHostile,
        string carryYrs = null, string carryYrsWork = null, string yrsControls = null)
    {
        Directory.CreateDirectory(outDir);
        var fixtures = Path.Combine(workDir, "fixtures");
        Prepare(tracePath, fixtures);
        var f = new Fixtures(fixtures);
        var cases = Catalog.All.Where(c => only == null || only.Split(',').Any(o => c.Name.StartsWith(o, StringComparison.Ordinal))).ToList();

        var results = new JsonObject
        {
            ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
            ["machine"] = Machine(),
            ["passes"] = passes,
            ["runs"] = runs,
            ["inputs"] = Inputs.Hash(),
        };
        var measurements = new JsonArray();
        var checks = new JsonObject();
        results["measurements"] = measurements;
        results["checks"] = checks;
        var expected = cases.ToDictionary(c => c.Name, c => c.Expected?.Invoke(f));
        var controls = yrsControls?.Split(',').ToHashSet(StringComparer.Ordinal) ?? [];
        var carried = carryYrs == null ? null : JsonNode.Parse(File.ReadAllText(Path.Combine(carryYrs, "results.json")))!["measurements"]!.AsArray()
            .Select(x => x!.AsObject()).Where(x => (string)x["lib"] == "yrs").ToList();
        bool Carried(Case c) => carried != null && !controls.Contains(c.Name);
        if (carried != null)
        {
            results["yrsCarriedFrom"] = carryYrs;
            results["yrsControls"] = new JsonArray(controls.Select(x => (JsonNode)x).ToArray());
        }

        for (var pass = 1; pass <= passes; pass++)
        {
            var passDir = Path.Combine(workDir, $"pass-{pass}");
            Directory.CreateDirectory(passDir);
            foreach (var c in cases)
            {
                var libs = pass % 2 == 1 ? new[] { "ycs", "yrs" } : ["yrs", "ycs"];
                if (Carried(c))
                {
                    libs = ["ycs"];
                    foreach (var old in carried.Where(x => (string)x["case"] == c.Name && (int)x["pass"] == pass))
                    {
                        var copy = old.DeepClone().AsObject();
                        copy["carried"] = true;
                        measurements.Add(copy);
                    }

                    var stem = c.Name.Replace('/', '_') + ".yrs.";
                    foreach (var ext in new[] { "content", "v1" })
                    {
                        var from = Path.Combine(carryYrsWork, $"pass-{pass}", stem + ext);
                        if (File.Exists(from))
                        {
                            File.Copy(from, Path.Combine(passDir, stem + ext), overwrite: true);
                        }
                    }
                }

                foreach (var lib in libs)
                {
                    var (json, failure) = Spawn(["child", lib, c.Name, fixtures, passDir, runs.ToString(CultureInfo.InvariantCulture)], TimeSpan.FromMinutes(20), WorkloadRssLimit);
                    var m = json ?? new JsonObject { ["lib"] = lib, ["case"] = c.Name, ["failure"] = failure };
                    m["pass"] = pass;
                    measurements.Add(m);
                    Console.Error.WriteLine($"pass {pass} {c.Name,-24} {lib}  {(json == null ? "FAILED " + failure : Summary(json))}");
                }

                var problems = Check(c, passDir, expected[c.Name]);
                var list = checks[c.Name] as JsonArray ?? [];
                checks[c.Name] = list;
                list.Add(problems.Count == 0 ? "ok" + IdenticalStates(c, passDir) : string.Join("; ", problems));
                if (problems.Count > 0)
                {
                    Console.Error.WriteLine($"  CHECK {c.Name}: {string.Join("; ", problems)}");
                }
            }
        }

        if (!skipHostile)
        {
            results["hostile"] = RunHostile();
        }

        File.WriteAllText(Path.Combine(outDir, "results.json"), results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(outDir, "tables.md"), Report.Render(results));
        Console.Error.WriteLine($"wrote {Path.Combine(outDir, "results.json")} and tables.md");
        return 0;
    }

    private static string Summary(JsonObject m)
    {
        var ms = m["ms"]!.AsArray().Select(x => x!.GetValue<double>()).Order().ToList();
        return $"median {ms[ms.Count / 2],9:F1} ms  load {m["load0"]}->{m["load1"]}";
    }

    /// <summary>Replays the trace on both libraries and writes what the apply and encode workloads read.</summary>
    private static void Prepare(string tracePath, string dir)
    {
        Directory.CreateDirectory(dir);
        File.Copy(tracePath, Path.Combine(dir, "trace.json.gz"), overwrite: true);
        var trace = Inputs.LoadTrace(tracePath);
        var ycs = YcsCases.Record(trace);
        var yrs = YrsCases.Record(trace);
        if (ycs.Text != trace.EndContent || yrs.Text != trace.EndContent)
        {
            throw new InvalidOperationException("a replay of the trace does not end in its endContent");
        }

        if (!ycs.HalfVector.AsSpan().SequenceEqual(yrs.HalfVector))
        {
            throw new InvalidOperationException("the two libraries' state vectors halfway through the trace differ");
        }

        File.WriteAllBytes(Path.Combine(dir, "trace.ycs.v1"), ycs.V1);
        File.WriteAllBytes(Path.Combine(dir, "trace.ycs.v2"), ycs.V2);
        File.WriteAllBytes(Path.Combine(dir, "trace.yrs.v1"), yrs.V1);
        File.WriteAllBytes(Path.Combine(dir, "trace.yrs.v2"), yrs.V2);
        File.WriteAllBytes(Path.Combine(dir, "trace.half.sv"), ycs.HalfVector);
        Inputs.WriteUpdates(Path.Combine(dir, "updates.ycs.v1"), ycs.Updates);
        Inputs.WriteUpdates(Path.Combine(dir, "updates.yrs.v1"), yrs.Updates);
        Console.Error.WriteLine(
            $"trace: {trace.Edits.Length} edits in {trace.Transactions} transactions, {trace.EndContent.Length} characters; " +
            $"state v1 ycs {ycs.V1.Length} / yrs {yrs.V1.Length} bytes, v2 ycs {ycs.V2.Length} / yrs {yrs.V2.Length}; " +
            $"updates ycs {ycs.Updates.Count} ({ycs.Updates.Sum(u => (long)u.Length)} bytes) / yrs {yrs.Updates.Count} ({yrs.Updates.Sum(u => (long)u.Length)} bytes)");
    }

    /// <summary>
    /// Both libraries read the same content, the model's where there is one, and each library's state decodes in
    /// both libraries to the same root content.
    /// </summary>
    private static List<string> Check(Case c, string passDir, string expected)
    {
        var problems = new List<string>();
        var stem = Path.Combine(passDir, c.Name.Replace('/', '_'));
        string Read(string lib, string ext) => File.Exists($"{stem}.{lib}.{ext}") ? File.ReadAllText($"{stem}.{lib}.{ext}") : null;
        var ycs = Read("ycs", "content");
        var yrs = Read("yrs", "content");
        if (ycs == null || yrs == null)
        {
            problems.Add("a run produced no content");
            return problems;
        }

        if (ycs != yrs)
        {
            problems.Add("the libraries' contents differ");
        }

        if (expected != null && ycs != expected)
        {
            problems.Add("ycs' content is not the model's");
        }

        if (expected != null && yrs != expected)
        {
            problems.Add("YDotNet's content is not the model's");
        }

        var ycsState = File.ReadAllBytes($"{stem}.ycs.v1");
        var yrsState = File.ReadAllBytes($"{stem}.yrs.v1");
        var decoded = new Dictionary<string, string>();
        foreach (var (name, state) in new[] { ("ycs state", ycsState), ("YDotNet state", yrsState) })
        {
            try
            {
                decoded[$"{name} in ycs"] = YcsCases.ContentOf(state, c.Root);
            }
            catch (Exception e)
            {
                problems.Add($"{name} does not decode in ycs: {e.GetType().Name}");
            }

            try
            {
                decoded[$"{name} in YDotNet"] = YrsCases.ContentOf(state, c.Root);
            }
            catch (Exception e)
            {
                problems.Add($"{name} does not decode in YDotNet: {e.GetType().Name}");
            }
        }

        if (decoded.Values.Distinct().Count() > 1)
        {
            problems.Add("decoded states differ: " + string.Join(", ", decoded.GroupBy(kv => kv.Value).Select(g => string.Join("+", g.Select(kv => kv.Key)))));
        }

        return problems;
    }

    /// <summary>Not a check: whether the two libraries wrote the same v1 bytes for the same content.</summary>
    private static string IdenticalStates(Case c, string passDir)
    {
        var stem = Path.Combine(passDir, c.Name.Replace('/', '_'));
        return File.ReadAllBytes($"{stem}.ycs.v1").AsSpan().SequenceEqual(File.ReadAllBytes($"{stem}.yrs.v1")) ? "; v1 identical" : "; v1 differs";
    }

    private static JsonArray RunHostile()
    {
        var all = new JsonArray();
        foreach (var h in Hostile.All)
        {
            var row = new JsonObject { ["case"] = h.Name, ["source"] = h.Source, ["bytes"] = h.Update.Length, ["v2"] = h.V2 };
            foreach (var lib in new[] { "ycs", "yrs" })
            {
                var (json, failure) = Spawn(["hostile", lib, h.Name], TimeSpan.FromSeconds(30), HostileRssLimit);
                row[lib] = json ?? new JsonObject { ["outcome"] = failure };
                Console.Error.WriteLine($"hostile {h.Name,-38} {lib}  {row[lib]!.ToJsonString()}");
            }

            all.Add(row);
        }

        return all;
    }

    /// <summary>
    /// Runs this program with <paramref name="args"/> and parses the JSON it prints. A child that exceeds the time
    /// or the resident-set limit is killed; a child that dies says how.
    /// </summary>
    private static (JsonObject Json, string Failure) Spawn(string[] args, TimeSpan timeout, long rssLimit)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        {
            psi.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
        }

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        var sw = Stopwatch.StartNew();
        long peak = 0;
        string killed = null;
        while (!p.WaitForExit(50))
        {
            peak = Math.Max(peak, Rss(p.Id));
            if (peak > rssLimit)
            {
                killed = $"killed at {peak >> 20} MiB resident (limit {rssLimit >> 20} MiB) after {sw.Elapsed.TotalSeconds:F1} s";
            }
            else if (sw.Elapsed > timeout)
            {
                killed = $"hang: killed after {timeout.TotalSeconds:F0} s";
            }

            if (killed != null)
            {
                p.Kill();
                p.WaitForExit();
                return (null, killed);
            }
        }

        var output = stdout.Result;
        var errors = stderr.Result;
        if (p.ExitCode != 0)
        {
            var how = p.ExitCode > 128 ? $"died with signal {p.ExitCode - 128} ({Signal(p.ExitCode - 128)})" : $"exit code {p.ExitCode}";
            var first = errors.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            return (null, $"crash: {how}: {(first.Length > 160 ? first[..160] : first)}");
        }

        return (JsonNode.Parse(output.Trim().Split('\n')[^1])!.AsObject(), null);
    }

    private static string Signal(int n) => n switch { 6 => "SIGABRT", 9 => "SIGKILL", 11 => "SIGSEGV", 4 => "SIGILL", 7 => "SIGBUS", _ => "?" };

    private static long Rss(int pid)
    {
        try
        {
            var line = File.ReadLines($"/proc/{pid}/status").FirstOrDefault(l => l.StartsWith("VmRSS:", StringComparison.Ordinal));
            return line == null ? 0 : long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], CultureInfo.InvariantCulture) * 1024;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static JsonObject Machine()
    {
        string Grep(string file, string key) =>
            File.ReadLines(file).FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "?";
        var os = File.ReadLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))?.Split('=', 2)[1].Trim('"') ?? "?";
        return new JsonObject
        {
            ["cpu"] = Grep("/proc/cpuinfo", "model name"),
            ["logicalCores"] = Environment.ProcessorCount,
            ["memory"] = Grep("/proc/meminfo", "MemTotal"),
            ["os"] = $"{os}, kernel {Environment.OSVersion.Version}",
            ["runtime"] = RuntimeInformation.FrameworkDescription,
            ["ydotnet"] = typeof(YDotNet.Document.Doc).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            ["loadAtStart"] = File.ReadAllText("/proc/loadavg").Trim(),
        };
    }
}
