namespace Ycs.Properties;

/// <summary>
/// A law of <see cref="Properties"/> that Yjs breaks too, the same way: the property's generator leaves the case
/// out, or the property is stated as Yjs satisfies it (docs/testing.md, "Laws Yjs breaks"). <see cref="Script"/>
/// shows the break as a script of the language; it is recorded with Yjs' observations (fixtures/laws.jsonl) and
/// replayed by <see cref="RecordedTests.Recorded_ObservationsEqualYjs"/>, so that Ycs keeps breaking it exactly
/// as Yjs does. interop/properties/laws.mjs runs the laws that need more than a script against Yjs.
/// </summary>
internal sealed record YjsLaw(string Name, string Property, string Why, string Script);

/// <summary>The laws Yjs 13.6.32 breaks, each with a script that shows it.</summary>
internal static class YjsLaws
{
    public static readonly IReadOnlyList<YjsLaw> All =
    [
        new("reapplied-update-splits-lone-surrogate", "idempotence",
            "Applying an update the document already has splits items at its structs' origins (Yjs' integrateStructs calls " +
            "getMissing, which splits with getItemCleanEnd, before it finds the struct known); a split after a lone " +
            "surrogate replaces it and the next character with U+FFFD. The last step re-applies the client's own insert of " +
            "'a' after 'b\\ud800': 'b\\ud800a' becomes 'b\\ufffd\\ufffd'. Idempotence is generated without lone surrogates " +
            "and characters outside the BMP.",
            """{"clients":[{"gc":false,"id":408356}],"roots":[{"kind":"text","name":"t"}],"steps":[{"at":0,"c":0,"op":"insert","path":["t"],"text":"b\ud800"},{"c":0,"delta":[{"retain":2},{"insert":"a"}],"op":"delta","path":["t"]},{"from":0,"k":1,"op":"deliver","to":0,"via":"v1"}]}"""),
        new("reapplied-update-splits-surrogate-pair", "idempotence",
            "The same split between the halves of a surrogate pair: client 2's embed went between the halves of '\\ud83d\\udc4d' " +
            "(which client 2 replaced locally, without an update); re-applying it on client 3, which has the pair, splits " +
            "there and replaces both halves with U+FFFD (the state's bytes change: 8J+RjQ to 77+977+9).",
            """{"clients":[{"gc":true,"id":982949},{"gc":false,"id":152522},{"gc":false,"id":17},{"gc":false,"id":4}],"roots":[{"kind":"text","name":"t"},{"kind":"map","name":"m"}],"steps":[{"c":2,"key":"c","op":"set","path":["m"],"value":{"$t":"text","s":"👍"}},{"at":0,"c":1,"op":"insert","path":["t"],"text":"a"},{"c":1,"key":"c","op":"set","path":["m"],"value":0},{"from":1,"k":0,"op":"deliver","to":2,"via":"v2to1"},{"at":1,"c":2,"op":"embed","path":["m","c"],"value":{"image":"b","w":0}},{"from":[[3,1],[2,0],[3,0],[1,1]],"op":"merged","to":3,"via":"v1"},{"from":2,"op":"sync","to":0,"via":"v2"},{"from":3,"op":"sync","to":0,"via":"v2"},{"from":0,"op":"sync","to":3,"via":"v2"},{"from":2,"k":1,"op":"deliver","to":3,"via":"v1"}]}"""),
        new("split-surrogate-pair-not-in-the-diff", "diff-update",
            "A local retain that ends between the halves of a surrogate pair splits it, and the document replaces both " +
            "halves with U+FFFD in place, without an update; a peer that received the pair before keeps it, and no diff " +
            "brings the replacement. Client 1 ends with 'abc\\ud834\\udd1e', clients 0 and 2 with 'abc\\ufffd\\ufffd'. " +
            "diff-update is generated without lone surrogates and characters outside the BMP.",
            """{"clients":[{"gc":false,"id":6},{"gc":false,"id":1000004},{"gc":false,"id":1000005}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":0,"delta":[{"insert":"abc"},{"insert":"𝄞"}],"op":"delta","path":["t"]},{"c":0,"delta":[{"retain":4}],"op":"delta","path":["t"]},{"from":0,"k":0,"op":"deliver","to":1,"via":"v1"},{"diff":true,"from":0,"op":"sync","to":1,"via":"v2"},{"from":0,"op":"sync","to":2,"via":"v2"}]}"""),
        new("format-cleanup-depends-on-what-arrived", "diff-update",
            "The formatting cleanup after a remote transaction (see format-cleanup-depends-on-grouping) depends on what the " +
            "transaction brought: client 2, which received client 0's updates one by one and then the diff, deleted the " +
            "'bold' that client 3, which received the whole state, and client 0 keep. diff-update compares the content " +
            "without attributes, and the state vector without the receivers' own clients, when a receiver cleaned up.",
            """{"clients":[{"gc":false,"id":2147483301},{"gc":false,"id":895405},{"gc":false,"id":1000004},{"gc":false,"id":1000005}],"roots":[{"kind":"text","name":"t"},{"kind":"array","name":"a"}],"steps":[{"c":0,"delta":[{"attrs":{"bold":12},"retain":1}],"op":"delta","path":["t"]},{"from":0,"k":0,"op":"deliver","to":1,"via":"v1"},{"at":0,"c":0,"op":"embed","path":["t"],"value":{"image":"a","w":0}},{"at":0,"attrs":{"bold":12},"c":1,"len":3,"op":"format","path":["t"]},{"at":0,"c":0,"op":"ains","path":["a"],"values":[{"$t":"xmle","n":"span"}]},{"from":1,"k":0,"op":"deliver","to":0,"via":"v2to1"},{"at":0,"c":0,"len":2,"op":"delete","path":["t"]},{"from":0,"k":0,"op":"deliver","to":2,"via":"v1"},{"from":0,"k":3,"op":"deliver","to":2,"via":"v1"},{"diff":true,"from":0,"op":"sync","to":2,"via":"v2"},{"from":0,"op":"sync","to":3,"via":"v2"}]}"""),
        new("undo-redo-splits-lone-surrogate", "undo-redo-pair",
            "Undoing the insert of 'a' after a lone surrogate splits the text there, which replaces the surrogate with " +
            "U+FFFD; the redo then restores 'a' as U+FFFD too ('\\n\\ud800a' becomes '\\n\\ufffd\\ufffd'). The undo " +
            "properties are generated without lone surrogates and characters outside the BMP.",
            """{"clients":[{"gc":false,"id":8}],"roots":[{"kind":"xml","name":"x"}],"steps":[{"c":0,"id":"all","op":"um","scope":[["x"]],"timeout":"0"},{"c":0,"op":"push","path":["x"],"values":[{"$t":"xmle","n":"span"},{"$t":"xmlt","s":"\n\ud800"}]},{"at":2,"c":0,"op":"insert","path":["x",1],"text":"a"},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"redo"}]}"""),
        new("format-cleanup-depends-on-grouping", "merge-partition",
            "After a remote transaction a text deletes the formats that are redundant at that moment (Yjs' " +
            "cleanupYTextAfterTransaction, a local transaction of the receiver); which are redundant depends on what " +
            "the transaction brought, so the same updates applied one by one (client 3) and merged into one (client 4) " +
            "end with different attributes on the embed ('bold' only on client 4). merge-partition compares the content " +
            "without attributes when a receiver cleaned up.",
            """{"clients":[{"gc":false,"id":8},{"gc":false,"id":13},{"gc":false,"id":4294966834},{"gc":true,"id":1000006},{"gc":true,"id":1000007}],"roots":[{"kind":"array","name":"a"}],"steps":[{"at":0,"c":0,"op":"ains","path":["a"],"values":[{"$t":"text","s":"a"},""]},{"from":0,"k":0,"op":"deliver","to":2,"via":"v2to1"},{"at":0,"attrs":{"bold":1.5,"size":"a"},"c":2,"len":2,"op":"format","path":["a",0]},{"at":1,"c":2,"len":1,"op":"delete","path":["a",0]},{"from":0,"k":0,"op":"deliver","to":1,"via":"v1"},{"at":0,"c":1,"len":1,"op":"delete","path":["a",0]},{"at":0,"attrs":{"color":"#","size":6},"c":1,"op":"embed","path":["a",0],"value":{"image":"a","w":84}},{"from":0,"k":0,"op":"deliver","to":3,"via":"v1"},{"from":2,"k":0,"op":"deliver","to":3,"via":"v1"},{"from":2,"k":1,"op":"deliver","to":3,"via":"v1"},{"from":1,"k":0,"op":"deliver","to":3,"via":"v1"},{"from":1,"k":1,"op":"deliver","to":3,"via":"v1"},{"diff":false,"from":[[0,0],[2,0],[2,1],[1,0],[1,1]],"op":"merged","to":4,"via":"v1"}]}"""),
        new("observer-order-follows-collected-structs-gc-on", "gc-invisible",
            "Observers of different types are called in the order in which the transaction first changed each type; a " +
            "deleted item collected into a GC struct changes none, so in the last step 'm' comes before 'a' with gc on " +
            "and after it with gc off (the -gc-off entry). gc-invisible compares each observer's calls in order, not the " +
            "order between observers.",
            """{"clients":[{"gc":true,"id":4294967047},{"gc":true,"id":11},{"gc":true,"id":14},{"gc":true,"id":19}],"roots":[{"kind":"array","name":"a"},{"kind":"map","name":"m"}],"steps":[{"at":0,"c":1,"op":"ains","path":["a"],"values":[0,{"$t":"xmlf"},"n"]},{"from":1,"k":0,"op":"deliver","to":0,"via":"v1"},{"at":3,"c":1,"op":"ains","path":["a"],"values":[{"$t":"map"}]},{"c":1,"key":"b","op":"set","path":["a",3],"value":{"$t":"text"}},{"from":1,"k":1,"op":"deliver","to":0,"via":"v1"},{"from":1,"k":2,"op":"deliver","to":0,"via":"v1"},{"from":0,"op":"sync","to":3,"via":"v1"},{"at":0,"c":3,"op":"embed","path":["a",3,"b"],"value":{"image":"a","w":0}},{"at":1,"c":3,"len":3,"op":"delete","path":["a"]},{"c":3,"key":"b","op":"set","path":["m"],"value":{"$t":"map"}},{"from":3,"op":"sync","to":0,"via":"v2"},{"from":0,"op":"sync","to":2,"via":"v2"}]}"""),
        new("observer-order-follows-collected-structs-gc-off", "gc-invisible",
            "The same script with gc off.",
            """{"clients":[{"gc":false,"id":4294967047},{"gc":false,"id":11},{"gc":false,"id":14},{"gc":false,"id":19}],"roots":[{"kind":"array","name":"a"},{"kind":"map","name":"m"}],"steps":[{"at":0,"c":1,"op":"ains","path":["a"],"values":[0,{"$t":"xmlf"},"n"]},{"from":1,"k":0,"op":"deliver","to":0,"via":"v1"},{"at":3,"c":1,"op":"ains","path":["a"],"values":[{"$t":"map"}]},{"c":1,"key":"b","op":"set","path":["a",3],"value":{"$t":"text"}},{"from":1,"k":1,"op":"deliver","to":0,"via":"v1"},{"from":1,"k":2,"op":"deliver","to":0,"via":"v1"},{"from":0,"op":"sync","to":3,"via":"v1"},{"at":0,"c":3,"op":"embed","path":["a",3,"b"],"value":{"image":"a","w":0}},{"at":1,"c":3,"len":3,"op":"delete","path":["a"]},{"c":3,"key":"b","op":"set","path":["m"],"value":{"$t":"map"}},{"from":3,"op":"sync","to":0,"via":"v2"},{"from":0,"op":"sync","to":2,"via":"v2"}]}"""),
        new("gc-struct-waits-for-no-parent-gc-on", "gc-invisible",
            "A set in a map that the same transaction deletes is sent, with gc on, as a GC struct, which waits for nothing: " +
            "client 2, which lacks the map, integrates it and what the transaction did after it (an insert of 'a' into " +
            "an XML text) at once; with gc off (the -gc-off entry) it is an item that waits for its parent, and the 'a' " +
            "waits behind it. gc-invisible compares the steps up to the first at which something waits in either " +
            "variant, and at the end the content of the clients with nothing waiting.",
            """{"clients":[{"gc":true,"id":7},{"gc":true,"id":9},{"gc":true,"id":266171}],"roots":[{"kind":"array","name":"a"},{"kind":"xml","name":"x"}],"steps":[{"at":0,"c":0,"op":"ains","path":["a"],"values":[{"$t":"map"}]},{"c":1,"op":"xafter","path":["x"],"ref":null,"values":[{"$t":"xmlt"}]},{"from":[[1,0],[1,2],[0,0]],"op":"merged","to":1,"via":"v1"},{"c":1,"op":"transact","ops":[{"key":"b","op":"set","path":["a",0],"value":"s"},{"at":0,"op":"insert","path":["x",0],"text":"a"},{"at":0,"len":1,"op":"delete","path":["a"]}]},{"from":1,"k":0,"op":"deliver","to":2,"via":"v1"},{"from":1,"k":1,"op":"deliver","to":2,"via":"v1"}]}"""),
        new("gc-struct-waits-for-no-parent-gc-off", "gc-invisible",
            "The same script with gc off.",
            """{"clients":[{"gc":false,"id":7},{"gc":false,"id":9},{"gc":false,"id":266171}],"roots":[{"kind":"array","name":"a"},{"kind":"xml","name":"x"}],"steps":[{"at":0,"c":0,"op":"ains","path":["a"],"values":[{"$t":"map"}]},{"c":1,"op":"xafter","path":["x"],"ref":null,"values":[{"$t":"xmlt"}]},{"from":[[1,0],[1,2],[0,0]],"op":"merged","to":1,"via":"v1"},{"c":1,"op":"transact","ops":[{"key":"b","op":"set","path":["a",0],"value":"s"},{"at":0,"op":"insert","path":["x",0],"text":"a"},{"at":0,"len":1,"op":"delete","path":["a"]}]},{"from":1,"k":0,"op":"deliver","to":2,"via":"v1"},{"from":1,"k":1,"op":"deliver","to":2,"via":"v1"}]}"""),
        new("relative-position-in-collected-type-gc-on", "gc-invisible",
            "A relative position in a type that is deleted resolves, with gc off (the -gc-off entry), to the deleted type " +
            "(at no visible path); with gc on the type is collected and the position does not resolve. gc-invisible " +
            "leaves out such resolutions.",
            """{"clients":[{"gc":true,"id":2147483637}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"key":"b","op":"set","path":["m"],"value":{"$t":"xmle","n":"span"}},{"at":0,"c":0,"id":"r1","op":"rpos","path":["m","b"]},{"c":0,"op":"clear","path":["m"]},{"c":0,"id":"r1","op":"rres"}]}"""),
        new("relative-position-in-collected-type-gc-off", "gc-invisible",
            "The same script with gc off.",
            """{"clients":[{"gc":false,"id":2147483637}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"key":"b","op":"set","path":["m"],"value":{"$t":"xmle","n":"span"}},{"at":0,"c":0,"id":"r1","op":"rpos","path":["m","b"]},{"c":0,"op":"clear","path":["m"]},{"c":0,"id":"r1","op":"rres"}]}"""),
        new("empty-cleanup-transactions-depend-on-gc-on", "gc-invisible",
            "How many empty transactions a remote update's cleanup opens depends on gc: in the last step one with gc on, " +
            "two with gc off (the -gc-off entry). An UndoManager whose scope is the document captures every transaction, " +
            "an empty one too (Yjs' afterTransactionHandler: 'type === this.doc'), so its stack differs (Yjs 13.6.32 " +
            "throws for such a scope, see KnownFailures.yjs-throws; this script scopes the text). gc-invisible leaves out " +
            "the stacks of such an UndoManager, as it leaves out empty transactions.",
            """{"clients":[{"gc":true,"id":412435},{"gc":true,"id":170012}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":1,"id":"u0","op":"um","scope":[["t"]],"timeout":"0"},{"at":0,"attrs":{"bold":0,"color":0},"c":0,"op":"embed","path":["t"],"value":{"image":"b","w":0}},{"at":0,"c":0,"len":1,"op":"delete","path":["t"]},{"from":0,"op":"sync","to":1,"via":"v1"}]}"""),
        new("empty-cleanup-transactions-depend-on-gc-off", "gc-invisible",
            "The same script with gc off.",
            """{"clients":[{"gc":false,"id":412435},{"gc":false,"id":170012}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":1,"id":"u0","op":"um","scope":[["t"]],"timeout":"0"},{"at":0,"attrs":{"bold":0,"color":0},"c":0,"op":"embed","path":["t"],"value":{"image":"b","w":0}},{"at":0,"c":0,"len":1,"op":"delete","path":["t"]},{"from":0,"op":"sync","to":1,"via":"v1"}]}"""),
        new("relative-position-follows-redo-locally", "relpos-stable",
            "A relative position on an item that an undo deletes and a redo re-creates resolves to the re-created item " +
            "only on the replica that redid it (Item.redone is local): client 0 resolves to 0, client 1 to 1 (after the " +
            "deleted item). relpos-stable leaves out scripts with an undo or a redo after the position was taken.",
            """{"clients":[{"gc":false,"id":17},{"gc":false,"id":4294966637}],"roots":[{"kind":"text","name":"t"}],"steps":[{"c":0,"id":"u1","op":"um","scope":[["t"]],"timeout":"0"},{"at":0,"c":0,"op":"insert","path":["t"],"text":"a"},{"at":0,"c":0,"id":"m","op":"rpos","path":["t"]},{"c":0,"id":"u1","op":"undo"},{"c":0,"id":"u1","op":"redo"},{"from":0,"op":"sync","to":1,"via":"v1"},{"c":0,"id":"m","op":"rres"},{"c":1,"id":"m","op":"rres"}]}"""),
        new("redo-recreated-type", "undo-all-restores",
            "Undoing every change and redoing every change does not restore a nested type that was created and then edited: " +
            "the redo re-creates the type, and not every edit made inside it is redone (here the XML text ends with " +
            "'a\u4e2d\u6587 ' after the redos, 'a\u4e2d\u6587 \n' before the undos). undo-all-restores checks the redos " +
            "only in scripts that edit no nested type.",
            """{"clients":[{"gc":false,"id":4294966611}],"roots":[{"kind":"map","name":"m"}],"steps":[{"c":0,"id":"all","op":"um","scope":[["m"]],"timeout":"0"},{"c":0,"key":"a","op":"set","path":["m"],"value":{"$t":"xmle","n":"div","v":[{"$t":"xmlt","s":"\u4e2d\u6587 b"}]}},{"at":0,"attrs":{"italic":null},"c":0,"len":5,"op":"format","path":["m","a",0]},{"at":0,"c":0,"op":"insert","path":["m","a",0],"text":"a"},{"at":4,"c":0,"len":1,"op":"delete","path":["m","a",0]},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"undo"},{"c":0,"id":"all","op":"redo"},{"c":0,"id":"all","op":"redo"},{"c":0,"id":"all","op":"redo"},{"c":0,"id":"all","op":"redo"}]}"""),
    ];
}
