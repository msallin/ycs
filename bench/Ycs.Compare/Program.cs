using System.Text.Json.Nodes;
using Ycs.Compare;

const string Usage = """
    usage:
      Ycs.Compare suite --trace <automerge-paper.json.gz> --out <dir> [--work <dir>] [--passes 3] [--runs 5] [--only <name part>] [--no-hostile]
                        [--carry-yrs <results dir> --carry-yrs-work <work dir> [--yrs-controls <case,case>]]
      Ycs.Compare report <dir>                      re-render <dir>/tables.md from <dir>/results.json
      Ycs.Compare child <ycs|yrs> <case> <fixtures dir> <out dir> <runs>
      Ycs.Compare hostile <ycs|yrs> <case>
      Ycs.Compare list
    """;

if (args.Length == 0)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

string Option(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

switch (args[0])
{
    case "suite":
        return Suite.Run(
            Option("--trace", null) ?? throw new ArgumentException("--trace is required"),
            Option("--out", null) ?? throw new ArgumentException("--out is required"),
            Option("--work", Path.Combine(Path.GetTempPath(), "ycs-compare-work")),
            int.Parse(Option("--passes", "3")),
            int.Parse(Option("--runs", "5")),
            Option("--only", null),
            args.Contains("--no-hostile"),
            Option("--carry-yrs", null),
            Option("--carry-yrs-work", null),
            Option("--yrs-controls", null));
    case "report":
        var results = JsonNode.Parse(File.ReadAllText(Path.Combine(args[1], "results.json")))!.AsObject();
        File.WriteAllText(Path.Combine(args[1], "tables.md"), Report.Render(results));
        return 0;
    case "child":
        return Child.Run(args[1], args[2], args[3], args[4], int.Parse(args[5]));
    case "hostile":
        return Hostile.Run(args[1], args[2]);
    case "list":
        foreach (var c in Catalog.All)
        {
            Console.WriteLine($"{c.Name,-24} {c.What}");
        }

        foreach (var h in Hostile.All)
        {
            Console.WriteLine($"hostile {h.Name,-38} {h.Source}");
        }

        return 0;
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}
