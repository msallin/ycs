using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Corpus;

/// <summary>
/// The differential undo fuzz: replays the sessions interop/corpus/undo-gen.mjs ran in real Yjs, with
/// the same setup and the same steps (remote changes as the bytes Yjs produced), and compares after
/// every step the document state, what Undo and Redo returned, the sizes of both stacks, and the
/// UndoManager events in order. Each script stops at its first divergence. The pinned set is
/// undo/undo-differential.json.gz; <c>YCS_UNDO_FUZZ=&lt;file&gt;</c> replays a larger one written by the
/// generator instead.
/// </summary>
[TestClass]
public class UndoDifferentialTests
{
    // Scripts that diverge for a known reason outside the UndoManager: seed -> why.
    private static readonly Dictionary<int, string> KnownDivergences = new()
    {
    };

    public TestContext TestContext { get; set; }

    [TestMethod]
    public void Scripts_EveryStep_MatchesYjs()
    {
        var path = Environment.GetEnvironmentVariable("YCS_UNDO_FUZZ");
        using var json = path != null
            ? JsonDocument.Parse(File.ReadAllBytes(path))
            : JsonDocument.Parse(new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "undo", "undo-differential.json.gz")), CompressionMode.Decompress));

        var scripts = json.RootElement.GetProperty("scripts").EnumerateArray().ToList();
        var divergences = scripts.Select(UndoReplay.Run).Where(d => d != null).ToList();
        var unexpected = divergences.Where(d => !KnownDivergences.ContainsKey(d.Seed)).ToList();
        var fixedKnown = KnownDivergences.Keys.Where(seed => path == null && divergences.All(d => d.Seed != seed)).ToList();

        TestContext.WriteLine($"{scripts.Count} scripts, {divergences.Count} diverge ({unexpected.Count} not known)");
        foreach (var group in divergences.GroupBy(d => d.Kind).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            TestContext.WriteLine($"  first divergence {group.Key}: {group.Count()}");
        }

        Assert.IsEmpty(unexpected, string.Join(Environment.NewLine + Environment.NewLine, unexpected.Take(int.TryParse(Environment.GetEnvironmentVariable("YCS_UNDO_SHOW"), out var show) ? show : 3)));
        Assert.IsEmpty(fixedKnown, $"listed in KnownDivergences but no longer diverging: {string.Join(", ", fixedKnown)}");
    }

    /// <summary>
    /// The 20 scripts of <c>node undo-gen.mjs 20000 60</c> that diverged while deleted structs merged to
    /// the left less far than in Yjs 13.6 (undo/undo-gc-merge.json.gz, taken from that run).
    /// </summary>
    [TestMethod]
    public void GcMergeScripts_EveryStep_MatchesYjs()
    {
        using var json = JsonDocument.Parse(new GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "undo", "undo-gc-merge.json.gz")), CompressionMode.Decompress));

        var divergences = json.RootElement.GetProperty("scripts").EnumerateArray().Select(UndoReplay.Run).Where(d => d != null).ToList();

        Assert.IsEmpty(divergences, string.Join(Environment.NewLine + Environment.NewLine, divergences.Take(3)));
    }
}

internal sealed record UndoDivergence(int Seed, string Config, int Step, string Kind, string Message, IReadOnlyList<string> History)
{
    public override string ToString() =>
        $"seed {Seed} {Config} step {Step}: {Message}{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", History.TakeLast(30));
}

/// <summary>Replays one script of undo-gen.mjs; the cases of <see cref="Apply"/> are those of its apply().</summary>
internal static class UndoReplay
{
    private sealed class ClassOrigin
    {
    }

