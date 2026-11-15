using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ycs.Properties;

/// <summary>
/// A divergence from Yjs (or a violated property) that is a bug in Ycs, or a documented difference:
/// its minimal script, why, and how the soak recognizes it. <see cref="Normalize"/>, if set,
/// rewrites an observation so that the divergence, which would otherwise show in most scripts,
/// does not hide the others; <see cref="Matches"/>, if set, recognizes a mismatch it causes.
/// </summary>
internal sealed record KnownFailure(string Name, string Severity, string Reason, string Script)
{
    public Action<Dictionary<string, object>> Normalize { get; init; }

    public Func<Mismatch, Dictionary<string, object>, bool> Matches { get; init; }

    /// <summary>Not a bug of Ycs: a difference docs/api.md documents, or a Yjs bug.</summary>
    public bool Documented { get; init; }

    /// <summary>Fails only in Debug builds (a Debug.Assert of the library); Release agrees with Yjs.</summary>
    public bool DebugOnly { get; init; }

    /// <summary>For a violated property (no oracle): the violation's signature ("property: kind"); null for a divergence from Yjs.</summary>
    public string Property { get; init; }
}

/// <summary>
/// The bugs this project found and that are not fixed (src/ is not changed by test work), most
/// severe first. <see cref="PinnedTests"/> replays each script against the Yjs observations
/// recorded for it (interop/properties/fixtures/pinned.jsonl) and reports Inconclusive with the
/// reason while Ycs still differs, and fails once it agrees, so that the entry (and its
/// normalization) is removed with the fix. See docs/testing.md.
/// </summary>
internal static class KnownFailures
{
    public static readonly IReadOnlyList<KnownFailure> All =
    [
        new("convert-v1-to-v2-overlapping-delete-set", "low",
            "UpdateOperations.ConvertUpdateFormatV1ToV2 of a valid v1 update whose delete set is not sorted, or has " +
            "overlapping or repeated ranges (Yjs' v1 decoder takes them), writes a v2 delete set that Ycs' own v2 decoder rejects ('A " +
            "variable-length integer is out of range', from the negative clock difference). Yjs' output applies but is " +
            "corrupt in the same way (see yjs-unsorted-delete-set): neither converts such an update faithfully.",
            """{"clients":[{"gc":false,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AgGsAgIEAKwCAQN4w6kC8KIECoesAgEGx/CiBAqsAgEDA2RpdgILAgADAAKsAgEBAg==","via":"v1to2"}]}""")
        {
            Matches = (m, script) => m.Op is "raw" or "deliver" && Step(script, m).Str("via") == "v1to2" && m.Signature.EndsWith(" / ycs threw", StringComparison.Ordinal),
        },
        new("yjs-gc-throws-after-integrating", "documented",
            "Not a Ycs bug: an update that deletes a type in which a key holds two live items (a value of several items, " +
            "which Yjs never writes undeleted, split by a deleted range or a reference inside it, by this update or an earlier " +
            "one): Yjs integrates it and then throws in the cleanup ('Unexpected case' in Item.gc), so its document is left " +
            "changed, and wedged: the throw skips the end of cleanupTransactions, which leaves the transaction in " +
            "doc._transactionCleanups, so no later transaction of that document is cleaned up (no observer, afterTransaction " +
            "or update event, also for a local change; the former pin pending-integration-events was this). Ycs rejects it " +
            "with YUpdateException before integrating (docs/api.md, Untrusted input). Both reject; the states differ.",
            """{"clients":[{"gc":true,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AwKsAgAIAQFhA353AXN9AQQBAXQD5LitAvj6//8HAIesAgIBhKwCAwJiYwPwogQAh6wCAAEoAPCiBAABbQF9RygA+Pr//wcAAWwDfUd4eAA=","via":"v1"},{"op":"raw","to":0,"u":"AQUKACgA+Pr//wcAAWsCeH0BxPj6//8HAfj6//8HAQJiYygA8KIEAAFsAnh9R4fwogQAAQoCA/CiBAEDAfj6//8HAQADCgIBAwQC","via":"v1"}]}""")
        {
            Documented = true,
            Matches = (m, _) => m.Op == "raw" && Threw(m.Yjs) && Threw(m.Ycs),
        },
        new("gc-check-rejects-what-yjs-applies", "documented",
            "Not a Ycs bug (docs/api.md, Untrusted input): Ycs estimates where a map value of an update lands among its key's " +
            "items to find a value split as yjs-gc-throws-after-integrating describes, and in rare fuzzed updates (about 1 in " +
            "20,000 of the structure-aware fuzzer's scripts, none Yjs writes) the estimate is wrong: Ycs rejects an update " +
            "Yjs applies.",
            """{"clients":[{"gc":true,"id":1}],"roots":[{"kind":"map","name":"m"}],"steps":[{"op":"raw","to":0,"u":"BAUMACcBAW0BbQEhAQFtAW0BRwwBAcMKAwoDAwECAygADAIBbAJ+fgANAAKsAgAiAAwAAWsCATEDImoiJwAMAAFtAQMKAAQBAXQD5LithAoAA3jDqQMBAWEDAQIDAgoCAAEDAwwBAgQ=","via":"v1"},{"op":"raw","to":0,"u":"AQMLAAQBAXQDeMOpBwEBeAMBcMQLAAsAA3jDqQIMAQgCrAIBAQE=","via":"v1to2"},{"op":"raw","to":0,"u":"AQTwogQAKAAMAgFtA30Bfn5CDAACATEDImoiJwAMAgFsAIQLBAJiYwA=","via":"v1"},{"op":"raw","to":0,"u":"AwMKBIgKAwF+KAEBbQFtAX4iAAwCAWsCATEDImoiBQwJRwsCBicADAABbAEiAQFtAW0CATEDImoiBAEBdAFhKAEBbQFsAX0BAvCiBAgnAAsCAW0GBADwogQIA+S4rQPwogQBCQcKAQQDCwECAw==","via":"v1"},{"op":"raw","to":0,"u":"AwQLBYMKAwMBAgOICwUBdwFzxvCiBAnwogQJBml0YWxpYwR0cnVlKAEBbQFtAns/+AAAAAAAAHs/+AAAAAAAAAL4+v//BwAnAQFtAWwExwsCCwIDAXABDA/E8KIEB/CiBAcD5LitAwsCBQEHAvCiBAEFAawCAQID","via":"v1"}]}""")
        {
            Documented = true,
            Matches = (m, script) => m.Op == "raw" && m.Signature.EndsWith(" / ycs threw", StringComparison.Ordinal) && RejectedByGcCheck(script, m.Step),
        },
        new("huge-clock-gc-encoding", "low",
            "A client whose history starts with a GC struct reaching close to the clock limit (clock 2^31 - 65), followed " +
            "by items, a Skip and pending structs: after integration Ycs' EncodeStateAsUpdate (and the transaction's update) " +
            "splits that client's structs differently from Yjs (5 structs where Yjs writes 6); the content agrees.",
            """{"clients":[{"gc":false,"id":2}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AQL4+v//BwAAv////wfHrAIFrAIFAgILAQMDrAIBAwI=","via":"v1"},{"op":"raw","to":0,"u":"AQMLAIWsAgYReyJpbWFnZSI6ImkucG5nIn1ErAIGAmJjAQEBYQECrAIBAggLAQAC","via":"v1"},{"op":"raw","to":0,"u":"AQSsAgAIAQFhA30BdwFzfoisAgEDdQJ9AXcBeH0BdQJ9AXcBeAQBAXQD5LitBwEBeAYA","via":"v1"}]}""")
        {
            Matches = (m, _) => m.Op == "raw" && m.Primary is "ev:update" or "v1" or "v2" && !m.Differences.Any(d => d.Contains(".roots.", StringComparison.Ordinal)),
        },
        new("empty-remote-transaction", "low",
            "Applying a fuzzed update that, at that point, changes nothing (its structs are known or still wait): Ycs runs " +
            "an empty transaction (afterTransaction fires, origin \"net\"); Yjs runs none.",
            """{"clients":[{"gc":true,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AwT4+v//BwAnAQFtAWsBhKwCAgN4w6kIAQFhA30BeH1HBwEBeAMDZGl2BPCiBAAhAQFtAWsCIgD4+v//BwABawIBMQMiaiLI+Pr//wcE+Pr//wcEAnh7P/gAAAAAAACErAICAWECDAAoAPj6//8HAAFsAnh7P/gAAAAAAACH+Pr//wcEAAIMAQID8KIEAQEH","via":"v1"},{"op":"raw","to":0,"u":"AQULACcA+Pr//wcGAW0Gh/j6//8HBgYnAPj6//8HBgFsAwNkaXYHAPj6//8HBgMBcCMA+Pr//wcAAWsDAQIDAvj6//8HAQIIDAEBAw==","via":"v1"},{"op":"raw","to":0,"u":"AgQKAIfwogQJBiEBAW0BbAIHAAsDAwNkaXaHCgAGBPj6//8HBygACwYBbQJ+dQJ9AXcBeMELCQsHAoSsAgED5LitCgEC+Pr//wcBAALwogQCAQIJAw==","via":"v1"},{"op":"raw","to":0,"u":"AwQKBYjwogQEAXgHAAsAAMf4+v//BwT4+v//BwQCKAEBbQFrAXUCfQF3AXgErAIDKAEBbQFsAn1HfUcnAAsFAWsGBwEBeAMDZGl2xAsICwgCYmMEDAOH+Pr//wcGBojwogQFAXUCfQF3AXjI+Pr//wcEDAQDfQF7P/gAAAAAAAB4BwD4+v//BwYGAA==","via":"v1"}]}""")
        {
            Matches = (m, _) => m.Op == "raw" && m.Primary == "ev:atx" && m.Differences.All(d => d.Contains("yjs \"(missing)\"", StringComparison.Ordinal)),
        },
        new("pending-deletes-split-surrogate-pair", "low",
            "Two deletions that arrive before the insertion they delete (one character each, the two halves of a surrogate " +
            "pair): Ycs applies the waiting ranges one by one, splitting the deleted item between the halves, so its encoding " +
            "holds U+FFFD U+FFFD; Yjs merges the pending ranges first and keeps the pair. The visible content agrees.",
            """{"clients":[{"gc":false,"id":818522},{"gc":false,"id":10}],"roots":[{"kind":"text","name":"t"}],"steps":[{"at":0,"c":0,"op":"insert","path":["t"],"text":"\ud83d\udc4d"},{"c":0,"delta":[{"delete":1}],"op":"delta","path":["t"]},{"c":0,"delta":[{"delete":1}],"op":"delta","path":["t"]},{"from":0,"k":2,"op":"deliver","to":1,"via":"v1"},{"from":0,"k":1,"op":"deliver","to":1,"via":"v1"},{"from":0,"k":0,"op":"deliver","to":1,"via":"v2"}]}""")
        {
            Matches = (m, _) => m.Op is "deliver" or "merged" or "sync" or "raw" && m.Primary is "ev:update" or "v1" or "v2"
                && m.Differences.Any(HasTwoReplacementCharacters),
        },
        new("debug-assert-clock-over-2^30", "low",
            "Debug builds only: a valid update with clocks between 2^30 and 2^31 - 1 (within docs/api.md's limit) fails " +
            "Debug.Assert(value <= Bits.Bits30) in the encoder; Release builds apply and re-encode it as Yjs does.",
            """{"clients":[{"gc":true,"id":1},{"gc":false,"id":2}],"roots":[{"kind":"text","name":"t"},{"kind":"array","name":"a"},{"kind":"map","name":"m"},{"kind":"xml","name":"x"}],"steps":[{"op":"raw","to":0,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":0,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":0,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":0,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":1,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":1,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"}]}""")
        {
            DebugOnly = true,
            // In Debug the assertion also shows as a plain rejection of such an update (it is caught as an error).
            Matches = (m, _) => m.Differences.Any(d => d.Contains("Bits.Bits30", StringComparison.Ordinal))
                || (Debugger.IsDebugBuild && m.Op == "raw" && m.Signature.EndsWith(" / ycs threw", StringComparison.Ordinal)),
        },
        new("debug-assert-clock-over-2^30-property", "low",
            "The rejects-cleanly property sees debug-assert-clock-over-2^30 as a DebugAssertException (Debug builds only).",
            """{"clients":[{"gc":true,"id":1},{"gc":false,"id":2}],"roots":[{"kind":"text","name":"t"},{"kind":"array","name":"a"},{"kind":"map","name":"m"},{"kind":"xml","name":"x"}],"steps":[{"op":"raw","to":0,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":0,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":0,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":0,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":1,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":1,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"}]}""")
        {
            DebugOnly = true,
            Property = "rejects-cleanly: threw DebugAssertException",
        },
        new("yjs-redo-recreated-type", "documented",
            "Not a Ycs bug: undoing everything and redoing everything does not restore a nested type that was created and " +
            "then edited (redo re-creates the type, and the edits inside it are not all redone); Yjs ends with the same " +
            "content as Ycs on this script (the differential agrees), so the property is stronger than Yjs' UndoManager.",
            """{"clients":[{"gc":false,"id":4294966611}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"id":"all","op":"um","scope":[["m"]],"timeout":"0"},{"c":0,"key":"a","op":"set","path":["m"],"value":{"$t":"xmle","n":"div","v":[{"$t":"xmlt","s":"\u4e2d\u6587 b"}]}},{"at":0,"attrs":{"italic":null},"c":0,"len":5,"op":"format","path":["m","a",0]},{"at":0,"c":0,"op":"insert","path":["m","a",0],"text":"a"},{"at":4,"c":0,"len":1,"op":"delete","path":["m","a",0]},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"redo"},{"c":0,"id":"all","op":"redo"},{"c":0,"id":"all","op":"redo"},{"c":0,"id":"all","op":"redo"}]}""")
        {
            Documented = true,
            Property = "undo-all-restores: after redoing everything",
        },
        new("yjs-throws", "documented",
            "Not a Ycs bug: Yjs 13.6.32 throws where Ycs does not: an UndoManager whose scope is a list holding the Y.Doc " +
            "(it takes typeScope[0].doc, undefined for a Doc: 'Cannot read properties of undefined'); XmlFragment.toString " +
            "on a fragment holding a non-XML value. Ycs accepts both.",
            """{"clients":[{"id":1}],"roots":[{"name":"t","kind":"text"}],"steps":[{"op":"um","c":0,"id":"u","scope":["$doc"],"timeout":"0"},{"op":"insert","c":0,"path":["t"],"at":0,"text":"a"}]}""")
        {
            Documented = true,
            Matches = (m, _) => m.Signature.EndsWith(" / yjs threw", StringComparison.Ordinal)
                || m.Differences.Any(d => d.Contains("yjs {\"$err\":\"toJSON\"}", StringComparison.Ordinal) || d.Contains("$err: yjs \"toJSON\"", StringComparison.Ordinal)),
        },
        new("yjs-unsorted-delete-set", "documented",
            "Not a Ycs bug: a delete set whose ranges are not sorted or overlap (valid v1; Yjs never writes one, a fuzzer or another " +
            "implementation may) makes Yjs' v2 delete-set encoder write negative clock differences, so its pending delete " +
            "set holds a phantom range (here 11:[131,7], which would delete items that do not exist yet) and stays " +
            "pending; Ycs applies the ranges as given.",
            """{"clients":[{"gc":false,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AQILAAQBAXQCYmMnAQFtAWsCAQsEAggAAwABAgg=","via":"v1"},{"op":"raw","to":0,"u":"BAT4+v//BwAHAAsCAYUKARF7ImltYWdlIjoiaS5wbmcifSgA+Pr//wcAAW0CdQJ9AXcBeHUCfQF3AXiBCwEBAA0AAqwCACIA+Pr//wcAAWsCATEDImoiCAEBYQJ3AXN9AQEKAMQLAAsAAmJjAvj6//8HAgMBAQKsAgECBw==","via":"v1"},{"op":"raw","to":0,"u":"AwULAygBAW0BbQN4dwFzez/4AAAAAAAAIgD4+v//BwABbQIBMQMiaiIIAKwCCAN+ez/4AAAAAAAAfgcBAXgGgqwCAgIBMQMiaiIFDAAHAQF4BggBAWEBdwFzJwEBbQFsAAQBAXQDZGVmBAALCwJiYwOsAgQEAQF0A2RlZgcBAXgDAXAnAQFtAWwAAA==","via":"v1"}]}""")
        {
            Documented = true,
            Matches = (m, _) => m.Op == "raw" && m.Primary == "pend",
        },
    ];

