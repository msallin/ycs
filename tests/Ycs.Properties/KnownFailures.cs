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
        new("yjs-gc-throws-after-integrating", "documented",
            "Not a Ycs bug: an update that deletes a type in which a key holds two live items (a value of several items, " +
            "which Yjs never writes undeleted, split by a deleted range or a reference inside it, by this update or an earlier " +
            "one): Yjs integrates it and then throws in the cleanup ('Unexpected case' in Item.gc), so its document is left " +
            "changed, and wedged: the throw skips the end of cleanupTransactions, which leaves the transaction in " +
            "doc._transactionCleanups, so no later transaction of that document is cleaned up (no observer, afterTransaction " +
            "or update event, also for a local change; the former pins pending-integration-events and empty-remote-transaction " +
            "were this). Ycs rejects it with YUpdateException before integrating (docs/api.md, Untrusted input). Both reject; " +
            "the states differ.",
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
        new("right-origin-precedes-origin", "documented",
            "Not a Ycs bug (docs/api.md, Untrusted input): an item whose right origin precedes its origin within one item of their " +
            "client (Yjs never writes one; a fuzzer does): Yjs applies it, splitting the item of the origin at the right origin, so " +
            "that the new item lands between the halves, where a client that reads the whole state in one update, which does not " +
            "split there, places it elsewhere: a document and a client that joins it disagree for good. Ycs rejects the update " +
            "(or, when it was waiting for what arrived, drops it and reports PartlyApplied).",
            """{"clients":[{"gc":false,"id":2}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AQULAAcADAEDA2RpdsYKAgoBBGJvbGQEdHJ1ZQcACgMDAXAEAKwCAQJiY4QKAAFhA6wCAQIGCgEFAgwBAQI=","via":"v1to2"},{"op":"raw","to":0,"u":"AwP4+v//BwAEAQF0A2RlZgoBh/j6//8HAgEDDAAHAQF4AwFwxwwADAADAXCHDAAGAgoAAQEBdAMnAAwAAW0DA2RpdgIMAQEBCgIDAwAD","via":"v1to2"}]}""")
        {
            Documented = true,
            Matches = (m, script) => m.Op == "raw" && m.Signature.EndsWith(" / ycs threw", StringComparison.Ordinal) && Rejected(script, m.Step, "right origin that precedes its origin"),
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
            "pending; Ycs applies the ranges as given, and its ConvertUpdateFormatV1ToV2 sorts them (Yjs' conversion writes " +
            "the negative differences).",
            """{"clients":[{"gc":false,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AQILAAQBAXQCYmMnAQFtAWsCAQsEAggAAwABAgg=","via":"v1"},{"op":"raw","to":0,"u":"BAT4+v//BwAHAAsCAYUKARF7ImltYWdlIjoiaS5wbmcifSgA+Pr//wcAAW0CdQJ9AXcBeHUCfQF3AXiBCwEBAA0AAqwCACIA+Pr//wcAAWsCATEDImoiCAEBYQJ3AXN9AQEKAMQLAAsAAmJjAvj6//8HAgMBAQKsAgECBw==","via":"v1"},{"op":"raw","to":0,"u":"AwULAygBAW0BbQN4dwFzez/4AAAAAAAAIgD4+v//BwABbQIBMQMiaiIIAKwCCAN+ez/4AAAAAAAAfgcBAXgGgqwCAgIBMQMiaiIFDAAHAQF4BggBAWEBdwFzJwEBbQFsAAQBAXQDZGVmBAALCwJiYwOsAgQEAQF0A2RlZgcBAXgDAXAnAQFtAWwAAA==","via":"v1"}]}""")
        {
            Documented = true,
            // Also seen first in the converted bytes, when the step converts such an update from v1 to v2 (Ycs sorts it).
            Matches = (m, script) => m.Op == "raw" && (m.Primary == "pend"
                || (Step(script, m) is var step && step.Str("via") == "v1to2" && ScriptRun.HasUnorderedDeleteSet(step.Str("u")))),
        },
    ];

    private static bool Threw(string observation) => Json.Parse(observation) is Dictionary<string, object> o && o.Get("err") is true;

    // Whether Ycs' rejection at step `step` is the garbage collection check's (StructStore.IntegrationPlan.CheckGarbageCollection).
    private static bool RejectedByGcCheck(Dictionary<string, object> script, int step) => Rejected(script, step, "garbage collection cannot remove");

    // Whether Ycs' rejection at step `step` has `reason` in its message.
    private static bool Rejected(Dictionary<string, object> script, int step, string reason)
    {
        var run = new ScriptRun(script);
        var steps = script.List("steps");
        for (var i = 0; i <= step && i < steps.Count; i++)
        {
            run.Step((Dictionary<string, object>)steps[i]);
        }

        return run.LastException?.Message.Contains(reason, StringComparison.Ordinal) == true;
    }

    private static Dictionary<string, object> Step(Dictionary<string, object> script, Mismatch m) =>
        m.Step >= 0 && m.Step < script.List("steps").Count ? (Dictionary<string, object>)script.List("steps")[m.Step] : new Dictionary<string, object>();

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