    public static UndoDivergence Run(JsonElement script)
    {
        var seed = script.GetProperty("seed").GetInt32();
        var config = script.GetProperty("config");
        var doc = new YDoc(new YDocOptions { ClientId = 1 });
        var roots = new Dictionary<string, YType>
        {
            ["t"] = doc.GetText("t"),
            ["m"] = doc.GetMap("m"),
            ["a"] = doc.GetArray("a"),
            ["x"] = doc.GetXmlFragment("x"),
        };

        var options = new UndoManagerOptions
        {
            CaptureTimeout = config.GetProperty("timeout").GetDouble() > 0 ? 1_000_000_000 : 0,
            IgnoreRemoteMapChanges = config.GetProperty("ignoreRemoteMapChanges").GetBoolean(),
            DeleteFilter = config.GetProperty("deleteFilter").GetInt32() switch
            {
                1 => item => item.Kind != ItemKind.Type,
                2 => item => item.ParentSub != "y",
                _ => null,
            },
            CaptureTransaction = config.GetProperty("captureTransaction").GetBoolean() ? tr => tr.Origin is not "nocap" : null,
        };

        if (config.GetProperty("tracked").ValueKind == JsonValueKind.Array)
        {
            options.TrackedOrigins = config.GetProperty("tracked").EnumerateArray()
                .Select(o => o.ValueKind == JsonValueKind.Null ? null : o.GetString() == "cls" ? typeof(ClassOrigin) : (object)o.GetString())
                .ToHashSet();
        }

        var scope = config.GetProperty("scope");
        var um = scope.ValueKind == JsonValueKind.Null
            ? new UndoManager(doc, options)
            : new UndoManager(scope.EnumerateArray().Select(n => (object)roots[n.GetString()]).ToList(), options);

        var events = new List<string>();
        um.StackItemAdded += (_, e) => events.Add($"added {Name(e.Type)} {e.ChangedParentTypes.Count}");
        um.StackItemUpdated += (_, e) => events.Add($"updated {Name(e.Type)} {e.ChangedParentTypes.Count}");
        um.StackItemPopped += (_, e) => events.Add($"popped {Name(e.Type)} {e.ChangedParentTypes.Count}");
        um.StackCleared += (_, e) => events.Add($"cleared {Bool(e.UndoStackCleared)} {Bool(e.RedoStackCleared)}");

        var history = new List<string>();
        var stepIndex = 0;
        foreach (var step in script.GetProperty("ops").EnumerateArray())
        {
            var op = step.GetProperty("op");
            var kind = op[0].GetString();
            history.Add(op.GetRawText().Length > 200 ? op.GetRawText()[..200] + "..." : op.GetRawText());
            events.Clear();
            string why;
            try
            {
                bool? ret = null;
                switch (kind)
                {
                    case "undo": ret = um.Undo() != null; break;
                    case "redo": ret = um.Redo() != null; break;
                    case "stop": um.StopCapturing(); break;
                    case "clear": um.Clear(op[1].GetBoolean(), op[2].GetBoolean()); break;
                    case "scope": um.AddToScope(roots[op[1].GetString()]); break;
                    case "remote": doc.ApplyUpdate(Convert.FromBase64String(op[1].GetString()), "remote"); break;
                    case "local":
                        doc.Transact(_ =>
                        {
                            foreach (var change in op[2].EnumerateArray())
                            {
                                Apply(doc, change);
                            }
                        }, Origin(op[1]));
                        break;
                    default: throw new InvalidOperationException("op " + kind);
                }

                why = Compare(step, doc, um, ret, events);
            }
            catch (Exception e)
            {
                why = $"throws: {e.GetType().Name}: {e.Message}";
            }

            if (why != null)
            {
                return new UndoDivergence(seed, config.GetRawText(), stepIndex, kind + " " + why.Split(':')[0], why, history);
            }

            stepIndex++;
        }

        return null;
    }

    private static string Compare(JsonElement step, YDoc doc, UndoManager um, bool? ret, List<string> events)
    {
        // A file written with FULL=1 holds whole states instead of their hashes.
        var expectedState = step.GetProperty("state").GetString();
        var state = Convert.ToHexStringLower(doc.EncodeStateAsUpdate());
        if ((expectedState.Length == 16 ? Convert.ToHexStringLower(SHA256.HashData(Convert.FromHexString(state)))[..16] : state) != expectedState)
        {
            return expectedState.Length == 16 ? "state: the document differs" : $"state: the document differs: Yjs {expectedState}, Ycs {state}";
        }

        if (ret != null && ret != step.GetProperty("ret").GetBoolean())
        {
            return $"returned: Yjs {step.GetProperty("ret").GetBoolean()}, Ycs {ret}";
        }

        if (um.UndoStack.Count != step.GetProperty("undo").GetInt32() || um.RedoStack.Count != step.GetProperty("redo").GetInt32())
        {
            return $"stacks: Yjs {step.GetProperty("undo").GetInt32()}/{step.GetProperty("redo").GetInt32()}, Ycs {um.UndoStack.Count}/{um.RedoStack.Count}";
        }

        var expected = step.GetProperty("events").EnumerateArray().Select(e => e.GetString()).ToList();
        if (!expected.SequenceEqual(events))
        {
            return $"events: Yjs [{string.Join(", ", expected)}], Ycs [{string.Join(", ", events)}]";
        }

        return null;
    }