    private static bool Threw(string observation) => Json.Parse(observation) is Dictionary<string, object> o && o.Get("err") is true;

    // Whether Ycs' rejection at step `step` is the garbage collection check's (StructStore.IntegrationPlan.CheckGarbageCollection).
    private static bool RejectedByGcCheck(Dictionary<string, object> script, int step)
    {
        var run = new ScriptRun(script);
        var steps = script.List("steps");
        for (var i = 0; i <= step && i < steps.Count; i++)
        {
            run.Step((Dictionary<string, object>)steps[i]);
        }

        return run.LastException?.Message.Contains("garbage collection cannot remove", StringComparison.Ordinal) == true;
    }

    private static Dictionary<string, object> Step(Dictionary<string, object> script, Mismatch m) =>
        m.Step >= 0 && m.Step < script.List("steps").Count ? (Dictionary<string, object>)script.List("steps")[m.Step] : new Dictionary<string, object>();

    // ".ev[0].es[0].d[1].insert: yjs "a" | ycs "aa"": Ycs' insert ends with Yjs' and is longer.
    private static bool RepeatedInsert(string difference)
    {
        var match = Regex.Match(difference, "\\.d\\[\\d+\\]\\.insert: yjs \"(.*)\" \\| ycs \"(.*)\"$");
        return match.Success && match.Groups[2].Value.Length > match.Groups[1].Value.Length
            && match.Groups[2].Value.EndsWith(match.Groups[1].Value, StringComparison.Ordinal);
    }

