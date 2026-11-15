namespace Ycs.Properties;

/// <summary>
/// A known failure that is fixed: its minimal script stays as a regression case. A divergence's script
/// is recorded with Yjs' observations (fixtures/fixed.jsonl) and replayed by
/// <see cref="RecordedTests.Recorded_ObservationsEqualYjs"/>; a property's (<see cref="Property"/>, a
/// property name) is checked by <see cref="RecordedTests.FixedProperties_Hold"/>.
/// </summary>
internal sealed record FixedFailure(string Name, string Fix, string Script)
{
    public string Property { get; init; }
}

/// <summary>The entries of <see cref="KnownFailures"/> that were fixed, with what the fix was.</summary>
internal static class FixedFailures
{
    public static readonly IReadOnlyList<FixedFailure> All =
    [
        new("snapshot-read-corrupts-text",
            "ToArraySnapshot, GetSnapshot and GetAllSnapshot read without a transaction or a split, as Yjs' typeListToArraySnapshot, " +
            "typeMapGetSnapshot and typeMapGetAllSnapshot; a split after a lone high surrogate replaced it and the next character with U+FFFD.",
            """{"clients":[{"gc":false,"id":4294967149},{"gc":false,"id":452169}],"roots":[{"kind":"xml","name":"x"}],"steps":[{"c":0,"op":"xafter","path":["x"],"ref":null,"values":[{"$t":"xmle","n":"div"}]},{"from":0,"k":0,"op":"deliver","to":1,"via":"v2"},{"c":1,"op":"xafter","path":["x",0],"ref":null,"values":[{"$t":"xmle","n":"span","v":[{"$t":"xmle","n":"div"}]}]},{"from":1,"k":0,"op":"deliver","to":0,"via":"v1"},{"at":0,"c":1,"op":"ains","path":["x",0,0],"values":[{"$t":"xmle","n":"span","v":[{"$t":"xmle","n":"span"}]}]},{"from":[[1,1],[0,0]],"op":"merged","to":0,"via":"v2"},{"c":0,"op":"xafter","path":["x",0,0,0,0],"ref":null,"values":[{"$t":"xmlt","s":"c\ud800"}]},{"c":0,"id":"s0","op":"snap"},{"at":2,"c":0,"op":"insert","path":["x",0,0,0,0,0],"text":"a"},{"c":0,"id":"s0","op":"readSnap","path":["x",0,0,1]}]}"""),
        new("snapshot-array-read-merged-items",
            "The same change: an item merged across the snapshot's boundary is read whole, as Yjs reads it (it was documented as a difference).",
            """{"clients":[{"gc":false,"id":3}],"roots":[{"kind":"array","name":"a"}],"steps":[{"at":0,"c":0,"op":"ains","path":["a"],"values":[1]},{"c":0,"id":"s3","op":"snap"},{"c":0,"op":"push","path":["a"],"values":[0]},{"c":0,"id":"s3","op":"readSnap","path":["a"]}]}"""),
        new("snapshot-array-read-opens-a-transaction",
            "The same change: ToArraySnapshot opens no transaction, so a read raises no AfterTransaction.",
            """{"clients":[{"gc":false,"id":2147483431}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":0,"id":"s0","op":"snap"},{"c":0,"id":"s0","op":"readSnap","path":["a"]}]}"""),
        new("subdocs-event-removed-instead-of-loaded",
            "Not a bug of Ycs: the adapter read YDoc.SubdocsChanged's (Loaded, Added, Removed) by position as Yjs' " +
            "{ added, removed, loaded }; it reads the named elements now.",
            """{"clients":[{"gc":false,"id":2147483114}],"roots":[{"kind":"array","name":"a"}],"steps":[{"c":0,"op":"push","path":["a"],"values":[{"$doc":{"autoLoad":false,"g":"doc-1"}}]}]}"""),
        new("pending-deletes-split-surrogate-pair",
            "When an update leaves deletions waiting and some that waited before stay waiting, every client's waiting ranges are " +
            "sorted and merged, as Yjs' mergeUpdatesV2 of its pendingDs; adjacent ranges deleting the halves of a surrogate pair " +
            "are then applied as one, without the split that replaced the pair with U+FFFD U+FFFD.",
            """{"clients":[{"gc":false,"id":818522},{"gc":false,"id":10}],"roots":[{"kind":"text","name":"t"}],"steps":[{"at":0,"c":0,"op":"insert","path":["t"],"text":"\ud83d\udc4d"},{"c":0,"delta":[{"delete":1}],"op":"delta","path":["t"]},{"c":0,"delta":[{"delete":1}],"op":"delta","path":["t"]},{"from":0,"k":2,"op":"deliver","to":1,"via":"v1"},{"from":0,"k":1,"op":"deliver","to":1,"via":"v1"},{"from":0,"k":0,"op":"deliver","to":1,"via":"v2"}]}"""),
        new("huge-clock-gc-encoding",
            "The same change as pending-deletes-split-surrogate-pair: the waiting ranges are merged when Yjs merges them, so they " +
            "split the items where Yjs' do (Yjs wrote 6 structs, Ycs 5). Nothing about the clocks near 2^31.",
            """{"clients":[{"gc":false,"id":2}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AQL4+v//BwAAv////wfHrAIFrAIFAgILAQMDrAIBAwI=","via":"v1"},{"op":"raw","to":0,"u":"AQMLAIWsAgYReyJpbWFnZSI6ImkucG5nIn1ErAIGAmJjAQEBYQECrAIBAggLAQAC","via":"v1"},{"op":"raw","to":0,"u":"AQSsAgAIAQFhA30BdwFzfoisAgEDdQJ9AXcBeH0BdQJ9AXcBeAQBAXQD5LitBwEBeAYA","via":"v1"}]}"""),
        new("pending-deletes-client-order",
            "Deletions that waited are applied client by client from the highest client id down, as Yjs reads its pendingDs back " +
            "(writeDeleteSet writes the clients in that order); Ycs took them in the order the clients first waited. The order " +
            "decides whether a waiting range splits a key's value of several items (only a fuzzed update holds one) before or after " +
            "the waiting deletion of its type deletes the item the key holds, and so which of its items stay. Also the order of the " +
            "transaction's delete set and of the garbage collection filter's calls. Nothing about the clocks near 2^31.",
            """{"clients":[{"gc":false,"id":2}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AQMLAQgBAWEDeHcBc30BRwsCAQoCAQsBBAE=","via":"v1to2"},{"op":"raw","to":0,"u":"AwMKAAAEKAALBAFrAXiG+Pr//wcCBml0YWxpYwExBKwCAAC/////BycACwABbAaH+Pr//wcDBigACwQBbAN3AXN9R34C+Pr//wcGAQEBdAGD+Pr//wcAAwECAwOsAgIDA8L///8HAvj6//8HAQIDCwEEAQ==","via":"v1"},{"op":"raw","to":0,"u":"AQX4+v//BwAoAQFtAW0BeAQBAXQD5LitxPj6//8HAfj6//8HAQFhBwDwogQABsILAwsEAgExAyJqIgELAQIC","via":"v1to2"},{"op":"raw","to":0,"u":"AgELAAcBAXgDA2RpdgHwogQAxwsACwADA2RpdgLwogQBAAMLAQAD","via":"v1"}]}"""),
        new("debug-assert-clock-over-2^30",
            "IntDiffOptRleEncoder asserted values of at most 2^30 (Debug builds), a leftover of lib0's 31-bit shift; it writes " +
            "diff * 2 + flag as lib0 does, so clocks up to 2^31 - 1 (docs/api.md's limit) are fine and the assertion is gone.",
            """{"clients":[{"gc":true,"id":1},{"gc":false,"id":2}],"roots":[{"kind":"text","name":"t"},{"kind":"array","name":"a"},{"kind":"map","name":"m"},{"kind":"xml","name":"x"}],"steps":[{"op":"raw","to":0,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":0,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":0,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":0,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":1,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":1,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"}]}"""),
        new("debug-assert-clock-over-2^30-property",
            "The same assertion, seen by the rejects-cleanly property as a DebugAssertException.",
            """{"clients":[{"gc":true,"id":1},{"gc":false,"id":2}],"roots":[{"kind":"text","name":"t"},{"kind":"array","name":"a"},{"kind":"map","name":"m"},{"kind":"xml","name":"x"}],"steps":[{"op":"raw","to":0,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":0,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":0,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":0,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AgOsAgACAAy/////BwIBMQMiaiIKAUOsAgADAQIDAwwAAL////8HBwEBYQAKAwA=","via":"v1"},{"op":"raw","to":1,"u":"AQWsAgQIAAzE////BwF3AXMFAQF0EXsiaW1hZ2UiOiJpLnBuZyJ9CAAMxP///wcDfQF3AXN4ggy/////BwIBMQMiaiIEAAzF////BwJiYwA=","via":"v1"},{"op":"raw","to":1,"u":"AQULAAQADMX///8HA2RlZigBAW0BbAJ1An0BdwF4fUcEAAzD////BwN4w6lHrAIIACgBAW0BbQF1An0BdwF4AQsBBQQ=","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"},{"op":"raw","to":1,"u":"AQMMw////wcHAQF4BocMv////wcARwzD////BwYBDAMFAQcBw////wcC","via":"v1"}]}""")
        {
            Property = "rejects-cleanly",
        },
        new("gc-cleanup-throws-on-crafted-update",
            "ApplyUpdate rejects, before integrating, an update whose cleanup would garbage-collect a deleted type in which a key " +
            "holds two live items (StructStore.IntegrationPlan.CheckGarbageCollection); it threw InvalidOperationException from " +
            "Item.Gc after integrating.",
            """{"clients":[{"gc":true,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AwKsAgAIAQFhA353AXN9AQQBAXQD5LitAvj6//8HAIesAgIBhKwCAwJiYwPwogQAh6wCAAEoAPCiBAABbQF9RygA+Pr//wcAAWwDfUd4eAA=","via":"v1"},{"op":"raw","to":0,"u":"AQUKACgA+Pr//wcAAWsCeH0BxPj6//8HAfj6//8HAQJiYygA8KIEAAFsAnh9R4fwogQAAQoCA/CiBAEDAfj6//8HAQADCgIBAwQC","via":"v1"}]}""")
        {
            Property = "rejects-cleanly",
        },
        new("pending-apply-delta-keeps-sanitize",
            "A text's ApplyDelta before it is in a document is applied with the default sanitize once it is, as Yjs' pending " +
            "applyDelta ('this._pending.push(() => this.applyDelta(delta))' drops the option); with sanitize: false Ycs " +
            "dropped the trailing newline that Yjs keeps.",
            """{"clients":[{"gc":false,"id":16}],"roots":[{"kind":"text","name":"t2"}],"steps":[{"c":0,"delta":[{"insert":{"$t":"text","dl":[{"insert":"x\u000a"}],"dls":false}}],"op":"delta","path":["t2"]}]}"""),
        new("stale-marker-get-beyond-index",
            "YArray.Get returns nothing for a negative remainder, as Yjs' typeListGet reads the content at a negative offset " +
            "(undefined): an observer of another type read the list during a remote transaction's cleanup, before the list's own " +
            "observer call emptied its markers, and the marker the lookup found lay beyond the index. Ycs threw ArgumentOutOfRangeException.",
            """{"clients":[{"gc":false,"id":3,"quiet":true},{"gc":false,"id":10,"quiet":true}],"roots":[{"kind":"array","name":"a"},{"kind":"xml","name":"x"}],"steps":[{"c":0,"op":"transact","ops":[{"op":"push","path":["a"],"values":[0]},{"op":"push","path":["a"],"values":[0]},{"op":"push","path":["a"],"values":[{"$t":"map"}]},{"at":4,"op":"ains","path":["a"],"values":[true]},{"op":"push","path":["a"],"values":[{"$t":"array"}]},{"at":3,"op":"ains","path":["a"],"values":[0]}]},{"from":0,"k":0,"op":"deliver","to":1,"via":"v2"},{"c":1,"key":"b","op":"set","path":["a",2],"value":0},{"c":0,"op":"transact","ops":[{"from":1,"k":0,"op":"deliver","to":0,"via":"v1"},{"at":0,"op":"ains","path":["a",5],"values":[]}]},{"c":1,"op":"transact","ops":[{"op":"unshift","path":["x"],"values":[{"$t":"xmle","n":"div"}]},{"at":0,"len":3,"op":"delete","path":["a"]}]},{"c":0,"id":"x1","op":"react","ops":[{"key":"k2","op":"set","path":["a",2],"value":{"$t":"map"}}],"path":["x"],"txn":true},{"from":1,"k":1,"op":"deliver","to":0,"via":"v2"}]}"""),
    ];
}