    private static object Origin(JsonElement name) => name.ValueKind == JsonValueKind.Null ? null : name.GetString() == "cls" ? new ClassOrigin() : name.GetString();

    private static string Name(UndoManager.OperationType type) => type == UndoManager.OperationType.Undo ? "undo" : "redo";

    private static string Bool(bool b) => b ? "true" : "false";

    private static object Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetInt32(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static void Apply(YDoc doc, JsonElement op)
    {
        var t = doc.GetText("t");
        var m = doc.GetMap("m");
        var a = doc.GetArray("a");
        var x = doc.GetXmlFragment("x");
        object MapGet(string key) => m.ContainsKey(key) ? m.Get(key) : null;
        object XmlAt(int i) => i < x.Length ? x.Get(i) : null;

        switch (op[0].GetString())
        {
            case "tins":
                t.Insert(op[1].GetInt32(), op[2].GetString(), op[3].ValueKind == JsonValueKind.Null ? null : new Dictionary<string, object> { ["b"] = true });
                break;
            case "tdel": t.Delete(op[1].GetInt32(), op[2].GetInt32()); break;
            case "tfmt": t.Format(op[1].GetInt32(), op[2].GetInt32(), new Dictionary<string, object> { ["b"] = Value(op[3]) }); break;
            case "temb": t.InsertEmbed(op[1].GetInt32(), new Dictionary<string, object> { ["e"] = op[2].GetInt32() }); break;
            case "mset": m.Set(op[1].GetString(), Value(op[2])); break;
            case "mdel": m.Delete(op[1].GetString()); break;
            case "mnest": { var n = new YMap(); m.Set(op[1].GetString(), n); n.Set("k", Value(op[2])); break; }
            case "mtext": { var n = new YText(); m.Set(op[1].GetString(), n); n.Insert(0, op[2].GetString()); break; }
            case "mnset":
                switch (MapGet(op[1].GetString()))
                {
                    case YMap nm: nm.Set(op[2].GetString(), Value(op[3])); break;
                    case YText nt: nt.Insert(0, op[3].GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                }

                break;
            case "ains": a.Insert(op[1].GetInt32(), new object[] { Value(op[2]) }); break;
            case "adel": a.Delete(op[1].GetInt32(), op[2].GetInt32()); break;
            case "anest": { var n = new YMap(); a.Insert(op[1].GetInt32(), new object[] { n }); n.Set("k", Value(op[2])); break; }
            case "atext": { var n = new YText(); a.Insert(op[1].GetInt32(), new object[] { n }); n.Insert(0, op[2].GetString()); break; }
            case "nset":
                switch (a.Get(op[1].GetInt32()))
                {
                    case YMap nm: nm.Set(op[2].GetString(), Value(op[3])); break;
                    case YText nt: nt.Insert(0, op[3].GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture)); break;
                }

                break;
            case "xel": { var e = new YXmlElement("p"); x.Insert(op[1].GetInt32(), new object[] { e }); e.SetAttribute("k", op[2].GetString()); break; }
            case "xtext": { var n = new YXmlText(); x.Insert(op[1].GetInt32(), new object[] { n }); n.Insert(0, op[2].GetString()); break; }
            case "xattr":
                if (XmlAt(op[1].GetInt32()) is YXmlElement attrElement)
                {
                    if (op[3].ValueKind == JsonValueKind.Null)
                    {
                        attrElement.RemoveAttribute(op[2].GetString());
                    }
                    else
                    {
                        attrElement.SetAttribute(op[2].GetString(), op[3].GetString());
                    }
                }

                break;
            case "xchild":
                switch (XmlAt(op[1].GetInt32()))
                {
                    case YXmlElement e: e.Insert(0, new object[] { new YXmlText(op[2].GetString()) }); break;
                    case YXmlText xt: xt.Insert(0, op[2].GetString()); break;
                }

                break;
            case "xfmt":
                if (XmlAt(op[1].GetInt32()) is YXmlText ft && ft.Length > 0)
                {
                    ft.Format(0, Math.Min(op[2].GetInt32(), ft.Length), new Dictionary<string, object> { ["i"] = Value(op[3]) });
                }

                break;
            case "xdel": x.Delete(op[1].GetInt32(), op[2].GetInt32()); break;
            default: throw new InvalidOperationException("change " + op[0].GetString());
        }
    }
}