    // The Ycs side of a bytes difference holds U+FFFD U+FFFD (EF BF BD EF BF BD) and the Yjs side does not.
    private static bool HasTwoReplacementCharacters(string difference)
    {
        var match = Regex.Match(difference, "yjs \"([A-Za-z0-9+/=]+)\" \\| ycs \"([A-Za-z0-9+/=]+)\"");
        if (!match.Success)
        {
            return false;
        }

        static bool Has(string b64)
        {
            try
            {
                return Convert.ToHexString(Convert.FromBase64String(b64)).Contains("EFBFBDEFBFBD", StringComparison.Ordinal);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return Has(match.Groups[2].Value) && !Has(match.Groups[1].Value);
    }

    public static KnownFailure Named(string name) => All.Single(k => k.Name == name);

    /// <summary>Applies every normalization to one step's observation (both sides get the same).</summary>
    public static void Normalize(Dictionary<string, object> observation, KnownFailure except = null)
    {
        foreach (var k in All)
        {
            if (k != except)
            {
                k.Normalize?.Invoke(observation);
            }
        }
    }

    /// <summary>The known failure a property violation is, or null.</summary>
    public static KnownFailure Explain(Violation violation) =>
        All.FirstOrDefault(k => k.Property != null && violation.Signature.StartsWith(k.Property, StringComparison.Ordinal));

    /// <summary>The known failure a mismatch is, or null.</summary>
    public static KnownFailure Explain(Mismatch mismatch, Dictionary<string, object> script) =>
        All.FirstOrDefault(k => k.Matches != null && k.Matches(mismatch, script));

    /// <summary>Runs <paramref name="assertion"/>; if it fails and <paramref name="known"/> is set, reports Inconclusive instead.</summary>
    public static void Check(KnownFailure known, Action assertion)
    {
        if (known == null)
        {
            assertion();
            return;
        }

        try
        {
            assertion();
        }
        catch (Exception e) when (e is not AssertInconclusiveException)
        {
            Assert.Inconclusive($"KNOWN FAILURE {known.Name}: {known.Reason}{Environment.NewLine}{e.Message}");
        }

        Assert.Fail($"{known.Name} is listed in KnownFailures but now passes; remove the entry (and its normalization).");
    }
}
