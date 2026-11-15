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
        new("merge-structs-merges-gc-with-item", "high",
            "UpdateOperations.MergeStructs (MergeUpdates, DiffUpdate, ConvertUpdateFormat*) merges a GC struct with the " +
            "Item that follows it: GC.MergeWith asserts 'right is GC' (Debug) or swallows the item into the GC (Release), so " +
            "valid updates throw or lose content (here: the second array element) when merged or converted. Yjs only merges " +
            "structs of the same kind.",
            """{"clients":[{"gc":true,"id":5},{"gc":false,"id":20},{"gc":false,"id":2147482910}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":2,"op":"push","path":["a"],"values":[{"$t":"array"},false]},{"from":2,"op":"sync","to":0,"via":"v1"},{"c":0,"op":"transact","ops":[{"at":0,"op":"ains","path":["a",0],"values":[0]},{"at":0,"len":1,"op":"delete","path":["a"]}]},{"c":0,"op":"push","path":["a"],"values":[{"$b":"29g="}]},{"from":[[2,1],[2,0],[0,1],[0,0],[0,2]],"op":"merged","to":1,"via":"v1"}]}""")
        {
            Matches = (m, script) => m.Op == "merged" || (m.Op == "deliver" && Step(script, m).Str("via") is "v1to2" or "v2to1"),
        },
        new("text-event-delta-repeats-inserts", "high",
            "YTextEvent.Delta: an insert that follows another insert with different attributes (or an embed) repeats what " +
            "came before it: inserting \"a\" then \"a\" {color: 0} reports [a][aa {color: 0}] (three characters for two), an " +
            "embed followed by \"a\" reports \"System.Collections.Generic.Dictionary`2[...]a\". Bindings that apply the " +
            "event's delta corrupt the editor; the document itself is right.",
            """{"clients":[{"gc":false,"id":2147483332}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":0,"delta":[{"insert":"a"},{"attrs":{"color":0},"insert":"a"}],"op":"delta","path":["t"]}]}""")
        {
            Matches = (m, _) => m.Differences.Any(RepeatedInsert),
        },
        new("relative-position-end-of-nested-type", "high",
            "RelativePosition.FromTypeIndex at the end of a nested type (an empty Y.XmlText in a fragment, the end of a " +
            "Y.Text in an array) stores the type's own item as the position's item instead of as its type, so it resolves to " +
            "index 0 of the parent (['x'], 0) instead of the end of the nested type (['x', 0], 2); Yjs encodes " +
            "{type: id, assoc}. Cursors at the end of a paragraph jump.",
            """{"clients":[{"gc":false,"id":491458}],"roots":[{"kind":"xml","name":"x"}],"steps":[{"at":0,"c":0,"op":"ains","path":["x"],"values":[{"$t":"xmlt"}]},{"at":0,"c":0,"id":"r1","op":"rpos","path":["x",0]},{"at":0,"c":0,"op":"insert","path":["x",0],"text":"ab"},{"c":0,"id":"r1","op":"rres"}]}""")
        {
            Matches = (m, _) => m.Op is "rpos" or "rres" && m.Primary == "r",
        },
        new("undo-redo-nested-type-not-copied", "high",
            "Undoing the deletion of a nested shared type, or redoing its insertion, throws NotImplementedException from " +
            "AbstractType.InternalCopy (ContentType.Copy in Transaction.RedoItem): YText, YArray, YMap, ... do not " +
            "implement it. The undo/redo transaction ends half done; Yjs re-creates the type with its content (_copy).",
            """{"clients":[{"gc":false,"id":9}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":0,"id":"u0","op":"um","scope":[["a"]],"timeout":"inf"},{"at":0,"c":0,"op":"ains","path":["a"],"values":[{"$t":"text"}]},{"c":0,"id":"u0","op":"undo"},{"c":0,"id":"u0","op":"redo"}]}""")
        {
            Matches = (m, script) => m.Op is "undo" or "redo" && m.Signature.EndsWith(" / ycs threw", StringComparison.Ordinal),
        },
        new("undo-property-nested-type", "high",
            "The undo-all-restores and undo-redo-pair properties fail for the same reason as undo-redo-nested-type-not-copied.",
            """{"clients":[{"gc":false,"id":5}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":0,"id":"all","op":"um","scope":[["a"]],"timeout":"0"},{"at":0,"c":0,"op":"ains","path":["a"],"values":[{"$t":"text"}]},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"redo"}]}""")
        {
            Property = "undo-",
        },
        new("undo-reverts-untracked-remote-change", "high",
            "An UndoManager with the default tracked origins (null) reverts changes it does not track along with the one it " +
            "does: a remote insertion (origin \"net\") into a nested Y.XmlText, or a local insertion with origin \"o\" " +
            "(here: undoing a tracked delete also deletes the untracked map); Yjs leaves them alone.",
            """{"clients":[{"gc":false,"id":2},{"gc":false,"id":4}],"roots":[{"kind":"text","name":"t"},{"kind":"xml","name":"x"}],"steps":[{"c":1,"id":"u0","op":"um","scope":[["x"],["t"]],"timeout":"inf"},{"at":0,"c":0,"op":"ains","path":["x"],"values":[{"$t":"xmlt"},{"$t":"xmlt"}]},{"from":0,"k":0,"op":"deliver","to":1,"via":"v1"},{"at":0,"c":1,"op":"insert","path":["x",0],"text":"a"},{"at":0,"c":0,"op":"insert","path":["x",1],"text":"a"},{"from":0,"k":1,"op":"deliver","to":1,"via":"v2"},{"at":0,"c":1,"op":"insert","path":["t"],"text":"a"},{"c":1,"id":"u0","op":"undo"}]}""")
        {
            Matches = (m, _) => m.Op == "undo" && m.Differences.Any(d => Regex.IsMatch(d, @"^\.ev\[\d+\]\.es\[\d+\]: yjs ""\(missing\)"" \| ycs \{")
                || (d.Contains(".roots.", StringComparison.Ordinal) && d.EndsWith("| ycs \"(missing)\"", StringComparison.Ordinal))),
        },
        new("remote-overwritten-entry-dropped", "medium",
            "Applying a remote update from a gc'd client in which a map entry holding a nested type was overwritten: Ycs " +
            "leaves the overwritten entry's struct out (its transaction's Changed lacks the key, its update and its state " +
            "lack the struct); Yjs integrates it as a deleted item. The visible content agrees.",
            """{"clients":[{"gc":false,"id":5},{"gc":false,"id":2147482848},{"gc":true,"id":2147482834}],"roots":[{"kind":"array","name":"a"},{"kind":"map","name":"m"}],"steps":[{"c":2,"key":"b","op":"set","path":["m"],"value":0},{"from":2,"k":0,"op":"deliver","to":0,"via":"v2"},{"at":0,"c":2,"op":"ains","path":["a"],"values":[{"$t":"text"}]},{"at":0,"c":2,"op":"insert","path":["a",0],"text":"a"},{"at":0,"c":2,"len":1,"op":"delete","path":["a"]},{"c":1,"key":"k1","op":"set","path":["m"],"value":{"$t":"array"}},{"c":2,"op":"transact","ops":[{"key":"k2","op":"set","path":["m"],"value":{"$t":"array","v":[{}]}},{"op":"clear","path":["m"]}]},{"from":1,"op":"sync","to":0,"via":"v1"},{"c":2,"key":"k1","op":"set","path":["m"],"value":{"$t":"xmle","n":"span"}},{"from":2,"k":4,"op":"deliver","to":0,"via":"v1"},{"from":2,"k":5,"op":"deliver","to":0,"via":"v2to1"},{"from":2,"k":2,"op":"deliver","to":0,"via":"v2to1"},{"from":2,"k":1,"op":"deliver","to":0,"via":"v1"}]}""")
        {
            Matches = (m, _) => m.Differences.Any(d => Regex.IsMatch(d, @"^\.ev\[\d+\]\.changed\[")),
        },
        new("convert-v1-to-v2-overlapping-delete-set", "low",
            "UpdateOperations.ConvertUpdateFormatV1ToV2 of a valid v1 update whose delete set is not sorted, or has " +
            "overlapping or repeated ranges (Yjs' v1 decoder takes them), writes a v2 delete set that Ycs' own v2 decoder rejects ('A " +
            "variable-length integer is out of range', from the negative clock difference). Yjs' output applies but is " +
            "corrupt in the same way (see yjs-unsorted-delete-set): neither converts such an update faithfully.",
            """{"clients":[{"gc":false,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AgGsAgIEAKwCAQN4w6kC8KIECoesAgEGx/CiBAqsAgEDA2RpdgILAgADAAKsAgEBAg==","via":"v1to2"}]}""")
        {
            Matches = (m, script) => m.Op is "raw" or "deliver" && Step(script, m).Str("via") == "v1to2" && m.Signature.EndsWith(" / ycs threw", StringComparison.Ordinal),
        },
        new("text-todelta-omits-nested-types", "medium",
            "YText.ToDelta (and the text's view) leaves out a shared type embedded in the text (ContentType, as a peer " +
            "inserts it with insertEmbed(index, new Y.Text())): Yjs' delta has {insert: <the type>} where Ycs has nothing.",
            """{"clients":[{"gc":false,"id":2}],"roots":[{"kind":"text","name":"t"}],"steps":[{"op":"raw","to":0,"u":"AwWsAgAHAQF4BgMBAWEDAQIDBACsAgAD5LithqwCAgRib2xkBHRydWXGrAICrAIDBml0YWxpYwR0cnVlAgsACAEBYQJ9R3s/+AAAAAAAAMQKAAoAA3jDqQEKAAcBAXQCAA==","via":"v1"}]}""")
        {
            Matches = (m, _) => m.Differences.Any(d => Regex.IsMatch(d, @"(\.insert: yjs \{""\$t"":|: yjs \{""insert"":\{""\$t"":|\.insert\.\$t: yjs ""|yjs \{.*""insert"":\{""\$t"":.*\| ycs ""\(missing\)"")")),
        },
        new("odd-origins-integrate-elsewhere", "medium",
            "A valid update whose items have origins in other parents (an origin on a deleted item of a nested array, " +
            "origin equal to right origin), as the structure-aware fuzzer writes them: Ycs integrates an item into a " +
            "different parent than Yjs (the deep event is on x where Yjs' is on a), although docs/api.md says such " +
            "updates are integrated as Yjs integrates them.",
            """{"clients":[{"gc":false,"id":2}],"roots":[{"kind":"array","name":"a"},{"kind":"xml","name":"x"}],"steps":[{"op":"raw","to":0,"u":"AwXwogQABAEBdAFhBwEBYQAHAQF4BgEA8KIEAQEHAQF4BgKsAgAGAPCiBAQGaXRhbGljATHI8KIEA/CiBAMBeAMMAMfwogQC8KIEAgYoAQFtAWwCdwFzdwFzBAAMAAFhAvCiBAEDAQwBAAM=","via":"v1"},{"op":"raw","to":0,"u":"AQKsAgKH8KIEBwYBAPCiBAIBAvj6//8HAQIC8KIEAgMCBwM=","via":"v1to2"},{"op":"raw","to":0,"u":"AgHwogQFxPCiBADwogQAAmJjA/j6//8HAIjwogQDAncBc3cBcyIBAW0BbQIBMQMiaiKHDAADAXAA","via":"v1to2"},{"op":"raw","to":0,"u":"AQHwogQHBwD4+v//BwQGBPCiBAEAAqwCAQAC+Pr//wcBBAIMAQMC","via":"v1"}]}""")
        {
            Matches = (m, _) => m.Op == "raw" && m.Differences.Any(d => Regex.IsMatch(d, @"^\.ev\[\d+\]\.w: yjs ""deep:")),
        },
        new("gc-cleanup-throws-on-crafted-update", "medium",
            "Two valid-looking updates (a nested type whose items are deleted while children of it arrive with parentSubs) " +
            "make ApplyUpdate throw InvalidOperationException from Item.Gc in the transaction cleanup, after the update was " +
            "integrated: not YUpdateException, and the document is changed, against docs/api.md ('the integration does not " +
            "fail on input alone'). Yjs throws too ('Unexpected case' in Item.gc), so the differential agrees; the contract does not.",
            """{"clients":[{"gc":true,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AwKsAgAIAQFhA353AXN9AQQBAXQD5LitAvj6//8HAIesAgIBhKwCAwJiYwPwogQAh6wCAAEoAPCiBAABbQF9RygA+Pr//wcAAWwDfUd4eAA=","via":"v1"},{"op":"raw","to":0,"u":"AQUKACgA+Pr//wcAAWsCeH0BxPj6//8HAfj6//8HAQJiYygA8KIEAAFsAnh9R4fwogQAAQoCA/CiBAEDAfj6//8HAQADCgIBAwQC","via":"v1"}]}""")
        {
            Property = "rejects-cleanly: threw InvalidOperationException",
        },
        new("observer-order-across-types", "low",
            "When one remote transaction changes several types, Ycs calls their observers in another order than Yjs " +
            "(Yjs: each changed type's observers in the order the types were changed, then the deep observers; here Ycs " +
            "calls a's deep observer before t's observer).",
            """{"clients":[{"gc":false,"id":378036},{"gc":true,"id":12}],"roots":[{"kind":"text","name":"t"},{"kind":"array","name":"a"}],"steps":[{"c":1,"op":"unshift","path":["a"],"values":[{"$t":"text"}]},{"at":0,"c":1,"op":"insert","path":["a",0],"text":"a"},{"c":1,"op":"transact","ops":[{"at":0,"attrs":{"size":0},"len":1,"op":"format","path":["a",0]},{"at":0,"len":1,"op":"delete","path":["a"]}]},{"at":0,"c":1,"op":"insert","path":["t"],"text":"a"},{"from":1,"k":2,"op":"deliver","to":0,"via":"v2"},{"from":1,"k":3,"op":"deliver","to":0,"via":"v1"},{"from":1,"k":1,"op":"deliver","to":0,"via":"v1"},{"from":1,"k":0,"op":"deliver","to":0,"via":"v2"}]}""")
        {
            Matches = (m, _) => m.Differences.Any(d => Regex.IsMatch(d, @"^\.ev\[\d+\]\.w: yjs ""(obs|deep):[^""]*"" \| ycs ""(obs|deep):")),
        },
        new("push-after-deleted-tail", "medium",
            "YArray.Push (and YXmlFragment.Push) inserts at index Length, i.e. before the deleted items at the end, with the " +
            "first of them as right origin; Yjs' push appends after the last item, deleted or not (it is the left origin). " +
            "The updates differ, and so does how a concurrent insert next to the deleted tail is ordered.",
            """{"clients":[{"gc":false,"id":16}],"roots":[{"kind":"array","name":"a"}],"steps":[{"at":0,"c":0,"op":"ains","path":["a"],"values":[1]},{"at":0,"c":0,"len":1,"op":"delete","path":["a"]},{"c":0,"op":"push","path":["a"],"values":["s"]}]}""")
        {
            Matches = (m, script) => m.Differences.Any(d => Regex.IsMatch(d, @"\.u: |\.v1: "))
                && (m.Op == "push" || (m.Op == "transact" && Step(script, m).List("ops").Cast<Dictionary<string, object>>().Any(o => o.Str("op") == "push"))),
        },
        new("deep-event-path-drops-steps", "medium",
            "YEvent.Path for an event of a nested type delivered to an ancestor's deep observer leaves out steps: a change " +
            "of m.k1 (a map in a map) has path [] instead of ['k1'], one of m.k2[0] has [0] instead of ['k2', 0].",
            """{"clients":[{"gc":false,"id":2147483157},{"gc":false,"id":10}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"key":"k1","op":"set","path":["m"],"value":{"$t":"map"}},{"from":0,"op":"sync","to":1,"via":"v1"},{"c":1,"op":"transact","ops":[{"key":"b","op":"set","path":["m"],"value":{"$t":"array"}},{"key":"c","op":"set","path":["m","k1"],"value":{"$t":"array"}}]}]}""")
        {
            Matches = (m, _) => m.Differences.Any(d => Regex.IsMatch(d, @"^\.ev\[\d+\]\.es\[\d+\]\.p\b")),
        },
        new("subdocument-options", "medium",
            "A subdocument's ContentDoc is written with options {gc, guid, autoLoad} where guid is a new random GUID " +
            "(YDocOptions' default), not the subdocument's; Yjs writes only what differs from the defaults ({gc: false}, " +
            "{autoLoad: true}, meta) and, reading Ycs' bytes, takes the random guid as the subdocument's guid.",
            """{"clients":[{"gc":false,"id":2147483114}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":0,"op":"push","path":["a"],"values":[{"$doc":{"autoLoad":false,"g":"doc-1"}}]}]}""")
        {
            Matches = (_, script) => Json.Canonical(script).Contains("\"$doc\"", StringComparison.Ordinal),
        },
        new("formatting-cleanup-deletes-more", "medium",
            "After a remote change, Ycs' formatting cleanup deletes a format item that Yjs' cleanupYTextAfterTransaction " +
            "keeps (here the one after \"a\"): the visible text and attributes agree, but the cleanup's update, the delete " +
            "set and the encoded state differ from a Yjs peer's.",
            """{"clients":[{"gc":false,"id":4},{"gc":false,"id":18}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":0,"delta":[{"insert":"😀"}],"op":"delta","path":["t"]},{"from":0,"k":0,"op":"deliver","to":1,"via":"v1to2"},{"c":1,"delta":[{"insert":"a"},{"attrs":{"bold":null,"color":0},"retain":0}],"op":"delta","path":["t"]},{"at":0,"c":0,"len":1,"op":"delete","path":["t"]},{"from":0,"op":"sync","to":1,"via":"v1"}]}""")
        {
            // The cleanup's deletions differ: other delete sets or update bytes, the same visible content.
            Matches = (m, _) => !m.Differences.Any(d => Regex.IsMatch(d, @"\.roots\.[^.]+\.(j|v)\b"))
                && (m.Differences.Any(d => d.Contains(".ds.", StringComparison.Ordinal)) || m.Differences.Any(d => Regex.IsMatch(d, @"^\.ev\[\d+\]\.u: "))),
        },
        new("format-event-spurious-removal", "medium",
            "YTextEvent.Delta of a Format reports a removal ({retain: 2, attributes: {size: null}}) of an attribute the " +
            "characters after the range never had; Yjs' event ends with the formatted range.",
            """{"clients":[{"gc":false,"id":606426}],"roots":[{"kind":"text","name":"t"}],"steps":[{"at":0,"c":0,"op":"insert","path":["t"],"text":"👍🏽"},{"at":1,"attrs":{"size":true},"c":0,"len":1,"op":"format","path":["t"]},{"at":0,"attrs":{"color":0,"size":true},"c":0,"len":1,"op":"format","path":["t"]}]}""")
        {
            Matches = (m, _) => m.Differences.Any(d => Regex.IsMatch(d, @"\.d\[\d+\]: yjs ""\(missing\)"" \| ycs \{""attributes""") || Regex.IsMatch(d, @"\.d\[\d+\]\.attributes: yjs null \| ycs \{")),
        },
        new("delete-event-misses-attribute-change", "medium",
            "YTextEvent.Delta of a Delete that removes the format items around the deleted range leaves out the change of " +
            "attributes of the character after it ({retain: 1, attributes: {italic: ...}} in Yjs' event); the document agrees.",
            """{"clients":[{"gc":false,"id":2147482983},{"gc":false,"id":722003}],"roots":[{"kind":"text","name":"t"}],"steps":[{"at":0,"c":0,"op":"insert","path":["t"],"text":"ba"},{"from":0,"k":0,"op":"deliver","to":1,"via":"v2"},{"c":1,"delta":[{"attrs":{"italic":"#"},"retain":2},{"attrs":{"italic":{"family":"s","w":0}},"retain":2}],"op":"delta","path":["t"]},{"at":0,"c":1,"op":"insert","path":["t"],"text":"👍🏽a中文"},{"at":6,"c":1,"len":4,"op":"delete","path":["t"]}]}""")
        {
            Matches = (m, _) => m.Differences.Any(d => Regex.IsMatch(d, @"\.d\[\d+\]: yjs \{(""attributes""|""retain"").* \| ycs ""\(missing\)""") || Regex.IsMatch(d, @"\.d\[\d+\]\.attributes(\.\w+)?: yjs .* \| ycs null$")),
        },
        new("embed-shared-type-rejected", "medium",
            "YText.InsertEmbed (and ApplyDelta) with a shared type (here a YXmlFragment) throws NotSupportedException " +
            "('cannot be stored in a Yjs document'): Ycs wraps every embed in ContentEmbed; Yjs inserts a shared type as " +
            "ContentType.",
            """{"clients":[{"gc":false,"id":13}],"roots":[{"kind":"text","name":"t"}],"steps":[{"at":0,"c":0,"op":"embed","path":["t"],"value":{"$t":"xmlf"}}]}""")
        {
            Matches = (m, _) => m.Op is "embed" or "delta" && m.Signature.EndsWith(" / ycs threw", StringComparison.Ordinal),
        },
        new("undo-without-effect-returns-item", "low",
            "UndoManager.Undo returns the stack item (and raises StackItemPopped) when undoing it changes nothing because " +
            "its target was overwritten; Yjs' undo pops such items and returns null.",
            """{"clients":[{"gc":false,"id":18}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"key":"c","op":"set","path":["m"],"value":{"$t":"array","v":[0]}},{"c":0,"id":"u1","op":"um","origins":["o1"],"scope":[["m"]],"timeout":"inf"},{"c":0,"op":"transact","ops":[{"at":0,"len":1,"op":"delete","path":["m","c"]}],"origin":"o1"},{"c":0,"key":"c","op":"set","path":["m"],"value":0},{"c":0,"id":"u1","op":"undo"}]}""")
        {
            Matches = (m, _) => m.Op is "undo" or "redo" && m.Differences.Any(d => d.StartsWith(".r.r: yjs false | ycs true", StringComparison.Ordinal)),
        },
        new("undo-stack-item-updated", "low",
            "A change the UndoManager merges into its last stack item (within captureTimeout) raises StackItemAdded again; " +
            "Yjs raises 'stack-item-updated', which Ycs does not have.",
            """{"clients":[{"gc":false,"id":4}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":0,"id":"u0","op":"um","scope":[["t"]],"timeout":"inf"},{"at":0,"c":0,"op":"insert","path":["t"],"text":"a"},{"at":1,"c":0,"op":"insert","path":["t"],"text":"b"}]}""")
        {
            // Yjs' 'stack-item-updated' (um~) where Ycs raises StackItemAdded (um+).
            Normalize = obs =>
            {
                foreach (var e in obs.List("ev")?.OfType<Dictionary<string, object>>() ?? [])
                {
                    if (e.Str("w") == "um~")
                    {
                        e["w"] = "um+";
                    }
                }
            },
        },
        new("stack-cleared-after-transaction", "low",
            "UndoManager.Clear raises StackCleared after its transaction ended; Yjs emits 'stack-cleared' inside it, before " +
            "the transaction's afterTransaction.",
            """{"clients":[{"gc":false,"id":17},{"gc":false,"id":8}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":1,"id":"u0","op":"um","scope":[["a"]],"timeout":"inf"},{"at":0,"c":0,"op":"ains","path":["a"],"values":[{"$t":"array"}]},{"from":[[0,0]],"op":"merged","to":1,"via":"v2"},{"at":0,"c":1,"len":1,"op":"delete","path":["a"]},{"c":1,"id":"u0","op":"umclear"}]}""")
        {
            Matches = (m, _) => m.Op == "umclear" && m.Primary == "ev:um",
        },
        new("gc-structs-not-merged", "low",
            "After garbage collection, adjacent GC structs (and deleted items) of one client are not merged as Yjs' " +
            "tryMergeDeleteSet/tryGc merge them, so EncodeStateAsUpdate(V2) is not byte-identical to Yjs' (same content).",
            """{"clients":[{"gc":true,"id":2147483286}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"key":"k1","op":"set","path":["m"],"value":{"$t":"map"}},{"c":0,"key":"c","op":"set","path":["m","k1"],"value":{"$t":"map","v":[["a",null]]}},{"c":0,"key":"c","op":"set","path":["m","k1"],"value":{"$t":"array"}},{"c":0,"key":"k1","op":"mdel","path":["m"]}]}""")
        {
            Matches = (m, _) => m.Primary is "v1" or "v2",
        },
        new("merge-property", "high",
            "The merge-partition property fails for the same reason as merge-structs-merges-gc-with-item (a GC struct " +
            "merged with the item after it; DebugAssertException 'right is GC' in Debug builds).",
            """{"clients":[{"gc":true,"id":19},{"gc":true,"id":3},{"gc":false,"id":530752}],"roots":[{"kind":"text","name":"t"},{"kind":"array","name":"a"},{"kind":"map","name":"m"}],"steps":[{"c":1,"op":"push","path":["a"],"values":[{"$i":"-2147483649"},{"$t":"xmlf","v":[{"$t":"xmle","a":[["class","c"]],"n":"span"}]}]},{"from":1,"k":0,"op":"deliver","to":1,"via":"v1"},{"from":1,"k":0,"op":"deliver","to":0,"via":"v1"},{"at":0,"c":0,"len":1,"op":"delete","path":["a",1]},{"from":2,"op":"sync","to":1,"via":"v1"},{"c":1,"key":"b","op":"set","path":["m"],"value":{"$t":"array","v":[{"$t":"text","s":"c"},"s7"]}},{"from":1,"k":2,"op":"deliver","to":2,"via":"v1to2"}]}""")
        {
            Property = "merge-partition",
        },
        new("event-replay-property", "high",
            "The event-replay property fails on text roots for the reasons of text-event-delta-repeats-inserts, " +
            "format-event-spurious-removal and delete-event-misses-attribute-change (it holds for Yjs: EventReplay_HoldsForYjs).",
            """{"clients":[{"gc":false,"id":2147483412}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":0,"delta":[{"insert":"a"},{"attrs":{"font":"#"},"insert":"a"}],"op":"delta","path":["t"]}]}""")
        {
            Property = "event-replay: text",
        },
        new("snapshot-array-read-merged-items", "documented",
            "Not a Ycs bug: Yjs' typeListToArraySnapshot returns values pushed after the snapshot when they were merged " +
            "into an item that existed before it (Yjs does not split the item at the snapshot); Ycs splits and returns " +
            "only the values the snapshot saw.",
            """{"clients":[{"gc":false,"id":3}],"roots":[{"kind":"array","name":"a"}],"steps":[{"at":0,"c":0,"op":"ains","path":["a"],"values":[1]},{"c":0,"id":"s3","op":"snap"},{"c":0,"op":"push","path":["a"],"values":[0]},{"c":0,"id":"s3","op":"readSnap","path":["a"]}]}""")
        {
            Documented = true,
            Matches = (m, _) => m.Op == "readSnap" && m.Differences.Any(d => Regex.IsMatch(d, @"^\.r\[\d+\]: yjs .* \| ycs ""\(missing\)""")),
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
        new("todelta-opens-a-transaction", "low",
            "YText.ToDelta() always runs in a transaction (origin \"splitSnapshotAffectedStructs\"), so reading a text " +
            "raises BeforeTransaction/AfterTransaction (and, inside an observer, starts a nested transaction); Yjs' toDelta " +
            "opens one only when given a snapshot, with origin 'cleanup'.",
            """{"clients":[{"id":1}],"roots":[{"name":"t","kind":"text"}],"steps":[{"op":"insert","c":0,"path":["t"],"at":0,"text":"a"}]}""")
        {
            // Drops the empty transactions both libraries open to read a text (Yjs only for snapshots).
            Normalize = obs =>
            {
                obs.List("ev")?.RemoveAll(e => e is Dictionary<string, object> r && r.Str("w") == "atx" && r.List("changed") is { Count: 0 }
                    && r.Str("origin") is "splitSnapshotAffectedStructs" or "cleanup");
            },
        },
        new("snapshot-array-read-opens-a-transaction", "low",
            "YArrayBase.ToArraySnapshot runs in a transaction (origin null), which raises AfterTransaction on a read; Yjs' " +
            "typeListToArraySnapshot does not.",
            """{"clients":[{"gc":false,"id":2147483431}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":0,"id":"s0","op":"snap"},{"c":0,"id":"s0","op":"readSnap","path":["a"]}]}""")
        {
            Matches = (m, _) => m.Op == "readSnap" && m.Primary == "ev:atx",
        },
        new("xml-tostring-non-string-values", "low",
            "YXmlElement/YXmlText.ToString render attribute and embed values with .NET's ToString (\"False\", " +
            "\"System.Collections.Generic.Dictionary`2[...]\"), and a formatting attribute whose value is an object or list " +
            "as a bare tag; Yjs uses JavaScript's String() (\"false\", \"[object Object]\") and renders the value's own keys " +
            "as attributes (<color 0=\"a\">).",
            """{"clients":[{"gc":false,"id":9}],"roots":[{"kind":"xml","name":"x"}],"steps":[{"at":0,"c":0,"op":"ains","path":["x"],"values":[{"$t":"xmle","n":"span"},{"$t":"xmle","n":"div","v":[{"$t":"xmlt","s":"a"}]}]},{"c":0,"key":"href","op":"setAttr","path":["x",0],"value":false},{"at":0,"attrs":{"bold":true,"color":["a"]},"c":0,"len":1,"op":"format","path":["x",1,0]}]}""")
        {
            Matches = (m, _) => m.Differences.All(d => Regex.IsMatch(d, @"^\.st\.\d+\.roots\.[^.]+\.j\b") && !d.Contains("$err", StringComparison.Ordinal)),
        },
        new("pending-left-out-of-encodings", "documented",
            "EncodeStateAsUpdate(V2) leaves out the structs and deletions that wait for a missing update (docs/api.md, " +
            "HasPending); Yjs 13.6 merges store.pendingStructs and store.pendingDs into encodeStateAsUpdate, so a Yjs peer " +
            "relays them and a Ycs peer does not.",
            """{"clients":[{"gc":false,"id":7},{"gc":false,"id":6}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"key":"c","op":"set","path":["m"],"value":[]},{"c":0,"key":"a","op":"set","path":["m"],"value":{"$t":"array"}},{"from":0,"k":1,"op":"deliver","to":1,"via":"v1"}]}""")
        {
            Documented = true,
            // A state with pending content: its encodings and delete set are not compared.
            Normalize = obs =>
            {
                if (obs.Get("st") is Dictionary<string, object> st)
                {
                    foreach (var state in st.Values.OfType<Dictionary<string, object>>())
                    {
                        if (state.Get("pend") is true)
                        {
                            state.Remove("v1");
                            state.Remove("v2");
                            state.Remove("ds");
                        }
                    }
                }
            },
            Matches = (m, _) => m.Op == "sync" && m.Primary == "r",
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
        new("yjs-throws", "documented",
            "Not a Ycs bug: Yjs throws on the operation (applyDelta([{delete: 0}]) on an empty text dereferences null; a " +
            "BigInt at the top level of an array insert is 'Unexpected content type'; XmlFragment.toString on a fragment " +
            "holding a non-XML value dereferences null) and Ycs does not.",
            """{"clients":[{"id":1}],"roots":[{"name":"t","kind":"text"}],"steps":[{"op":"delta","c":0,"path":["t"],"delta":[{"delete":0}]}]}""")
        {
            Documented = true,
            Matches = (m, _) => m.Signature.EndsWith(" / yjs threw", StringComparison.Ordinal)
                || m.Differences.Any(d => d.Contains("yjs {\"$err\":\"toJSON\"}", StringComparison.Ordinal)),
        },
    ];

    private static Dictionary<string, object> Step(Dictionary<string, object> script, Mismatch m) =>
        m.Step >= 0 && m.Step < script.List("steps").Count ? (Dictionary<string, object>)script.List("steps")[m.Step] : new Dictionary<string, object>();

    // ".ev[0].es[0].d[1].insert: yjs "a" | ycs "aa"": Ycs' insert ends with Yjs' and is longer.
    private static bool RepeatedInsert(string difference)
    {
        var match = Regex.Match(difference, "\\.d\\[\\d+\\]\\.insert: yjs \"(.*)\" \\| ycs \"(.*)\"$");
        return match.Success && match.Groups[2].Value.Length > match.Groups[1].Value.Length
            && match.Groups[2].Value.EndsWith(match.Groups[1].Value, StringComparison.Ordinal);
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
