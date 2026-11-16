using static Ycs.Properties.Json;

namespace Ycs.Properties;

/// <summary>A property that does not hold for a script: which, what kind of failure, and the detail.</summary>
internal sealed record Violation(string Property, string Kind, string Detail)
{
    public string Signature => $"{Property}: {Kind}";
}

/// <summary>A property that needs no oracle: how to generate its scripts and how to check one.</summary>
internal sealed record PropertySpec(string Name, string Description, Func<ulong, Dictionary<string, object>> Generate, Func<Dictionary<string, object>, Violation> Check);

/// <summary>
/// Properties of Ycs that need no oracle (no Node), checked on generated scripts: each executes the
/// script on Ycs (ScriptRun) and then does more with the replicas, or compares observations of
/// variants of the script. Each Check returns null when the property holds.
/// </summary>
internal static class Properties
{
    public static readonly IReadOnlyList<PropertySpec> All =
    [
        new("convergence", "every client, and a fresh observer, ends in the same state after receiving every update in a random order, with duplicates and some withheld until last",
            ConvergenceScript, Convergence),
        new("encode-roundtrip", "ApplyUpdate(EncodeStateAsUpdate(doc)) into a fresh document reproduces the content and the state vector, and its encoding is then a fixpoint, in v1 and v2",
            seed => Generator.Generate(seed, NoLoneSurrogates), EncodeRoundTrip),
        new("merge-partition", "MergeUpdates of any partition of a document's updates (and of the merged parts) gives the document's state, in v1 and v2",
            seed => Generator.Generate(seed, BmpOnly), MergePartition),
        new("diff-update", "DiffUpdate(EncodeStateAsUpdate(a), StateVector(b)) and a.EncodeStateAsUpdate(StateVector(b)), applied to b (which holds part of a), give a's state",
            DiffUpdateScript, DiffUpdate),
        new("v1-v2-conversion", "an update converted v1 to v2 (and back) applies to the same state as the original, and converting a converted update again changes no byte",
            seed => Generator.Generate(seed, NoLoneSurrogates), Conversion),
        new("undo-all-restores", "on a single client with only local changes, undoing every tracked change restores the initial content and redoing all of them the final one",
            seed => UndoScript(seed, pairs: false), UndoAll),
        new("undo-redo-pair", "an undo followed at once by a redo restores the content before the undo",
            seed => UndoScript(seed, pairs: true), UndoRedoPairs),
        new("snapshot-stable", "with gc off, reading a snapshot (ToDelta, CreateDocFromSnapshot) gives the same at the end as when it was taken, and CreateDocFromSnapshot then equals the content",
            SnapshotScript, SnapshotStable),
        new("relpos-stable", "a relative position on a character resolves, on every replica after a full sync, to the same index, which holds that character",
            RelPosScript, RelPosStable),
        new("gc-invisible", "garbage collection on or off changes no observation but the encodings",
            seed => WithoutSnapshots(Generator.Generate(seed)), GcInvisible),
        new("naive-recompute", "after every step, ToString, Length and Count equal a walk over the items",
            seed => Generator.Generate(seed), NaiveRecompute),
        new("item-list-invariants", "after every step, every sequence satisfies the Lean-derived list invariants (tests/Ycs.Corpus/ItemListInvariants.cs)",
            seed => Generator.Generate(seed), ItemListInvariantsHold),
        new("idempotence", "applying an update a document already has changes nothing",
            seed => Generator.Generate(seed, BmpOnly), Idempotence),
        new("commutativity", "applying two updates in either order gives the same state",
            CommutativityScript, Commutativity),
        new("rejects-cleanly", "a structured (fuzzed) update is applied, or rejected with YUpdateException leaving the document as it was; nothing else is thrown; and after the script every client's state applies to a fresh document, in v1 and v2",
            seed => UpdateFuzzer.Generate(seed).Script, RejectsCleanly),
        new("markers-invisible", "search markers change no observation (content, encodings, results, events) against the same script with them off, and after every step every marker points at an item of its list with the right index",
            ListsOrGeneral, MarkersInvisible),
        new("merge-order", "merging a run at the end of a transaction in one pass leaves the same structs (ids, lengths, content, links, origins, parents, map entries, search markers) as Yjs' pair-by-pair order, after every step",
            ListsOrWindows, MergeOrder),
        new("run-extension", "inserts that continue a run in place (with the one-pass merge) leave the same structs (as merge-order, search markers included) and observations as Yjs' items, one per insert, merged pair by pair, after every step",
            ListsOrWindows, RunExtension),
        new("walk-index", "the marker lookups through the run-head index end on the same item with the same index as Yjs' loops item by item, at every lookup, with the same structs (as merge-order, search markers included) and observations, after every step; with run extension as the script has it and off",
            ListsOrWindows, WalkIndex),
        new("event-replay", "replaying the deltas and key changes the root observers saw onto a shadow copy reproduces each root (text, array, xml, map) after every step",
            seed => Generator.Generate(seed, BmpOnly), EventReplay),
    ];

    public static PropertySpec Named(string name) => All.Single(p => p.Name == name);

    // A lone surrogate is kept by the document that inserted it and written as U+FFFD, in Yjs as in
    // Ycs: properties that compare a document with one decoded from its bytes leave them out.
    private static readonly GenOptions NoLoneSurrogates = new() { LoneSurrogates = false };

    // A split surrogate pair is replaced by U+FFFD in place, in Yjs as in Ycs, without an event and
    // also in what a snapshot shows; so is a lone surrogate where an item is split after it. Any split
    // does that, also one no change asks for: applying an update a document already has splits items at
    // its structs' origins (Yjs' getMissing runs before the struct is found to be known). Properties
    // about events, snapshots, undo and re-applied updates leave out such characters (docs/testing.md).
    private static readonly GenOptions BmpOnly = new() { LoneSurrogates = false, Astral = false };

    // ---------------------------------------------------------------- helpers

    private static (ScriptRun Run, List<Dictionary<string, object>> Observations) Execute(Dictionary<string, object> script)
    {
        var run = new ScriptRun(script);
        var observations = script.List("steps").Cast<Dictionary<string, object>>().Select(run.Step).ToList();
        observations.Add(run.End());
        return (run, observations);
    }

    /// <summary>
    /// Whether an operation threw. A throwing operation may leave a change without an update (Yjs
    /// splits a text before it finds format's attributes missing), so properties about what the
    /// updates carry do not apply; the differential soak compares those scripts with Yjs instead.
    /// </summary>
    private static bool HasErrors(IEnumerable<Dictionary<string, object>> observations) =>
        observations.Any(o => o.Get("err") is true || (o.Get("r") is List<object> results && results.Any(r => r is Dictionary<string, object> d && d.Get("err") is true)));

    private static List<(int Client, int K, byte[] V1, byte[] V2)> Updates(ScriptRun run) =>
        run.Clients.SelectMany(c => c.Log.Select((u, k) => (c.Index, k, u.V1, u.V2))).ToList();

    private static Replica Fresh(ScriptRun run, bool gc = true) => new(null, gc, run.Roots);

    private static Replica FreshWith(ScriptRun run, Replica source, bool v2)
    {
        var fresh = Fresh(run, source.Gc);
        if (v2)
        {
            fresh.ApplyV2(source.EncodeV2());
        }
        else
        {
            fresh.ApplyV1(source.EncodeV1());
        }

        return fresh;
    }

    private static string Content(Replica r) => Canonical(r.Content());

    private static string Sv(Replica r) => Canonical(ScriptRun.Clocks(Lib0.DecodeStateVector(r.StateVector())));

    private static string Ds(Replica r) => Canonical(Replica.DeleteSetOf(r.EncodeV1(), false));

    private static string Short(string s) => s.Length > 600 ? s[..600] + "..." : s;

    private static Violation Differs(string property, string kind, string a, string b) =>
        a == b ? null : new Violation(property, kind, $"{Short(a)}{Environment.NewLine}  vs {Short(b)}");

    private static Violation Guard(string property, Func<Violation> check)
    {
        try
        {
            return check();
        }
        catch (Exception e)
        {
            return new Violation(property, "threw " + e.GetType().Name, e.Message);
        }
    }

