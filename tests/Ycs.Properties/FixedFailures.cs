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
        new("gc-cleanup-throws-on-crafted-update",
            "ApplyUpdate rejects, before integrating, an update whose cleanup would garbage-collect a deleted type in which a key " +
            "holds two live items (StructStore.IntegrationPlan.CheckGarbageCollection); it threw InvalidOperationException from " +
            "Item.Gc after integrating.",
            """{"clients":[{"gc":true,"id":1}],"roots":[],"steps":[{"op":"raw","to":0,"u":"AwKsAgAIAQFhA353AXN9AQQBAXQD5LitAvj6//8HAIesAgIBhKwCAwJiYwPwogQAh6wCAAEoAPCiBAABbQF9RygA+Pr//wcAAWwDfUd4eAA=","via":"v1"},{"op":"raw","to":0,"u":"AQUKACgA+Pr//wcAAWsCeH0BxPj6//8HAfj6//8HAQJiYygA8KIEAAFsAnh9R4fwogQAAQoCA/CiBAEDAfj6//8HAQADCgIBAwQC","via":"v1"}]}""")
        {
            Property = "rejects-cleanly",
        },
    ];
}