    // Seeded by the script's text (not string.GetHashCode, which differs per process), so that a check is repeatable and shrinkable.
    private static Rng RngFor(Dictionary<string, object> script) =>
        new(BitConverter.ToUInt64(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Canonical(script))), 0));

    /// <summary>Delivers every update from every client's log to <paramref name="to"/> until nothing new appears (cleanups make updates).</summary>
    private static void DeliverAll(ScriptRun run, Replica to, Rng rng, bool duplicates)
    {
        var delivered = new HashSet<(int, int)>();
        for (var round = 0; round < 6; round++)
        {
            var pending = Updates(run).Where(u => !delivered.Contains((u.Client, u.K))).ToList();
            if (pending.Count == 0)
            {
                return;
            }

            rng.Shuffle(pending);
            // Some are withheld and arrive last.
            var late = pending.Where(_ => rng.Chance(0.15)).ToList();
            foreach (var u in pending.Except(late).Concat(late))
            {
                Apply(to, u, rng);
                if (duplicates && rng.Chance(0.1))
                {
                    Apply(to, u, rng);
                }

                delivered.Add((u.Client, u.K));
            }
        }
    }

    private static void Apply(Replica to, (int Client, int K, byte[] V1, byte[] V2) u, Rng rng)
    {
        if (rng.Chance(0.5))
        {
            to.ApplyV1(u.V1);
        }
        else
        {
            to.ApplyV2(u.V2);
        }
    }

    // ---------------------------------------------------------------- the properties

    /// <summary>
    /// The script with an extra client (the observer, which only receives), then rounds of
    /// deliveries until every client has every update, each round in a random order, with
    /// duplicates, v1 and v2, and some updates withheld until the end of the round. The deliveries
    /// are script steps, generated while running it (cleanups make new updates).
    /// </summary>
    private static Dictionary<string, object> ConvergenceScript(ulong seed)
    {
        // BMP only: a remote format that splits a surrogate pair leaves Yjs' replicas apart too (U+FFFD on one side).
        var script = Generator.Generate(seed, new GenOptions { FinalSync = 0, LoneSurrogates = false, Astral = false });
        script.List("clients").Add(Normalized(Obj(("id", 1000003L), ("gc", seed % 2 == 0))));
        var run = new ScriptRun(script);
        foreach (Dictionary<string, object> step in script.List("steps"))
        {
            run.Step(step);
        }

        var rng = new Rng(seed * 3571);
        var delivered = run.Clients.Select(_ => new HashSet<(int, int)>()).ToList();
        for (var round = 0; round < 8; round++)
        {
            var any = false;
            for (var to = 0; to < run.Clients.Count; to++)
            {
                var missing = Updates(run).Select(u => (u.Client, u.K)).Where(u => u.Client != to && !delivered[to].Contains(u)).ToList();
                rng.Shuffle(missing);
                var late = missing.Where(_ => rng.Chance(0.15)).ToList();
                foreach (var (from, k) in missing.Except(late).Concat(late))
                {
                    var times = rng.Chance(0.1) ? 2 : 1;
                    for (var t = 0; t < times; t++)
                    {
                        var step = Normalized(Obj(("op", "deliver"), ("from", (long)from), ("k", (long)k), ("to", (long)to), ("via", rng.Chance(0.5) ? "v1" : "v2")));
                        script.List("steps").Add(step);
                        run.Step(step);
                    }

                    delivered[to].Add((from, k));
                    any = true;
                }
            }

            if (!any)
            {
                break;
            }
        }

        return script;
    }

    private static Violation Convergence(Dictionary<string, object> script) => Guard("convergence", () =>
    {
        var (run, observations) = Execute(script);
        var all = Updates(run);
        // Only when every client was given every other client's every update (shrinking removes deliveries) and nothing threw.
        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        var given = steps.Where(s => s.Str("op") == "deliver").Select(s => (s.Int("to"), s.Int("from"), s.Int("k"))).ToHashSet();
        if (HasErrors(observations) || steps.Any(s => s.Str("op") is "sync" or "merged")
            || run.Clients.Any(c => all.Any(u => u.Client != c.Index && !given.Contains((c.Index, u.Client, u.K)))))
        {
            return null;
        }

        var replicas = run.Clients.Select(c => c.Replica).ToList();
        var observer = replicas[^1];
        foreach (var r in replicas)
        {
            if (r.HasPending)
            {
                return new Violation("convergence", "pending", $"client {r.ClientId} still has pending structs");
            }
        }

        var first = replicas[0];
        foreach (var r in replicas.Skip(1))
        {
            // The observer's own formatting cleanups delete locally and reach nobody, so its delete set is not compared.
            var v = Differs("convergence", "content", Content(first), Content(r))
                ?? Differs("convergence", "sv", Sv(first), Sv(r))
                ?? (r == observer ? null : Differs("convergence", "ds", Ds(first), Ds(r)));
            if (v != null)
            {
                return v with { Detail = $"client {first.ClientId} vs {r.ClientId}: {v.Detail}" };
            }
        }

        return null;
    });

    private static Violation EncodeRoundTrip(Dictionary<string, object> script) => Guard("encode-roundtrip", () =>
    {
        var (run, observations) = Execute(script);
        if (HasErrors(observations))
        {
            return null;
        }

        foreach (var c in run.Clients)
        {
            var r = c.Replica;
            var v1 = Fresh(run, r.Gc);
            v1.ApplyV1(r.EncodeV1());
            var v2 = Fresh(run, r.Gc);
            v2.ApplyV2(r.EncodeV2());
            // Bytes: a document read from the bytes may merge structs the original kept apart (an
            // UndoManager's keep flag), in Yjs as in Ycs; from then on the encoding is a fixpoint.
            var b1 = Fresh(run, gc: false);
            b1.ApplyV1(r.EncodeV1());
            var b2 = Fresh(run, gc: false);
            b2.ApplyV2(r.EncodeV2());
            var c1 = Fresh(run, gc: false);
            c1.ApplyV1(b1.EncodeV1());
            var c2 = Fresh(run, gc: false);
            c2.ApplyV2(b2.EncodeV2());
            var v = Differs("encode-roundtrip", "content v1", Content(r), Content(v1))
                ?? Differs("encode-roundtrip", "content v2", Content(r), Content(v2))
                ?? Differs("encode-roundtrip", "sv", Sv(r), Sv(v1))
                ?? (r.HasPending ? null : Differs("encode-roundtrip", "bytes v1", Convert.ToBase64String(b1.EncodeV1()), Convert.ToBase64String(c1.EncodeV1())))
                ?? (r.HasPending ? null : Differs("encode-roundtrip", "bytes v2", Convert.ToBase64String(b2.EncodeV2()), Convert.ToBase64String(c2.EncodeV2())));
            if (v != null)
            {
                return v;
            }
        }

        return null;
    });

    private static Violation MergePartition(Dictionary<string, object> script) => Guard("merge-partition", () =>
    {
        var (run, observations) = Execute(script);
        if (HasErrors(observations))
        {
            return null;
        }

        var rng = RngFor(script);
        foreach (var v2 in new[] { false, true })
        {
            // A document's updates: everything logged, and every client's state as one update.
            var updates = Updates(run).Select(u => v2 ? u.V2 : u.V1)
                .Concat(run.Clients.Select(c => v2 ? c.Replica.EncodeV2() : c.Replica.EncodeV1()))
                .ToList();
            rng.Shuffle(updates);
            var cleanedUp = false;
            Replica Watched()
            {
                var r = Fresh(run);
                r.UpdateV1 += (_, local) => cleanedUp |= local;
                return r;
            }

            var reference = Watched();
            foreach (var u in updates)
            {
                Apply(reference, u, v2);
            }

            var groups = updates.GroupBy(_ => rng.Int(4)).Select(g => g.ToList()).ToList();
            var merged = groups.Select(g => Ycs.Properties.Updates.Merge(g, v2)).ToList();
            var all = Ycs.Properties.Updates.Merge(merged, v2);
            var fromAll = Watched();
            Apply(fromAll, all, v2);
            var fromParts = Watched();
            foreach (var m in merged)
            {
                Apply(fromParts, m, v2);
            }

            // A text's formatting cleanup after a remote transaction (a local transaction of the receiver) deletes the
            // formats that are redundant at that moment: which they are depends on what the transaction brought, so
            // the attributes depend on how the updates were grouped, in Yjs as in Ycs. Then only the rest is compared.
            string Seen(Replica r) => cleanedUp ? Canonical(WithoutAttributes(r.Content())) : Content(r);
            var format = v2 ? "v2" : "v1";
            var v = Differs("merge-partition", "merged all " + format, Seen(reference), Seen(fromAll))
                ?? Differs("merge-partition", "merged parts " + format, Seen(reference), Seen(fromParts))
                ?? Differs("merge-partition", "sv " + format, Sv(reference), Sv(fromAll));
            if (v != null)
            {
                return v;
            }
        }

        return null;
    });

    /// <summary>
    /// A content view without formatting: no attributes in deltas (and the texts of a delta's inserts that differed in
    /// them joined), and no toJSON (an XML text renders its formats as tags).
    /// </summary>
    private static object WithoutAttributes(object view)
    {
        switch (view)
        {
            case Dictionary<string, object> d:
                return d.Where(kv => kv.Key is not ("attributes" or "j")).ToDictionary(kv => kv.Key, kv => WithoutAttributes(kv.Value));
            case List<object> l:
                var joined = new List<object>();
                foreach (var e in l.Select(WithoutAttributes))
                {
                    if (e is Dictionary<string, object> { Count: 1 } op && op.Get("insert") is string s
                        && joined.Count > 0 && joined[^1] is Dictionary<string, object> { Count: 1 } last && last.Get("insert") is string before)
                    {
                        last["insert"] = before + s;
                    }
                    else
                    {
                        joined.Add(e);
                    }
                }

                return joined;
            default:
                return view;
        }
    }

    private static void Apply(Replica to, byte[] update, bool v2)
    {
        if (v2)
        {
            to.ApplyV2(update);
        }
        else
        {
            to.ApplyV1(update);
        }
    }

    /// <summary>
    /// The script, then two fresh clients B and C (gc on) that receive the same random part of what
    /// a client A has, then A syncs to B by DiffUpdate(EncodeStateAsUpdate(A), StateVector(B)) and to
    /// C by A.EncodeStateAsUpdate(StateVector(C)) (sync with and without "diff").
    /// </summary>
    private static Dictionary<string, object> DiffUpdateScript(ulong seed)
    {
        // BMP only: a part B already holds keeps a surrogate pair that A split afterwards (A's U+FFFD is no update).
        var script = Generator.Generate(seed, BmpOnly);
        var run = new ScriptRun(script);
        foreach (Dictionary<string, object> step in script.List("steps"))
        {
            run.Step(step);
        }

        var rng = new Rng(seed * 7477);
        var clients = script.List("clients");
        var a = rng.Int(clients.Count);
        var (b, c) = ((long)clients.Count, (long)clients.Count + 1);
        clients.Add(Normalized(Obj(("id", 1000004L), ("gc", true))));
        clients.Add(Normalized(Obj(("id", 1000005L), ("gc", true))));
        var v2 = rng.Chance(0.5);
        // Only updates A made itself, so that B and C certainly hold a part of A.
        foreach (var u in Updates(run).Where(u => u.Client == a && rng.Chance(0.5)))
        {
            foreach (var to in new[] { b, c })
            {
                script.List("steps").Add(Normalized(Obj(("op", "deliver"), ("from", (long)u.Client), ("k", (long)u.K), ("to", to), ("via", "v1"))));
            }
        }

        script.List("steps").Add(Normalized(Obj(("op", "sync"), ("from", (long)a), ("to", b), ("via", v2 ? "v2" : "v1"), ("diff", true))));
        script.List("steps").Add(Normalized(Obj(("op", "sync"), ("from", (long)a), ("to", c), ("via", v2 ? "v2" : "v1"))));
        return script;
    }

    private static Violation DiffUpdate(Dictionary<string, object> script) => Guard("diff-update", () =>
    {
        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        var clients = script.List("clients").Cast<Dictionary<string, object>>().ToList();
        var b = clients.FindIndex(x => x.Int("id") == 1000004);
        var c = clients.FindIndex(x => x.Int("id") == 1000005);
        var diff = steps.FindIndex(s => s.Str("op") == "sync" && s.Bool("diff") && s.Int("to") == b);
        var plain = steps.FindIndex(s => s.Str("op") == "sync" && !s.Bool("diff") && s.Int("to") == c);
        if (b < 0 || c < 0 || diff < 0 || plain < 0 || steps[diff].Int("from") != steps[plain].Int("from"))
        {
            return null;
        }

        var (run, observations) = Execute(script);
        var a = run.Clients[(int)steps[diff].Int("from")].Replica;
        // Only if B and C hold part of A, and A's state did not change after the syncs.
        var from = steps[diff].Int("from");
        if (HasErrors(observations) || a.HasPending || steps.Skip(Math.Min(diff, plain)).Any(s => s.Int("c", -1) >= 0 || s.Int("to") == from)
            || steps.Any(s => s.Str("op") is "sync" or "merged" && s.Int("to") is var to && (to == b || to == c) && s.Int("from") != from)
            || steps.Any(s => s.Str("op") == "deliver" && (s.Int("to") == b || s.Int("to") == c) && s.Int("from") != from))
        {
            return null;
        }

        // B and C make no change of their own but their formatting cleanups after a remote transaction, which depend on
        // what each transaction brought (as for merge-partition): then the content is compared without attributes,
        // and the state vector without their own clients.
        var (rb, rc) = (run.Clients[b].Replica, run.Clients[c].Replica);
        var cleanedUp = run.Clients[b].Log.Count > 0 || run.Clients[c].Log.Count > 0;
        string Seen(Replica r) => cleanedUp ? Canonical(WithoutAttributes(r.Content())) : Content(r);
        string SvOf(Replica r) => Canonical(ScriptRun.Clocks(Lib0.DecodeStateVector(r.StateVector()).Where(kv => kv.Key != rb.ClientId && kv.Key != rc.ClientId).ToList()));
        return Differs("diff-update", "DiffUpdate", Seen(a), Seen(rb))
            ?? Differs("diff-update", "EncodeStateAsUpdate(sv)", Seen(a), Seen(rc))
            ?? Differs("diff-update", "sv", SvOf(a), SvOf(rb));
    });

    private static Violation Conversion(Dictionary<string, object> script) => Guard("v1-v2-conversion", () =>
    {
        var (run, observations) = Execute(script);
        if (HasErrors(observations))
        {
            return null;
        }

        var updates = Updates(run);
        var native1 = Fresh(run);
        var native2 = Fresh(run);
        var converted1 = Fresh(run);
        var converted2 = Fresh(run);
        foreach (var u in updates)
        {
            var c2 = Ycs.Properties.Updates.V1ToV2(u.V1);
            var c1 = Ycs.Properties.Updates.V2ToV1(u.V2);
            native1.ApplyV1(u.V1);
            native2.ApplyV2(u.V2);
            converted1.ApplyV1(c1);
            converted2.ApplyV2(c2);
            // Yjs' conversion is not byte-preserving either (it drops redundant parentSubs, say), but a
            // converted update is a fixpoint: converting it there and back again changes nothing.
            var once = Ycs.Properties.Updates.V2ToV1(c2);
            var twice = Ycs.Properties.Updates.V2ToV1(Ycs.Properties.Updates.V1ToV2(once));
            var v = Differs("v1-v2-conversion", "round trip not a fixpoint", Convert.ToBase64String(once), Convert.ToBase64String(twice));
            if (v != null)
            {
                return v;
            }
        }

        return Differs("v1-v2-conversion", "content v1 vs v2", Content(native1), Content(native2))
            ?? Differs("v1-v2-conversion", "content converted to v1", Content(native1), Content(converted1))
            ?? Differs("v1-v2-conversion", "content converted to v2", Content(native1), Content(converted2));
    });

    private static readonly string[] LocalKinds =
    [
        OpKinds.TextInsert, OpKinds.TextDelete, OpKinds.TextFormat, OpKinds.TextEmbed, OpKinds.TextDelta, OpKinds.TextAttr,
        OpKinds.ArrayInsert, OpKinds.ArrayDelete, OpKinds.MapSet, OpKinds.MapDelete, OpKinds.XmlInsert, OpKinds.XmlDelete,
        OpKinds.XmlAttr, OpKinds.XmlText, OpKinds.Nested, OpKinds.Transact,
    ];

    /// <summary>One client, local changes only, an UndoManager over every root (no capture), then undos and redos.</summary>
    private static Dictionary<string, object> UndoScript(ulong seed, bool pairs)
    {
        // BMP only: an undo or redo that splits an item at a lone surrogate or between the halves of a pair
        // replaces them with U+FFFD, in Yjs as in Ycs, so the content before the undo does not come back.
        var script = Generator.Generate(seed, new GenOptions { SingleClientLocal = true, Kinds = LocalKinds, MinSteps = 2, MaxSteps = 20, LoneSurrogates = false, Astral = false });
        var roots = script.List("roots").Cast<Dictionary<string, object>>().Select(r => (object)new List<object> { r["name"] }).ToList();
        var steps = new List<object> { Normalized(Obj(("op", "um"), ("c", 0L), ("id", "all"), ("scope", roots), ("timeout", "0"))) };
        var changes = script.List("steps");
        foreach (Dictionary<string, object> step in changes)
        {
            // The UndoManager tracks the null origin (its default) only.
            step.Remove("origin");
            steps.Add(step);
            if (pairs)
            {
                steps.Add(Normalized(Obj(("op", "undo"), ("c", 0L), ("id", "all"))));
                steps.Add(Normalized(Obj(("op", "redo"), ("c", 0L), ("id", "all"))));
            }
        }

        if (!pairs)
        {
            for (var i = 0; i < changes.Count; i++)
            {
                steps.Add(Normalized(Obj(("op", "undo"), ("c", 0L), ("id", "all"))));
            }

            for (var i = 0; i < changes.Count; i++)
            {
                steps.Add(Normalized(Obj(("op", "redo"), ("c", 0L), ("id", "all"))));
            }
        }

        script["steps"] = steps;
        return script;
    }

    private static Dictionary<string, object> Normalized(Dictionary<string, object> step) => (Dictionary<string, object>)Parse(Canonical(step));

    private static string RootsAfter(Dictionary<string, object> observation, int client = 0) =>
        Canonical(((Dictionary<string, object>)((Dictionary<string, object>)observation["st"])[client.ToString(System.Globalization.CultureInfo.InvariantCulture)])["roots"]);

    private static Violation UndoAll(Dictionary<string, object> script) => Guard("undo-all-restores", () =>
    {
        var (run, observations) = Execute(script);
        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        var lastChange = steps.FindLastIndex(s => s.Str("op") is not ("undo" or "redo" or "um"));
        var lastUndo = steps.FindLastIndex(s => s.Str("op") == "undo");
        var lastRedo = steps.FindLastIndex(s => s.Str("op") == "redo");
        var initial = Content(Fresh(run));

        // Only when the undo stack (redo stack) is empty afterwards was everything undone (redone),
        // and only if no change came after the undos (redos).
        bool Emptied(int i, string stack) => i >= 0 && observations[i].Get("r") is Dictionary<string, object> r && r.Get(stack) is false;
        return (Emptied(lastUndo, "u") && lastUndo > lastChange && (lastRedo < 0 || lastRedo > lastUndo)
                ? Differs("undo-all-restores", "after undoing everything", initial, RootsAfter(observations[lastUndo]))
                : null)
            ?? (Emptied(lastRedo, "d") && lastRedo > lastUndo && lastUndo > lastChange && lastChange >= 0 && Emptied(lastUndo, "u") && !EditsInsideNestedTypes(steps)
                ? Differs("undo-all-restores", "after redoing everything", RootsAfter(observations[lastChange]), RootsAfter(observations[lastRedo]))
                : null);
    });

    // Whether a change edits inside a nested type (which this single-client script created): redoing everything
    // re-creates such a type, and Yjs (as Ycs) does not redo every edit made inside it (YjsLaws: redo-recreated-type).
    private static bool EditsInsideNestedTypes(List<Dictionary<string, object>> steps) =>
        steps.SelectMany(s => s.List("ops")?.Cast<Dictionary<string, object>>().Prepend(s) ?? [s]).Any(s => s.List("path") is { Count: > 1 });

    private static Violation UndoRedoPairs(Dictionary<string, object> script) => Guard("undo-redo-pair", () =>
    {
        var (_, observations) = Execute(script);
        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        for (var i = 2; i < steps.Count; i++)
        {
            if (steps[i].Str("op") == "redo" && steps[i - 1].Str("op") == "undo" && steps[i - 2].Str("op") is not ("undo" or "redo" or "um")
                && observations[i - 1].Get("r") is Dictionary<string, object> undone && undone.Get("r") is true)
            {
                var v = Differs("undo-redo-pair", "redo after undo", RootsAfter(observations[i - 2]), RootsAfter(observations[i]));
                if (v != null)
                {
                    return v with { Detail = $"at step {i}: {v.Detail}" };
                }
            }
        }

        return null;
    });

    private static Dictionary<string, object> WithoutSnapshots(Dictionary<string, object> script)
    {
        script["steps"] = script.List("steps").Cast<Dictionary<string, object>>().Where(s => s.Str("op") is not ("snap" or "readSnap" or "docFromSnap")).Cast<object>().ToList();
        return script;
    }

    /// <summary>A script with gc off everywhere and, at a random step, a snapshot on one client read right away and again at the end.</summary>
    private static Dictionary<string, object> SnapshotScript(ulong seed)
    {
        var script = WithoutSnapshots(Generator.Generate(seed, BmpOnly));
        foreach (Dictionary<string, object> client in script.List("clients"))
        {
            client["gc"] = false;
        }

        var rng = new Rng(seed * 7919);
        var steps = script.List("steps");
        var c = (long)rng.Int(script.List("clients").Count);
        var reads = new List<object> { Normalized(Obj(("op", "docFromSnap"), ("c", c), ("id", "p"))) };
        // Only texts: Yjs' ToArraySnapshot and GetAllSnapshot (as Ycs') do not split at the snapshot, so they read an item
        // merged across it whole, values added after the snapshot included (Yjs breaks the property there).
        foreach (Dictionary<string, object> root in script.List("roots").Cast<Dictionary<string, object>>().Where(r => r.Str("kind") == "text"))
        {
            reads.Add(Normalized(Obj(("op", "readSnap"), ("c", c), ("id", "p"), ("path", new List<object> { root["name"] }))));
        }

        var at = rng.Int(steps.Count + 1);
        steps.InsertRange(at, reads);
        steps.Insert(at, Normalized(Obj(("op", "snap"), ("c", c), ("id", "p"))));
        steps.AddRange(reads.Select(Clone));
        return script;
    }

    private static Violation SnapshotStable(Dictionary<string, object> script) => Guard("snapshot-stable", () =>
    {
        var (_, observations) = Execute(script);
        if (HasErrors(observations))
        {
            return null;
        }

        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        var snap = steps.FindIndex(s => s.Str("op") == "snap" && s.Str("id") == "p");
        if (snap < 0)
        {
            return null;
        }

        // Each read right after the snapshot against the same read at the end.
        var c = steps[snap].Int("c");
        string ReadKey(Dictionary<string, object> s) => s.Str("op") is "readSnap" or "docFromSnap" && s.Int("c") == c && s.Str("id") == "p" ? Canonical(s) : null;
        var early = new Dictionary<string, int>();
        for (var i = snap + 1; i < steps.Count && ReadKey(steps[i]) is { } key; i++)
        {
            early.TryAdd(key, i);
        }

        for (var i = steps.Count - 1; i > snap && ReadKey(steps[i]) is { } key; i--)
        {
            if (early.TryGetValue(key, out var e) && e != i)
            {
                // A nested type in a read shows its current content, not the snapshot's: compare it by kind only.
                var v = steps[i].Str("op") == "docFromSnap"
                    ? Differs("snapshot-stable", "document from snapshot changed", Canonical(observations[e].Get("r")), Canonical(observations[i].Get("r")))
                    : Differs("snapshot-stable", "snapshot read changed", Canonical(Kinds(observations[e].Get("r"))), Canonical(Kinds(observations[i].Get("r"))));
                if (v != null)
                {
                    return v;
                }
            }
        }

        // The document from the snapshot, right after it was taken, is the content.
        var first = early.Where(kv => steps[kv.Value].Str("op") == "docFromSnap").Select(kv => kv.Value).DefaultIfEmpty(-1).First();
        if (first >= 0 && observations[first].Get("r") is Dictionary<string, object> doc && doc.Get("roots") is Dictionary<string, object> roots
            && ((Dictionary<string, object>)observations[snap]["st"]).Get(c.ToString(System.Globalization.CultureInfo.InvariantCulture)) is Dictionary<string, object> state)
        {
            var content = (Dictionary<string, object>)state["roots"];
            foreach (var (name, view) in roots)
            {
                var v = content.Get(name) is Dictionary<string, object> root
                    ? Differs("snapshot-stable", "document from snapshot is not the content", Canonical(root["v"]), Canonical(view))
                    : null;
                if (v != null)
                {
                    return v;
                }
            }
        }

        return null;
    });

    private static object Kinds(object view) => view switch
    {
        Dictionary<string, object> d when d.ContainsKey("$t") => Obj(("$t", d["$t"])),
        Dictionary<string, object> d => d.ToDictionary(kv => kv.Key, kv => Kinds(kv.Value)),
        List<object> l => l.Select(Kinds).ToList(),
        _ => view,
    };

    private static int ReadsPerSnapshot(Dictionary<string, object> script) => 1 + script.List("roots").Count;

    private const string Marker = "☃";

    /// <summary>A script with, at a random step, a marker character inserted into a root text and a relative position on it; then a full sync and a resolution on every client.</summary>
    private static Dictionary<string, object> RelPosScript(ulong seed)
    {
        var script = WithoutSnapshots(Generator.Generate(seed, new GenOptions { FinalSync = 0 }));
        if (!script.List("roots").Cast<Dictionary<string, object>>().Any(r => r.Str("kind") == "text"))
        {
            script.List("roots").Add(Normalized(Obj(("name", "t"), ("kind", "text"))));
        }

        var text = script.List("roots").Cast<Dictionary<string, object>>().First(r => r.Str("kind") == "text").Str("name");
        var rng = new Rng(seed * 104729);
        var steps = script.List("steps");
        var clients = script.List("clients").Count;
        var c = (long)rng.Int(clients);
        var at = rng.Int(steps.Count + 1);
        var prefix = new ScriptRun(script);
        foreach (Dictionary<string, object> step in steps.Take(at))
        {
            prefix.Step(step);
        }

        var length = prefix.Targets((int)c).First(t => t.Path.Count == 1 && (string)t.Path[0] == text).Length;
        var index = (long)rng.Int(length + 1);
        steps.Insert(at, Normalized(Obj(("op", "rpos"), ("c", c), ("id", "m"), ("path", new List<object> { text }), ("at", index))));
        steps.Insert(at, Normalized(Obj(("op", "insert"), ("c", c), ("path", new List<object> { text }), ("at", index), ("text", Marker))));
        for (var round = 0; round < 3; round++)
        {
            for (var from = 0; from < clients; from++)
            {
                for (var to = 0; to < clients; to++)
                {
                    if (from != to)
                    {
                        steps.Add(Normalized(Obj(("op", "sync"), ("from", (long)from), ("to", (long)to), ("via", "v1"))));
                    }
                }
            }
        }

        for (var to = 0; to < clients; to++)
        {
            steps.Add(Normalized(Obj(("op", "rres"), ("c", (long)to), ("id", "m"))));
        }

        return script;
    }

    private static Violation RelPosStable(Dictionary<string, object> script) => Guard("relpos-stable", () =>
    {
        var (_, observations) = Execute(script);
        if (HasErrors(observations))
        {
            return null;
        }

        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        var rpos = steps.FindIndex(s => s.Str("op") == "rpos" && s.Str("id") == "m");
        // The position is on the marker if its item is the item the marker insert made (shrinking can move either).
        var marker = rpos > 0 && steps[rpos - 1].Str("op") == "insert" && steps[rpos - 1].Str("text") == Marker && MarkerItem(script, observations, rpos);
        const int before = 0;
        var resolutions = Enumerable.Range(0, steps.Count).Where(i => i > rpos && steps[i].Str("op") == "rres" && steps[i].Str("id") == "m").ToList();
        // An undo or a redo after the position was taken may re-create its item; only the replica that did links the
        // two (Item.Redone is local), so the others resolve to where the deleted item was, in Yjs as in Ycs.
        if (rpos < 0 || resolutions.Count == 0 || observations[rpos].Get("r") is not Dictionary<string, object> || !Synced(observations[^1])
            || steps.Skip(rpos).Any(s => s.Str("op") is "undo" or "redo"))
        {
            return null;
        }

        // After the full sync (the last steps), every replica resolves the same way, onto the marker.
        var results = resolutions.Select(i => Canonical(observations[i].Get("r"))).ToList();
        foreach (var r in results.Skip(1))
        {
            var v = Differs("relpos-stable", "replicas resolve differently", results[0], r);
            if (v != null)
            {
                return v;
            }
        }

        var text = steps[rpos].List("path")[0] as string;
        foreach (var i in resolutions)
        {
            var client = steps[i].Int("c").ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!marker || ((Dictionary<string, object>)observations[^1]["st"]).Get(client) is not Dictionary<string, object> state
                || ((Dictionary<string, object>)state["roots"]).Get(text) is not Dictionary<string, object> root)
            {
                continue;
            }

            // The text's units as indexes count them: each UTF-16 code unit of a string, and 1 per embed.
            var units = ((List<object>)((Dictionary<string, object>)root["v"])["d"]).Cast<Dictionary<string, object>>()
                .SelectMany(op => op["insert"] is string str ? str.Select(ch => ch.ToString()) : ["\uFFFC"]).ToList();
            if (!units.Contains(Marker))
            {
                continue;
            }

            var resolved = observations[i].Get("r") as Dictionary<string, object>;
            var index = (int)(resolved?.Int("i", -1) ?? -1) - before;
            if (index < 0 || index >= units.Count || units[index] != Marker)
            {
                return new Violation("relpos-stable", "not on the character", $"client {client} resolved to {Canonical(resolved)}, text {Canonical(root["v"])}");
            }
        }

        return null;
    });

    // Whether every client ends with the same state vector and nothing pending (shrinking can remove the full sync).
    private static bool Synced(Dictionary<string, object> end)
    {
        var states = ((Dictionary<string, object>)end["st"]).Values.Cast<Dictionary<string, object>>().ToList();
        return states.All(s => s.Get("pend") is not true) && states.Select(s => Canonical(s["sv"])).Distinct(StringComparer.Ordinal).Count() == 1;
    }

    private static bool MarkerItem(Dictionary<string, object> script, List<Dictionary<string, object>> observations, int rpos)
    {
        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        var c = steps[rpos].Int("c");
        var id = ((Dictionary<string, object>)script.List("clients")[(int)c]).Int("id");
        var key = c.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (((Dictionary<string, object>)observations[rpos - 1]["st"]).Get(key) is not Dictionary<string, object> state
            || observations[rpos].Get("r") is not Dictionary<string, object> r || r.Get("j") is not Dictionary<string, object> json
            || json.Get("item") is not Dictionary<string, object> item)
        {
            return false;
        }

        var clock = ((Dictionary<string, object>)state["sv"]).Int(id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return item.Int("client") == id && item.Int("clock") == clock - 1;
    }

    /// <summary>
    /// The observations of the two variants (gc on, gc off) without what garbage collection changes, in Yjs as in
    /// Ycs (docs/testing.md, "Laws Yjs breaks"): the encodings, and results and update bytes that carry them; the
    /// order of calls between different observers (it follows the order in which a transaction first changed each
    /// type, and a struct collected into a GC struct changes none); a relative position in a deleted type (gone
    /// with gc); and everything from the first step at which structs wait in either variant (a GC struct waits for
    /// no parent, so it, and what comes after it, is integrated earlier) up to the end, where only the content of
    /// the clients with nothing waiting in either variant is compared; and the stacks of an UndoManager whose scope is
    /// the document, which captures every transaction, also the empty cleanup transactions, whose number depends on gc.
    /// </summary>
    private static (string On, string Off) WithoutGcDifferences(IReadOnlyList<Dictionary<string, object>> gcOn, IReadOnlyList<Dictionary<string, object>> gcOff, Dictionary<string, object> script)
    {
        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        var documentScoped = steps.Where(s => s.Str("op") == "um" && s.List("scope") is { } scope && scope.Contains("$doc"))
            .Select(s => $"{s.Int("c")}.{s.Str("id")}").ToHashSet(StringComparer.Ordinal);
        var on = (List<object>)Clone(gcOn.Cast<object>().ToList());
        var off = (List<object>)Clone(gcOff.Cast<object>().ToList());
        static IEnumerable<Dictionary<string, object>> States(object obs) =>
            ((obs as Dictionary<string, object>)?.Get("st") as Dictionary<string, object>)?.Values.Cast<Dictionary<string, object>>() ?? [];
        var count = Math.Min(on.Count, off.Count) - 1;
        var cut = Enumerable.Range(0, count).FirstOrDefault(i => States(on[i]).Concat(States(off[i])).Any(st => st.Get("pend") is true), count);
        for (var i = 0; i < cut; i++)
        {
            var (a, b) = ((Dictionary<string, object>)on[i], (Dictionary<string, object>)off[i]);
            if (steps[i].Str("op") == "rres" && (Hidden(a.Get("r")) || Hidden(b.Get("r"))))
            {
                a.Remove("r");
                b.Remove("r");
            }

            if (steps[i].Str("op") is "undo" or "redo" or "stop" or "umclear" && documentScoped.Contains($"{steps[i].Int("c")}.{steps[i].Str("id")}"))
            {
                a.Remove("r");
                b.Remove("r");
            }

            Normalize(a, steps[i], documentScoped);
            Normalize(b, steps[i], documentScoped);
        }

        // The end: the content of the clients with nothing waiting in either variant.
        var (endOn, endOff) = ((Dictionary<string, object>)on[^1], (Dictionary<string, object>)off[^1]);
        var settled = ((Dictionary<string, object>)endOn["st"]).Keys
            .Where(c => endOn.Get("st") is Dictionary<string, object> x && endOff.Get("st") is Dictionary<string, object> y
                && (x.Get(c) as Dictionary<string, object>)?.Get("pend") is false && (y.Get(c) as Dictionary<string, object>)?.Get("pend") is false)
            .ToList();
        string Render(List<object> all, Dictionary<string, object> end) => Canonical(all.Take(cut)
            .Append(settled.ToDictionary(c => c, c => ((Dictionary<string, object>)((Dictionary<string, object>)end["st"])[c])["roots"]))
            .ToList());
        return (Render(on, endOn), Render(off, endOff));

        // Not resolved, or into a type that is not visible (deleted).
        static bool Hidden(object r) => r is not Dictionary<string, object> d || (d.List("p") is { Count: > 0 } p && p[0] is "~");

        static void Normalize(Dictionary<string, object> obs, Dictionary<string, object> step, HashSet<string> documentScoped)
        {
            KnownFailures.Normalize(obs);
            // Results that carry encodings (sync messages, merged and converted updates, snapshots).
            if (step.Str("op") is "sync" or "merged" or "deliver" or "docFromSnap" or "snap")
            {
                obs.Remove("r");
            }

            // A transaction's update is encoded after its garbage collection, so its bytes differ too.
            // How many empty (cleanup) transactions Yjs opens depends on gc too.
            if (obs.List("ev") is { } events)
            {
                events.RemoveAll(e => e is Dictionary<string, object> r && (r.Str("w") is "u1" or "u2" || (r.Str("w") == "atx" && r.List("changed") is { Count: 0 })
                    || (r.Str("w") is "um+" or "um~" or "um-" or "umclear" && documentScoped.Contains(r.Str("id")))));
                var ordered = events.Select((e, k) => (e, k)).OrderBy(x => Canonical(Observer(x.e)), StringComparer.Ordinal).ThenBy(x => x.k).Select(x => x.e).ToList();
                events.Clear();
                events.AddRange(ordered);
            }

            foreach (var state in States(obs))
            {
                state.Remove("v1");
                state.Remove("v2");
            }
        }

        // The observer an event went to: its client and kind (an observer of a type, a document listener).
        static object Observer(object e) => e is Dictionary<string, object> d ? Obj(("c", d.Get("c") ?? Json.Missing.Value), ("w", d.Get("w") ?? Json.Missing.Value)) : e;
    }

    private static Violation GcInvisible(Dictionary<string, object> script) => Guard("gc-invisible", () =>
        GcInvisibleOn(script, Execute(Variant(script, true)).Observations, Execute(Variant(script, false)).Observations));

    /// <summary>The script with every client's garbage collection on or off.</summary>
    public static Dictionary<string, object> Variant(Dictionary<string, object> script, bool gc)
    {
        var copy = (Dictionary<string, object>)Clone(script);
        foreach (Dictionary<string, object> client in copy.List("clients"))
        {
            client["gc"] = gc;
        }

        return copy;
    }

    /// <summary>The gc-invisible check on any observations of the two variants (Ycs' or Yjs').</summary>
    public static Violation GcInvisibleOn(Dictionary<string, object> script, IReadOnlyList<Dictionary<string, object>> gcOn, IReadOnlyList<Dictionary<string, object>> gcOff)
    {
        var (on, off) = WithoutGcDifferences(gcOn, gcOff, script);
        if (on == off)
        {
            return null;
        }

        var a = (List<object>)Parse(on);
        var b = (List<object>)Parse(off);
        var i = Enumerable.Range(0, a.Count).First(k => Canonical(a[k]) != Canonical(b[k]));
        var kind = i == a.Count - 1 || Canonical(((Dictionary<string, object>)a[i]).Get("st")) != Canonical(((Dictionary<string, object>)b[i]).Get("st")) ? "state" : "events or results";
        return new Violation("gc-invisible", kind, $"step {i}: gc on {Short(Canonical(a[i]))}{Environment.NewLine}  gc off {Short(Canonical(b[i]))}");
    }

    private static Violation StepWise(Dictionary<string, object> script, Func<ScriptRun, int, Violation> afterStep) =>
        Guard("stepwise", () =>
        {
            var run = new ScriptRun(script);
            var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
            for (var i = 0; i < steps.Count; i++)
            {
                run.Step(steps[i]);
                if (afterStep(run, i) is { } v)
                {
                    return v with { Detail = $"after step {i} ({steps[i].Str("op")}): {v.Detail}" };
                }
            }

            return null;
        });

    /// <summary>Every other seed a script of long lists (ListScripts), the others the full mix.</summary>
    private static Dictionary<string, object> ListsOrGeneral(ulong seed) => seed % 2 == 1 ? ListScripts.Generate(seed) : Generator.Generate(seed);

    /// <summary>
    /// As <see cref="ListsOrGeneral"/>, with the windows in which a run extended in place could show (updates applied
    /// in the middle of transactions, garbage collection filters, snapshots read within transactions, observers
    /// editing; <see cref="GenOptions.Windows"/>): Yjs leaves search markers stale there, so markers on and off differ.
    /// </summary>
    private static Dictionary<string, object> ListsOrWindows(ulong seed) =>
        seed % 2 == 1 ? ListScripts.Generate(seed, windows: true) : Generator.Generate(seed, new GenOptions { Windows = true });

    /// <summary>The script with an option of Adapter.cs set (markers: false, merge: "pairwise"), which only Ycs reads.</summary>
    private static Dictionary<string, object> With(Dictionary<string, object> script, string key, object value)
    {
        var copy = (Dictionary<string, object>)Clone(script);
        copy[key] = value;
        return copy;
    }

    /// <summary>Runs two variants of a script step by step and returns the first difference <paramref name="compare"/> finds.</summary>
    private static Violation Lockstep(string property, Dictionary<string, object> a, Dictionary<string, object> b, Func<ScriptRun, Dictionary<string, object>, ScriptRun, Dictionary<string, object>, string> compare) =>
        Guard(property, () =>
        {
            var runA = new ScriptRun(a);
            var runB = new ScriptRun(b);
            var steps = a.List("steps").Cast<Dictionary<string, object>>().ToList();
            for (var i = 0; i <= steps.Count; i++)
            {
                var obsA = i < steps.Count ? runA.Step(steps[i]) : runA.End();
                var obsB = i < steps.Count ? runB.Step(steps[i]) : runB.End();
                if (compare(runA, obsA, runB, obsB) is { } detail)
                {
                    var at = i < steps.Count ? $"step {i} ({steps[i].Str("op")})" : "end";
                    return new Violation(property, detail.Split(':')[0], $"after {at}: {detail}");
                }
            }

            return null;
        });

    private static Violation MarkersInvisible(Dictionary<string, object> script) =>
        Lockstep("markers-invisible", script, With(script, "markers", false), (on, obsOn, _, obsOff) =>
        {
            foreach (var c in on.Clients)
            {
                if (c.Replica.SearchMarkerProblems() is { } problem)
                {
                    return "marker invalid: client " + c.Index + ": " + problem;
                }
            }

            var (a, b) = (Canonical(obsOn), Canonical(obsOff));
            return a == b ? null : $"observation differs: on {Short(a)}{Environment.NewLine}  off {Short(b)}";
        });

    private static Violation MergeOrder(Dictionary<string, object> script) =>
        SameStores("merge-order", With(script, "extend", false), With(With(script, "extend", false), "merge", "pairwise"), markers: true);

    // The search markers too: between steps every marker sits where Yjs' would (Yjs' lookup walks back to the start
    // of a run, and a marker moved off a deleted item lands on Yjs' item: YArrayBase.UpdateMarkerChanges).
    // As the script has it (quiet clients extend runs, the observed ones split them before the observers) and with
    // every client quiet.
    private static Violation RunExtension(Dictionary<string, object> script) =>
        SameStores("run-extension", script, With(With(script, "extend", false), "merge", "pairwise"), markers: true)
        ?? SameStores("run-extension", With(script, "quiet", true), With(With(With(script, "quiet", true), "extend", false), "merge", "pairwise"), markers: true);

    // Eager: the candidate asks the index from the first step of a walk, so that short lists use it too.
    private static Violation WalkIndex(Dictionary<string, object> script) =>
        SameStores("walk-index", With(script, "walk", "eager"), With(script, "walk", "plain"), markers: true, lookups: true)
        ?? SameStores("walk-index", With(With(script, "walk", "eager"), "extend", false), With(With(script, "walk", "plain"), "extend", false), markers: true, lookups: true);

    private static Violation SameStores(string property, Dictionary<string, object> candidate, Dictionary<string, object> reference, bool markers, bool lookups = false) =>
        Lockstep(property, candidate, reference, (a, obsA, b, obsB) =>
        {
            for (var k = 0; k < a.Clients.Count && lookups; k++)
            {
                var (x, y) = (a.Clients[k].Replica.TakeMarkerLookups(), b.Clients[k].Replica.TakeMarkerLookups());
                if (x != y)
                {
                    return $"lookup differs: client {k}: {Short(x)}{Environment.NewLine}  reference {Short(y)}";
                }
            }

            for (var k = 0; k < a.Clients.Count; k++)
            {
                var (x, y) = (a.Clients[k].Replica.StoreDump(markers), b.Clients[k].Replica.StoreDump(markers));
                if (x != y)
                {
                    var line = x.Split('\n').Zip(y.Split('\n')).First(p => p.First != p.Second);
                    return $"store differs: client {k}: {line.First}{Environment.NewLine}  reference {line.Second}";
                }
            }

            var (u, v) = (Canonical(obsA), Canonical(obsB));
            return u == v ? null : $"observation differs: {Short(u)}{Environment.NewLine}  reference {Short(v)}";
        });

    private static Violation NaiveRecompute(Dictionary<string, object> script) =>
        StepWise(script, (run, _) =>
        {
            foreach (var c in run.Clients)
            {
                if (c.Replica.NaiveRecomputationDifferences() is { Count: > 0 } d)
                {
                    return new Violation("naive-recompute", "mismatch", string.Join("; ", d));
                }
            }

            return null;
        }) is { } v ? v with { Property = "naive-recompute" } : null;

    private static Violation ItemListInvariantsHold(Dictionary<string, object> script)
    {
        var previous = new Dictionary<(int, string), Corpus.ItemListInvariants.Report>();
        var v = StepWise(script, (run, _) =>
        {
            foreach (var c in run.Clients)
            {
                foreach (var (path, runs) in c.Replica.Sequences())
                {
                    // Only a root keeps its path; a nested sequence's index can move.
                    var key = (c.Index, path);
                    var isRoot = !path.Contains(',', StringComparison.Ordinal);
                    var report = Corpus.ItemListInvariants.Check(runs, full: runs.Count < 200, isRoot ? previous.GetValueOrDefault(key) : null);
                    if (report.Violations.Count > 0)
                    {
                        return new Violation("item-list-invariants", report.Violations[0].Split(':')[0], $"{path}: {string.Join("; ", report.Violations)}");
                    }

                    if (isRoot)
                    {
                        previous[key] = report;
                    }
                }
            }

            return null;
        });
        return v == null ? null : v with { Property = "item-list-invariants" };
    }

    private static Violation Idempotence(Dictionary<string, object> script) => Guard("idempotence", () =>
    {
        var (run, observations) = Execute(script);
        if (HasErrors(observations))
        {
            return null;
        }

        foreach (var c in run.Clients)
        {
            var r = c.Replica;
            var before = (Content(r), Sv(r), Convert.ToBase64String(r.EncodeV1()), c.Log.Count);
            foreach (var u in Updates(run).Where(u => r.Contains(u.V1)))
            {
                r.ApplyV1(u.V1);
                r.ApplyV2(u.V2);
            }

            var after = (Content(r), Sv(r), Convert.ToBase64String(r.EncodeV1()), c.Log.Count);
            var v = Differs("idempotence", "content", before.Item1, after.Item1)
                ?? Differs("idempotence", "sv", before.Item2, after.Item2)
                ?? Differs("idempotence", "bytes", before.Item3, after.Item3)
                ?? (before.Item4 == after.Item4 ? null : new Violation("idempotence", "local updates", $"{after.Item4 - before.Item4} new local update(s)"));
            if (v != null)
            {
                return v with { Detail = $"client {r.ClientId}: {v.Detail}" };
            }
        }

        return null;
    });

    /// <summary>
    /// The script, then two fresh clients (X, Y; gc on) that receive the same random part of all
    /// updates, then two more updates a and b, X in the order a, b and Y in the order b, a; then X and
    /// Y sync both ways (their own formatting cleanups are updates too). All of it is script steps, so
    /// a minimal failing script runs as it is in the oracle (YCS_PROP_EXPLAIN).
    /// </summary>
    private static Dictionary<string, object> CommutativityScript(ulong seed)
    {
        var script = Generator.Generate(seed, new GenOptions { FinalSync = 0 });
        var (run, _) = Execute(script);
        var updates = Updates(run).Select(u => (u.Client, u.K)).ToList();
        if (updates.Count < 2)
        {
            return script;
        }

        var rng = new Rng(seed * 6151);
        var clients = script.List("clients");
        var x = (long)clients.Count;
        var y = x + 1;
        clients.Add(Obj(("id", new JsonNumber("1000001")), ("gc", true)));
        clients.Add(Obj(("id", new JsonNumber("1000002")), ("gc", true)));
        var a = rng.Pick(updates);
        var b = rng.Pick(updates);
        var basis = updates.Where(u => u != a && u != b && rng.Chance(0.5)).ToList();
        var steps = script.List("steps");
        Dictionary<string, object> Deliver((int Client, int K) u, long to) =>
            Normalized(Obj(("op", "deliver"), ("from", (long)u.Client), ("k", (long)u.K), ("to", to), ("via", "v1")));
        foreach (var u in basis)
        {
            steps.Add(Deliver(u, x));
            steps.Add(Deliver(u, y));
        }

        steps.AddRange([Deliver(a, x), Deliver(b, x), Deliver(b, y), Deliver(a, y)]);
        for (var round = 0; round < 3; round++)
        {
            steps.Add(Normalized(Obj(("op", "sync"), ("from", x), ("to", y), ("via", "v1"))));
            steps.Add(Normalized(Obj(("op", "sync"), ("from", y), ("to", x), ("via", "v1"))));
        }

        return script;
    }

    private static Violation Commutativity(Dictionary<string, object> script) => Guard("commutativity", () =>
    {
        var clients = script.List("clients").Count;
        if (clients < 2)
        {
            return null;
        }

        // X and Y must still be there and have been given the same updates (shrinking may change that).
        var ids = script.List("clients").Cast<Dictionary<string, object>>().Select(c => c.Int("id")).ToList();
        var (x, y) = (ids.IndexOf(1000001), ids.IndexOf(1000002));
        if (x < 0 || y < 0)
        {
            return null;
        }

        var steps = script.List("steps").Cast<Dictionary<string, object>>().ToList();
        string Received(int to) => string.Join(",", steps.Where(s => s.Str("op") == "deliver" && s.Int("to") == to).Select(s => $"{s.Int("from")}.{s.Int("k")}").Order(StringComparer.Ordinal));
        bool Extra(Dictionary<string, object> s) => s.Str("op") is "sync" or "merged" && (s.Int("to") == x || s.Int("to") == y)
            && (s.Str("op") == "merged" || (s.Int("from") != x && s.Int("from") != y));
        if (Received(x) != Received(y) || steps.Any(Extra))
        {
            return null;
        }

        var (run, _) = Execute(script);
        var (rx, ry) = (run.Clients[x].Replica, run.Clients[y].Replica);
        return Differs("commutativity", "content", Content(rx), Content(ry))
            ?? Differs("commutativity", "sv", Sv(rx), Sv(ry))
            ?? (rx.HasPending == ry.HasPending ? null : new Violation("commutativity", "pending", $"{rx.HasPending} vs {ry.HasPending}"));
    });

    private static Violation RejectsCleanly(Dictionary<string, object> script) => Guard("rejects-cleanly", () =>
    {
        var run = new ScriptRun(script);
        foreach (Dictionary<string, object> step in script.List("steps"))
        {
            run.Step(step);
        }

        if (run.RejectionViolations.Count != 0)
        {
            return new Violation("rejects-cleanly", run.RejectionViolations[0].Split(':')[0], string.Join("; ", run.RejectionViolations));
        }

        // Whatever was accepted, the state of every client applies to a fresh document, in both formats: a room that
        // accepted an update no document can load is as good as broken.
        foreach (var c in run.Clients)
        {
            var r = c.Replica;
            var v = Differs("rejects-cleanly", "state applies v1", Sv(r), Sv(FreshWith(run, r, v2: false)))
                ?? Differs("rejects-cleanly", "state applies v2", Sv(r), Sv(FreshWith(run, r, v2: true)));
            if (v != null)
            {
                return v;
            }
        }

        return null;
    });

    private static Violation EventReplay(Dictionary<string, object> script) => Guard("event-replay", () =>
        EventReplayOn(script, Execute(script).Observations));

    /// <summary>The event-replay check on any observations (Ycs' or Yjs'), so that the property itself can be validated on Yjs.</summary>
    public static Violation EventReplayOn(Dictionary<string, object> script, IReadOnlyList<Dictionary<string, object>> observations)
    {
        var roots = script.List("roots").Cast<Dictionary<string, object>>().Select(r => (Name: r.Str("name"), Kind: r.Str("kind"))).ToList();
        var shadows = new Dictionary<(long, string), Shadow>();
        var steps = script.List("steps");
        for (var i = 0; i < steps.Count; i++)
        {
            var obs = observations[i];
            foreach (var e in obs.List("ev")?.Cast<Dictionary<string, object>>() ?? [])
            {
                var w = e.Str("w");
                if (w == null || !w.StartsWith("obs:", StringComparison.Ordinal))
                {
                    continue;
                }

                var root = w[4..];
                var kind = roots.First(r => r.Name == root).Kind;
                var key = (e.Int("c"), root);
                if (!shadows.TryGetValue(key, out var shadow))
                {
                    shadows[key] = shadow = new Shadow(kind);
                }

                var state = (Dictionary<string, object>)((Dictionary<string, object>)obs["st"])[e.Int("c").ToString(System.Globalization.CultureInfo.InvariantCulture)];
                var current = (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)state["roots"])[root])["v"];
                foreach (Dictionary<string, object> ev in e.List("es"))
                {
                    shadow.Apply(ev, current);
                }
            }

            foreach (var ((client, root), shadow) in shadows)
            {
                if (obs.Get("st") is not Dictionary<string, object> st || st.Get(client.ToString(System.Globalization.CultureInfo.InvariantCulture)) is not Dictionary<string, object> state)
                {
                    continue;
                }

                var current = (Dictionary<string, object>)((Dictionary<string, object>)((Dictionary<string, object>)state["roots"])[root])["v"];
                var v = Differs("event-replay", shadow.Kind, shadow.Render(), Shadow.RenderCurrent(shadow.Kind, current));
                if (v != null)
                {
                    return v with { Detail = $"client {client}, root {root}, after step {i} ({((Dictionary<string, object>)steps[i]).Str("op")}): {v.Detail}" };
                }
            }
        }

        return null;
    }

    /// <summary>A copy of a root built only from its observer's events.</summary>
    private sealed class Shadow(string rootKind)
    {
        public string Kind { get; } = rootKind;

        // text: (insert, attributes) per unit run; array/xml: values (shallow); map: key -> value (shallow).
        private readonly List<(object Insert, Dictionary<string, object> Attrs)> _text = new();
        private readonly List<object> _items = new();
        private readonly SortedDictionary<string, object> _map = new(StringComparer.Ordinal);

        public void Apply(Dictionary<string, object> ev, Dictionary<string, object> current)
        {
            switch (Kind)
            {
                case "text":
                    ApplyText(ev.List("d") ?? []);
                    break;
                case "array":
                case "xml":
                    ApplyItems(ev.List("d") ?? []);
                    break;
                case "map":
                    var values = (Dictionary<string, object>)current["v"];
                    foreach (var (key, change) in (ev.Get("keys") as Dictionary<string, object>) ?? [])
                    {
                        if (((Dictionary<string, object>)change).Str("action") == "delete")
                        {
                            _map.Remove(key);
                        }
                        else
                        {
                            _map[key] = Shallow(values.GetValueOrDefault(key));
                        }
                    }

                    break;
            }
        }

        private static int UnitLength(object insert) => insert is string s ? s.Length : 1;

        private void ApplyText(List<object> delta)
        {
            // Expand to single units, apply, then render merged runs.
            var units = new List<(object Insert, Dictionary<string, object> Attrs)>();
            foreach (var (insert, attrs) in _text)
            {
                if (insert is string s)
                {
                    units.AddRange(s.Select(ch => ((object)ch.ToString(), attrs)));
                }
                else
                {
                    units.Add((insert, attrs));
                }
            }

            var pos = 0;
            foreach (Dictionary<string, object> op in delta)
            {
                var attributes = op.Get("attributes") as Dictionary<string, object>;
                if (op.Has("retain"))
                {
                    var n = (int)op.Int("retain");
                    for (var k = pos; k < pos + n && k < units.Count; k++)
                    {
                        units[k] = (units[k].Insert, Merge(units[k].Attrs, attributes));
                    }

                    pos += n;
                }
                else if (op.Has("delete"))
                {
                    units.RemoveRange(pos, Math.Min((int)op.Int("delete"), units.Count - pos));
                }
                else
                {
                    var insert = op["insert"];
                    var clean = Merge(null, attributes);
                    if (insert is string s)
                    {
                        units.InsertRange(Math.Min(pos, units.Count), s.Select(ch => ((object)ch.ToString(), clean)));
                        pos += s.Length;
                    }
                    else
                    {
                        units.Insert(Math.Min(pos, units.Count), (Shallow(insert), clean));
                        pos++;
                    }
                }
            }

            _text.Clear();
            _text.AddRange(units);
        }

        private static Dictionary<string, object> Merge(Dictionary<string, object> attrs, Dictionary<string, object> change)
        {
            var result = attrs == null ? new Dictionary<string, object>() : new Dictionary<string, object>(attrs);
            foreach (var (k, v) in change ?? [])
            {
                if (v == null)
                {
                    result.Remove(k);
                }
                else
                {
                    result[k] = v;
                }
            }

            return result.Count == 0 ? null : result;
        }

        private void ApplyItems(List<object> delta)
        {
            var pos = 0;
            foreach (Dictionary<string, object> op in delta)
            {
                if (op.Has("retain"))
                {
                    pos += (int)op.Int("retain");
                }
                else if (op.Has("delete"))
                {
                    _items.RemoveRange(pos, Math.Min((int)op.Int("delete"), _items.Count - pos));
                }
                else
                {
                    var inserted = ((List<object>)op["insert"]).Select(Shallow).ToList();
                    _items.InsertRange(Math.Min(pos, _items.Count), inserted);
                    pos += inserted.Count;
                }
            }
        }

        /// <summary>A value with nested shared types reduced to their kind (their content changes without events of this root).</summary>
        private static object Shallow(object v) => v is Dictionary<string, object> d && d.ContainsKey("$t")
            ? Obj(("$t", d["$t"]), ("n", d.Get("n") ?? Json.Missing.Value))
            : v;

        public string Render() => Kind switch
        {
            "text" => Canonical(RenderText(_text)),
            "map" => Canonical(_map.ToDictionary(kv => kv.Key, kv => kv.Value)),
            _ => Canonical(_items),
        };

        private static List<object> RenderText(IEnumerable<(object Insert, Dictionary<string, object> Attrs)> units)
        {
            var ops = new List<object>();
            string text = null;
            Dictionary<string, object> textAttrs = null;
            void Flush()
            {
                if (text != null)
                {
                    ops.Add(Op(text, textAttrs));
                    text = null;
                }
            }

            foreach (var (insert, attrs) in units)
            {
                if (insert is string s)
                {
                    if (text != null && Canonical(attrs) == Canonical(textAttrs))
                    {
                        text += s;
                        continue;
                    }

                    Flush();
                    (text, textAttrs) = (s, attrs);
                }
                else
                {
                    Flush();
                    ops.Add(Op(insert, attrs));
                }
            }

            Flush();
            return ops;
        }

        private static Dictionary<string, object> Op(object insert, Dictionary<string, object> attrs)
        {
            var op = Obj(("insert", insert));
            if (attrs is { Count: > 0 })
            {
                op["attributes"] = attrs;
            }

            return op;
        }

        /// <summary>The current root view rendered as the shadow renders itself.</summary>
        public static string RenderCurrent(string kind, Dictionary<string, object> view) => kind switch
        {
            // Re-split and re-merge, so that runs Yjs keeps apart (separate items, same attributes) compare equal.
            "text" => Canonical(RenderText(((List<object>)view["d"]).Cast<Dictionary<string, object>>().SelectMany(Units))),
            "map" => Canonical(((Dictionary<string, object>)view["v"]).ToDictionary(kv => kv.Key, kv => Shallow(kv.Value))),
            _ => Canonical(((List<object>)view["v"]).Select(Shallow).ToList()),
        };

        private static IEnumerable<(object, Dictionary<string, object>)> Units(Dictionary<string, object> op)
        {
            var attrs = op.Get("attributes") as Dictionary<string, object>;
            if (op["insert"] is string s)
            {
                return s.Select(ch => ((object)ch.ToString(), attrs));
            }

            return [(Shallow(op["insert"]), attrs)];
        }
    }
}
