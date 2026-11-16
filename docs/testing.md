# Testing

What each project guards, and how to run, soak and update them. CI
(`.github/workflows/ci.yml`) runs all of it on every push.

| Project | Guards | Run |
| --- | --- | --- |
| `tests/Ycs.Tests` | Behaviour: unit tests, updates written by real Yjs (byte-identical re-encoding), merges and pending structs of updates that hold a GC struct and an item for one clock (`interop/js/merge-conflicts.mjs`), pending structs of updates that overlap by clock in every order of 3 to 5 (`interop/js/pending-orders.mjs`), the sort of `MergeUpdates` against V8's (`interop/js/v8-sort.mjs`), 40 randomized concurrent sessions replayed from Yjs, 1,425 differential traces of the shared types and their events (see below), the windows of the run extension and the search markers against Yjs and against a reference (see [below](#runs-extended-in-place-and-search-markers)), and the public API baseline (`PublicApi.txt`). | `dotnet test tests/Ycs.Tests` |
| `tests/Ycs.Corpus` | Convergence with Yjs on a recorded corpus of Yjs 13.6.32 sessions (`interop/corpus/`): the observer's state byte for byte, and for the scripted sessions (concurrent formatting) every client's, replayed with a Ycs document per client (`ClientReplayTests`); and the UndoManager, step by step, on random sessions run in Yjs (`UndoDifferentialTests`, `interop/corpus/undo-gen.mjs`). | `dotnet test tests/Ycs.Corpus` |
| `tests/Ycs.Fuzz` | Robustness of `ApplyUpdate`/`ApplyUpdateV2` against damaged input. | `dotnet test tests/Ycs.Fuzz -c Release` |
| `tests/Ycs.Properties` | Differential testing against real Yjs over the whole API (texts, arrays, maps, XML, nested types, subdocuments, transactions, undo, snapshots, relative positions, events, every network path and encoding), on recorded scripts (no Node) and in a live soak; properties that need no oracle; a structure-aware update fuzzer. See [below](#model-based-differential-testing-testsycsproperties). | `dotnet test tests/Ycs.Properties -c Release` |
| `tests/Ycs.Budgets` | Allocated bytes and time per operation for a fixed set of scenarios. | `dotnet test tests/Ycs.Budgets -c Release --logger "console;verbosity=detailed"` |
| `samples/Ycs.AotSample`, `samples/Ycs.WasmSample` | Native AOT and browser-wasm: publish without trim/AOT warnings and run. | see the README |
| `interop/run.sh`, `interop/corpus/roundtrip.sh`, `interop/v14/roundtrip.sh` | Live round-trips against real Yjs (Node); `run.sh` runs the v14 one too. | see the README |
| `tests/Ycs.Server.Tests` | The sync server (`samples/Ycs.Server`, [plan-server.md](plan-server.md)): the awareness port against vectors written by y-protocols 1.0.7, rooms and sessions, the WebSocket endpoint and the SignalR hub in-process, and a seeded fuzz of hostile sessions against honest clients (`HostileFuzzTests`; `YCS_SERVER_FUZZ=first:count` for more seeds). Ycs bugs it found are ignored tests listed in [findings-server.md](findings-server.md). | `dotnet test tests/Ycs.Server.Tests -c Release` |
| `interop/server/smoke.sh`, `interop/signalr/smoke.sh`, `interop/server/converge.sh` | The sync server against real clients: two `y-websocket` (or SignalR) providers sync text and awareness; random clients that edit, drop, edit offline and rejoin converge with each other, with a fresh client and with plain Yjs given every update, on the Ycs server (WebSocket, SignalR, mixed) and on the reference server `@y/websocket-server` with the same seeds. See [interop/server/README.md](../interop/server/README.md). | `bash interop/server/converge.sh [seeds] [first]` |
| `interop/demo/check.sh` | The demo page in Chromium (Playwright): two pages per transport and one mixed pair converge, show each other's cursor and merge offline edits. Not in CI (it needs a browser). | `bash interop/demo/check.sh` |
| `interop/yjs-pending/` | Not a test: the analysis of a Yjs 13 pending-retry bug (yjs/yjs#824); Ycs completes where Yjs sticks (`PendingRetryTests` in `tests/Ycs.Tests`). | see its README |
| `bench/server` | Not a test: the sync server against the reference server (fan-out, joins, memory per room). Results in its [README](../bench/server/README.md). | `bench/server/run.sh [fanout\|join\|memory\|all]` |
| `bench/Ycs.Benchmarks` | Not a test: the editing-trace benchmark behind the README's numbers; `limits`, what updates at the default `UpdateLimits` cost (the table in [api.md](api.md#untrusted-input)); `sizes`, what one item holds and costs by kind; `loop`, one operation repeated on the trace's document (time and bytes per operation, what to attach a profiler to). | `bench/Ycs.Benchmarks/get-trace.sh`, then `dotnet run -c Release --project bench/Ycs.Benchmarks -- <trace.json.gz>`; `... -- limits`; `... -- sizes`; `... -- loop <trace.json.gz> encode-v1 2000` |
| `bench/Ycs.Compare` | Not a test: CI builds it with the solution and does not run it (it depends on YDotNet and its native library): ycs against YDotNet (yrs) on the same workloads, results checked equal and cross-applied; Native AOT, wasm and hostile-input probes. Results in its [README](../bench/Ycs.Compare/README.md). | `bench/Ycs.Compare/run.sh` |

## Runs extended in place and search markers

An insert that continues its client's run extends that item in place (`Item.ExtendRun`) where nobody
observes the type, and the transaction splits the run back into Yjs' items before anything could tell
the two apart (docs/walkthrough-core.md, "Runs extended in place"). Search markers are where Yjs has
them after every operation: where Yjs leaves them stale, the item a marker sits on decides where an
edit lands. What proves both:

| Test | Compared with | What |
| --- | --- | --- |
| `RunExtensionYjsTests` | Yjs 13.6.32 (values recorded by `interop/markers/blockers.mjs`) | the four windows a review found: an update applied between index edits in one transaction, an observer editing during a remote transaction's cleanup, a marker an insert moves off a deleted item, a stale marker beyond the index an observer reads, inserts or deletes at (`Get` returns nothing, an insert throws, a delete starts at the front, as in Yjs); a garbage collection filter (its calls, the updates and states); snapshot reads within a transaction; a transaction started from an observer (the inner and the outer events) |
| `StaleMarkerScriptTests` | Yjs (`interop/markers/gen.mjs`: modes `apply`, `small`, `observer`) | the content after every step and the final state of scripts in which Yjs' markers are stale when an edit looks them up, each run three times: the marker walks as by default, through the run-head index from the first step (`eager`), and item by item (`plain`). `Fixtures/stale-markers.jsonl.gz`: seeds 1 to 300 of each mode, and every script a refused version failed; `interop/markers/soak.sh <scripts per mode> <first seed> [modes]` generates and replays more |
| `RunHeadIndexTests` | the walks item by item (`YDoc.RunHeadIndexDisabledForTests`) and Yjs | the design review's script (a stale marker that Yjs' walk right puts on the item after a chain, "cdeqpD" in Yjs 13.6.32), text and array; random edits of a long list split into one client's chain, with the other client's items inside it, deleted members and both stale windows: content, markers and where every lookup ends, eager, default and plain |
| `RunExtensionScenarioTests` | the reference (`ReferenceRun`: `YDoc.RunExtensionsDisabledForTests`, `StructStore.PairwiseMergeForTests`) | 36 hand-written scenarios (observers and handlers attached within a transaction, throwing bodies and handlers, updates applied and encodings made within a transaction, undo, surrogates, snapshots, relative positions, prelim types, a parent deleted while typing, filters, transactions started from observers): their logs (events with the items they added and deleted, updates, states, every struct with its marker flag) |
| `RunExtensionFuzzTests` | the reference | random scenarios mixing all of that with peer syncs, a map observer that edits the lists during remote cleanups and an update handler that types; the search markers of both lists after every step. 300 seeds; `YCS_EXTENSION_FUZZ=first:count` for more |
| `run-extension`, `merge-order` (`tests/Ycs.Properties`) | the reference | see [reference comparisons](#reference-comparisons) |
| the live soak, `fixtures/windows.jsonl` (`tests/Ycs.Properties`) | Yjs | scripts with quiet clients, filters, typing runs with an update applied or a snapshot read in the middle, reacting observers (`GenOptions.Windows`); the soak reports how many steps and scripts extended a run |

"Observed" (`YType.IsObserved`): a handler on the type, a deep handler on it or an ancestor, or
an `AfterTransaction`, `AfterTransactionCleanup` or `AfterAllTransactions` handler on the document; with
a `BeforeObserverCalls` handler nothing extends either. Update handlers do not count. An observed type,
and every type of a document with an UndoManager (an `AfterTransaction` handler), makes Yjs' items.

## Differential traces (`TraceReplayTests`)

`interop/traces/gen.mjs` runs three Yjs 13.6.32 clients that edit concurrently and records every
step of the observed one (a local transaction, a remote update, a snapshot, the first access of a
root) with what Yjs shows afterwards, in `tests/Ycs.Tests/Fixtures/trace-<set>.json.gz`.
`TraceReplayTests` replays each trace on one Ycs document and compares after every step: content,
`ToDelta` (with snapshots and `computeYChange` in `snapshots`), lengths, the state bytes (by a hash),
and every observer call in order with what it read (`Delta`, `Changes`, `Keys`, `Path`). The sets:
`text` (nobody observes), `text-observed` (an observer reads the delta, the changes and the keys),
`text-markers` (long plain text), `text-placeholder` (a root text that got formats before it was
asked for, so its search markers stay on), `list-map` and `list-map-push` (nested maps and arrays,
an observer on every type, a deep one on every third), `xml`, `snapshots`.

Every trace has to pass. To regenerate (after a change of the generator or of the tested Yjs
version):

```bash
cd interop/traces && npm ci && node gen.mjs
```

## Yjs v14 (`V14InteropTests`, `interop/v14`)

Yjs v14 is published as `@y/y` (pinned to 14.0.0-rc.30, with lib0 1.0.0-rc.37, in `interop/v14/package.json`
and its own lock; ESM only, Node >= 22). It writes the same v1 and v2 updates as v13 but has one type,
`Y.Node`, and draws 53-bit client ids. Ycs does not port v14: the parity target stays Yjs 13.6.32, and the
v14 interop checks that Ycs reads what v14 writes as Yjs 13.6.32 reads it, and that v14 takes Ycs' answers.

`interop/v14/gen.mjs` (deterministic; every client id set) writes `tests/Ycs.Tests/Fixtures/v14-interop.json`,
four documents of two v14 clients that edited concurrently: `nodes` (root nodes used as text, map, array and
XML; nested named and unnamed nodes, attributes of every value kind, a node as an attribute value, formatted
text, an object and a node inserted into text, list content, deletions of characters, list items, attributes
and children) and three on client ids above 2^32 and at 2^53 - 1 (items, origins, deletions and map values of
such clients; 2^53 - 1 also as a value). For each it records v14's updates (v1, v2, one per transaction),
state vector, snapshots (v1, v2), relative positions in every sequence (start, inside, end, both
associations) with what v14 resolves them to, a diff by state vector, a merge, how Yjs 13.6.32 reads the
document (`interop/v14/lib.mjs`: a root as the type the reader asks for, a nested named node as a
`YXmlElement`, an unnamed one as a `YXmlFragment`) and whether Yjs 13 re-encodes it byte for byte, and a change
a third v14 client made concurrently. `V14InteropTests` (no Node) checks: Ycs reads v1 and v2 as Yjs 13 does
(content through the types they arrive as, and lengths); re-encodes both byte for byte; writes v14's state
vector bytes with the same clients and clocks; v14's snapshots, Ycs' own snapshot of the document, every
relative position (decoded and re-encoded, and Ycs' own from the same index) and the diff and merge bytes
equal v14's; and a Ycs peer with a 53-bit client id (2^53 - 4) and one just above 2^32 edits every root
through the type it arrived as (text with formatting, map, array, an XML element with an XML text, a nested
element) and applies v14's concurrent change, keeping its own client id. `interop/v14/roundtrip.sh` exports
those peers' answers (`V14InteropTests.Export_WhenRequested_WritesYcsAnswers`, with `YCS_V14_EXPORT`) and
`roundtrip.mjs` applies them in v14: Ycs' diff for v14's state vector (v1, v2), its whole state (v1, v2) and
its edits one by one; v14 must reach Ycs' content and state vector with nothing pending, encode the converged
state byte for byte as Ycs does, and equal Ycs' snapshot. Regenerate the fixture after a change of
`gen.mjs` or of the pinned version: `cd interop/v14 && npm ci && node gen.mjs > ../../tests/Ycs.Tests/Fixtures/v14-interop.json`.

Findings (14.0.0-rc.30): no divergence of Ycs, and no v14 defect on the wire. One v13/v14 difference that
consumers see, pinned by the fixture: v14 writes an object inserted into a text node as `ContentAny` (a
list element), where v13's `insertEmbed` writes `ContentEmbed`; Yjs 13's `toDelta` leaves `ContentAny`
out but counts it in `length`, and Ycs does the same (`ToDelta`, `Length`), so a
v13 or Ycs binding of such a text is off by one per such object. A node inserted into text arrives as an
embedded `YXmlElement`, as a v13 embedded type.

## Fuzzing (`tests/Ycs.Fuzz`)

The seeds are every update of the Yjs corpus (applied on top of the updates before it), updates
generated in C# (`Seeds.cs`), in v1 and v2, and valid hostile updates (`Hostile.cs`: run-length bombs
of GC structs, deleted items, texts and subdocuments beyond and at the limits, and a quadratic
conflict; `HostileUpdates_ApplyOrFailCleanlyWithinTheCeilings` replays them unmutated). `Mutator.cs`
damages them: bit flips, truncation, inserted, deleted, duplicated, swapped and overwritten chunks,
var-uints replaced by huge values, random bytes. Half of the inputs go to an observed document: a
document listener reads every lazily computed member of every event and the texts' deltas, and an
UndoManager captures the change. The documents' limits are `Harness.Limits` (32,768 structs per
update, 2^20 conflict steps), so that no valid input can exceed the ceilings below. For each input
(`Harness.cs`):

* it finishes within 500 ms (`Slow`), and within 2 s (it runs on a worker thread; a hung one is abandoned);
* it allocates at most 64 MiB (for an input over 4 KiB, 16 KiB per input byte). The test process runs with a
  512 MiB GC heap limit (`runtimeconfig.template.json`), so a decoder that trusts a huge length
  fails at once with `OutOfMemoryException`, and the throw site names the allocation;
* it throws nothing but `YUpdateException` (`Harness.AllowedExceptionTypes`);
* when it throws, the document's text, state vector and v1 encoding are unchanged (when the conflict
  budget ran out, `PartlyApplied`, the document must still be readable);
* afterwards, applied, rejected or applied in part, the document is complete (`Consistency.cs`): its
  v1 and v2 state applies to a fresh document, which has the same state vector when nothing is
  pending, and every item list of both, of the roots and of every type nested in them, keeps the
  representation invariants of `ItemListInvariants` (each item linked back to the one before it, a
  positive length, no id in two items). Not its order invariants, nor that every item's parent is
  the list: they hold for what Yjs writes, and crafted updates Yjs applies (a right origin left of the
  item, a slice of a struct linked next to its origin in another parent) break them in Yjs too.

A violation is identified by its signature: the verdict, the exception type and the innermost Ycs
method it was thrown from (tiered compilation is off in this project, so inlining, and with it
the throw site, does not depend on how warm the code is).

By default the run is deterministic: 20 000 inputs from seed 1, a few seconds. For a soak:

```bash
YCS_FUZZ_ITERATIONS=1000000 YCS_FUZZ_SEED=random dotnet test tests/Ycs.Fuzz -c Release \
  --filter Name=MutatedUpdates_FailCleanly --logger "console;verbosity=detailed"
```

The output lists the verdicts and exception types seen and the seed used, so any run can be
repeated. A violation with a signature that is not in `KnownFailures.cs` fails the run and is
printed minimized, as the fields of a `PinnedCase`, ready to paste.

`KnownFailures.cs` holds the violations found and not fixed, each with its minimal input (empty
today). The fuzz run tolerates their signatures; `PinnedCases_StillFailAsPinned` replays them and is
inconclusive while one still fails, and fails once it passes: move the entry to `FixedFailures.cs`
with the fix, where `FixedCase_FailsCleanly` keeps it as a regression test.

## Public API (`tests/Ycs.Tests/PublicApi.txt`)

`PublicApi_MatchesBaseline` renders every public type of `Ycs.dll` and its public and protected
members, one line each, and compares the text with `PublicApi.txt`. When an API change is
intended, accept it and commit the file with the change:

```bash
YCS_ACCEPT_API=1 dotnet test tests/Ycs.Tests --filter PublicApi_MatchesBaseline
git diff tests/Ycs.Tests/PublicApi.txt
```

## Budgets (`tests/Ycs.Budgets`)

`Scenarios.cs` defines the scenarios (most on a 100 000-character text made of many items);
`Budgets.cs` is the one file with the numbers. Each scenario is warmed up and then averaged over
100–500 operations; the allocated bytes are counted on the test thread. A bytes budget is today's
measurement plus about 15 %; the time ceilings are about 10x today's and only catch an algorithmic
regression. The budgets are for `-c Release` (Debug is inconclusive); tiered compilation is off in
the project so that no tier-up lands in a measurement.

* A regression fails the test with the table.
* An improvement passes and the table says `improved: lower the budget to N`: put N into
  `Budgets.cs` in the same change, so the gain is kept.
* A new scenario fails with `no budget; suggest N` until its budget is added.

Three scenarios are a whole transaction each (a fresh document, two warm-ups, three measured): 100,000 pushes,
100,000 keystrokes, and single deletes at random places of one run, which went quadratic before. A test of its own
pushes a million values in one transaction and requires at most 512 MiB allocated and 64 MiB held by the document
(about a second).

## Model-based differential testing (`tests/Ycs.Properties`)

One operation language, two interpreters: `interop/properties/oracle.mjs` runs a script against
real Yjs (pinned to 13.6.32, as the corpus), `tests/Ycs.Properties/Adapter.cs` (the only file of the
project that touches the Ycs API) runs it against Ycs. Both print, after every step, the same
canonical JSON observation, and the two are compared character for character.

### How it relates to the other differential suites

`TraceReplayTests` (`interop/traces`), `EventTraceTests`, `PendingTraceTests` (in `tests/Ycs.Tests`)
and `UndoDifferentialTests` (`tests/Ycs.Corpus`, `interop/corpus/undo-gen.mjs`) each replay sessions
of one area recorded from Yjs on **one** Ycs document, with Yjs' own updates as the remote changes:
shared types and their events, nested maps and arrays, out-of-order delivery, the UndoManager. This
engine does not repeat them; it differs in kind:

* every client is a Ycs document that makes its own updates (so Ycs' updates, cleanups, undo
  transactions and their bytes are compared, not only how Ycs reads Yjs' bytes), and the network
  steps (deliver in any format, y-protocols sync, merge, diff, conversion) are part of the script;
* one language covers all areas at once, so interactions between them (an undo of a format in an XML
  text a remote peer moved, a relative position in a nested type after a merged update) are generated;
* scripts are generated and shrunk live (`soak.sh`), pairwise fixtures cover every pair of
  operation kinds, and the properties and the structure-aware fuzzer need no recording at all.

What the area suites check in more depth (every observer of every type in `EventTraceTests`, long
undo sessions with `captureTransaction` and `ignoreRemoteMapChanges` in `UndoDifferentialTests`,
long traces) stays theirs: add an operation here when it needs the cross-area or multi-client view.

### What each layer proves, and what it does not

| Layer | Proves | Does not prove |
| --- | --- | --- |
| Recorded fixtures (`RecordedTests`, `interop/properties/fixtures/*.jsonl`) | On 190 pairwise scripts (every unordered pair of the 19 operation kinds, and each with itself, made concurrently by two or three clients, then exchanged in random order with duplicates and mixed formats), 150 random scripts, 170 scripts with the windows of the run extension (`windows.jsonl`: 150 of the generator, 20 long lists), 80 fuzzed ones and the scripts of the fixed known failures (`FixedFailures.cs`), every step's observation equals Yjs'. Needs no Node. | Anything outside these scripts. Intermediate steps are stored as hashes (`he`, `hs`), so a failure says which step differs; `YCS_PROP_EXPLAIN` (with Node) says how. |
| Live soak (`SoakTests.LiveSoak_ScriptsAgreeWithYjs`, `interop/properties/soak.sh`) | The same on any number of fresh scripts (seeded, deterministic); each distinct mismatch is shrunk to a minimal script. | Behaviour the generator does not produce (see the coverage table); timing-dependent behaviour (an UndoManager's capture timeout is 0 or effectively infinite). |
| Properties (`PropertyTests`, `Properties.cs`) | Laws that need no oracle, on generated scripts: convergence, encode round trip, merge of any partition, diff, v1/v2 conversion, undo/redo, snapshot stability, relative positions, GC invisibility, naive recomputation, the Lean item-list invariants, idempotence, commutativity, event replay, clean rejection of fuzzed updates; and three comparisons with a reference inside Ycs (see [below](#reference-comparisons)). | That the laws match Yjs: where Yjs itself breaks one the property's generator leaves that out, or the property is stated as Yjs satisfies it (see [laws Yjs breaks](#laws-yjs-breaks)). `EventReplay_HoldsForYjs` checks the event-replay property on Yjs. |
| Structure-aware fuzzer (`UpdateFuzz.cs`, `UpdateFuzzTests`) | Updates encoded by the tests' own v1 encoder from a model (several synthetic clients in any order, runs split where Yjs merges, origins inside other runs and inside map values, parents that arrive later, Skip and GC structs, overlapping/repeated/unsorted delete sets, a map value's tail deleted with its map, clocks near 2^31 - 1, nesting at the limit (`UpdateLimits.MaxDepth`), clients with no structs) are applied, or rejected with `YUpdateException` and the document unchanged; with Node, as Yjs applies them. | Byte-level damage (that is `tests/Ycs.Fuzz`); v2 input other than Ycs' and Yjs' own conversions of the model's v1. |

A divergence that is a bug in Ycs is pinned in `KnownFailures.cs` (see [pinning](#from-a-minimal-script-to-a-pinned-test)).

### The language

A script is `{"clients": [{"id", "gc", "quiet"?, "gcf"?}...], "roots": [{"name", "kind": "text"|"array"|"map"|"xml"}...], "steps": [...], "compact"?}`.
Client ids are fixed (conflicts resolve the same on both sides); every client has every root,
observed by a shallow and a deep observer and an `afterTransaction` handler, unless it is `quiet`
(none: Ycs extends runs there; what such a client observes later, with `observe` or an UndoManager,
is compared). With `gcf: k` a client's garbage collection filter keeps the items whose clock is a
multiple of k, and each call is an event (`gcf`, id and length). A client whose own clock moved in a
non-local transaction (local edits in a transaction an update was applied in: Yjs makes it non-local)
draws a random client id, as in Yjs; both interpreters replace it with the next of 5,000,000,000 + n
in that transaction's `update` handler (the first thing called after the change that is not an
observer, before a later transaction of the batch uses the id) and before every step. Every operation is total: indexes are clamped, a path
that does not resolve to a type of the right kind makes the step a recorded no-op (`"r": "skip"`),
an exception is observed as `"err": true`. So a script stays valid when steps are removed, which is
what makes shrinking work.

Local steps have `"c"` (the client) and `"path"` (`[root, index or key, ...]`, through arrays,
XML children and map keys):

| `op` | Fields | API |
| --- | --- | --- |
| `insert`, `delete`, `format`, `embed`, `delta` | `at`, `text`, `len`, `attrs`, `value`, `delta` (`[{insert, retain, delete, attrs}]`), `sanitize` | `YText`/`YXmlText` `Insert`, `Delete`, `Format`, `InsertEmbed`, `ApplyDelta` |
| `setAttr`, `rmAttr` | `key`, `value` | `YText`/`YXmlElement` `SetAttribute`, `RemoveAttribute` |
| `ains`, `push`, `unshift`, `delete`, `xafter` | `at`, `values`, `ref` | `YArray`/`YXmlFragment` `Insert`, `Add`, `RemoveRange`, `InsertAfter` |
| `set`, `mdel`, `clear` | `key`, `value` | `YMap` indexer, `Remove`, `Clear` |
| `transact` | `origin`, `ops` (local steps without `c`, and `deliver`s to this client: an update applied inside the transaction) | `YDoc.Transact` |
| `um`, `undo`, `redo`, `stop`, `umclear` | `id`, `scope` (paths or `"$doc"`), `timeout` (`"0"` or `"inf"`), `origins` | `UndoManager` |
| `snap`, `readSnap`, `docFromSnap` | `id`, `prev` | `CreateSnapshot`, `ToDelta(snapshot, prev)`, `ToArray(snapshot)`, `ToDictionary(snapshot)`, `CreateDocFromSnapshot`, `EncodeSnapshot(V2)` |
| `rpos`, `rres` | `id`, `at`, `assoc` (`rres` on any client) | `RelativePosition.FromIndex`, `Encode`, `WriteJson`, `Decode`, `TryResolve` |
| `observe` | | `Observe`/`ObserveDeep` on a nested type |
| `react` | `id`, `ops`, `txn` | `Observe` on any type: the first time it changes, the handler makes `ops` (in one `Transact` with `txn`, else each in its own), also during another transaction's cleanup; the results are an event (`react`) |

Network steps: `deliver` (`from`, `k`: the k-th update `from` made, `to`, `via`: `v1`, `v2`,
`v1to2`, `v2to1`), `sync` (`from`, `to`, `via`; y-protocols' step 1 / step 2 through
`SyncProtocol`; with `diff`, step 2 is `DiffUpdate` of the whole state), `merged` (`from`:
`[[client, k]...]`, `to`, `via`, `diff`: `MergeUpdates(V2)`, `DiffUpdate(V2)`, `ParseUpdateMeta`,
`EncodeStateVectorFromUpdate`), `raw` (`to`, `u`: base64 bytes, `via`; the fuzzer's updates).

Values: JSON null, booleans, strings (lone surrogates included), integers, decimals; and tagged
`{"$i": "4294967296"}` (an integer), `{"$f": "NaN" | "Infinity" | "-Infinity" | "-0" | 16 hex digits}`
(a double by its bits), `{"$n": "123"}` (BigInt), `{"$b": base64}` (bytes), `{"$t": "text"|"array"|"map"|"xmlf"|"xmle"|"xmlt", "s"?, "v"?, "n"?, "a"?, "dl"?, "dls"?}`
(a new shared type with prelim content; for a text, `dl` is a delta applied before it is in a document, with
`sanitize` false when `dls` is false), `{"$doc": {"g", "autoLoad"}}` (a subdocument). Object keys
never look like integers (JavaScript would reorder them).

The observation of a step: `r` (the result), `err`, `ev` (every event, in order: each observer's
events with target path, relative path, kind, delta, keys with old values, attribute and child-list
flags, origin and local flag; `afterTransaction` with the changed types and keys; the v1 and v2 bytes
of every `update`; `subdocs`; UndoManager stack events), and `st` for the client the step touched:
the content of every root (a deep view with kinds, `toJSON`, length), the state vector (decoded and
encoded), the delete set, `EncodeStateAsUpdate` and `EncodeStateAsUpdateV2` (bytes; hashed in
compact scripts) and whether structs are pending. The last observation (`"i": "end"`) holds every
client's full state.

Coverage (which observations an operation kind is compared on; ● always, ○ where it applies):

| Operation kind | content | sv / ds | v1 / v2 state bytes | update bytes | events | result |
| --- | --- | --- | --- | --- | --- | --- |
| text insert/delete/format/embed/delta (`ApplyDelta`: retains with attributes within and beyond the end, deletes, inserts of strings, embeds and shared types, `sanitize`; on a text before it is in a document), text attributes | ● | ● | ● | ● | ● (delta, keys) | |
| array insert/push/unshift/delete, every value kind | ● | ● | ● | ● | ● (delta) | |
| map set/delete/clear, concurrent keys | ● | ● | ● | ● | ● (keys, old values) | |
| XML insert/insertAfter/delete/attributes, XML text | ● | ● | ● | ● | ● (delta, attributes, child list) | |
| nested types, subdocuments | ● | ● | ● | ● | ● (deep, paths; subdocs) | |
| transactions with origins | ● | ● | ● | ● | ● (one transaction, origin) | ○ per inner op |
| undo/redo/stopCapturing/clear | ● | ● | ● | ● | ● (stack events) | ● undo/redo returned, canUndo/canRedo, stack size |
| snapshots | | | | | ● (read transactions) | ● encoded snapshot v1/v2, reads, document from snapshot |
| relative positions | | | | | | ● encoding, JSON, resolution on any client |
| deliver (v1, v2, converted) | ● | ● | ● | ● | ● | ○ converted bytes |
| sync (step 1 / step 2, diff) | ● | ● | ● | ● | ● | ● both messages |
| merged (merge, diff) | ● | ● | ● | ● | ● | ● merged and diffed bytes, meta, state vector from update |
| raw (fuzzer) | ● | ● | ● | ● | ● | ○ converted bytes |

Not compared: wall-clock behaviour (capture timeouts other than 0 and infinite); random ids (a
subdocument's guid is given); Yjs' warnings on the console; `PermanentUserData`, `YXmlHook` and
`CreateTreeWalker` (not in the language yet).

### Running

```bash
dotnet test tests/Ycs.Properties -c Release                       # recorded, properties (2,000 scripts each), fuzz rejection: about 2 minutes
(cd interop/properties && npm ci)                                 # once, for everything below
interop/properties/soak.sh 200000 1 8                             # live soak: scripts, first seed, Node workers
YCS_PROP_SEEDS=30000 dotnet test tests/Ycs.Properties -c Release --filter "FullyQualifiedName~PropertyTests"
YCS_PROP_ONLY=run-extension,merge-order YCS_PROP_SEEDS=30000 dotnet test tests/Ycs.Properties -c Release --filter "FullyQualifiedName~Property_HoldsOnGeneratedScripts"
YCS_PROP_FUZZ=5000 dotnet test tests/Ycs.Properties -c Release --filter Name=StructuredUpdates_AgreeWithYjs
YCS_PROP_EXPLAIN=interop/properties/failures/x.json dotnet test tests/Ycs.Properties -c Release \
  --filter Name=Explain_PrintsBothObservations --logger "console;verbosity=detailed"   # one script, both sides, every difference
YCS_PROP_RECORD=1 dotnet test tests/Ycs.Properties -c Release --filter Name=RecordFixtures   # regenerate the fixtures
```

The soak prints each distinct mismatch (by signature: the operation of the first differing step,
the most severe difference, which side threw) with its minimal script, written to
`interop/properties/failures/` (ignored by git), and counts the mismatches known failures explain.
It fails if a mismatch is not known. Debug builds make the library's `Debug.Assert`s live (they turn
up as failures of their own); the soak and the fixtures use Release. Set `YCS_PROP_DEBUG=1` to print
the exceptions the operations threw.

### Reference comparisons

Four properties compare Ycs with itself, run step by step on the same script with an internal switch flipped
(set through `Adapter.cs`' accessors; script options only Ycs reads): every other seed is a script of the general
generator, the others one of `ListScripts.cs` (a text and an array grown to a few hundred elements by typing runs,
backspaces, random inserts, deletes and pushes, most of them in one transaction, with a late UndoManager, formats
and syncs: the general scripts stay too short to make search markers). `merge-order`, `run-extension` and
`walk-index` take them with the windows of the run extension (`GenOptions.Windows`; in `ListScripts`, updates applied in the middle of
transactions); `markers-invisible` without (where Yjs leaves markers stale, markers on and off differ in Yjs too).

| Property | Candidate | Reference | Compared after every step |
| --- | --- | --- | --- |
| `markers-invisible` | search markers on | off (`YDoc.SearchMarkersDisabledForTests`, option `markers: false`) | every observation (content, encodings, results, events); and every marker of a live type points at an item of its list with the right index (`YArrayBase.SearchMarkerViolations`, usable from a Debug session too) |
| `merge-order` | the end of a transaction merges a run in one pass | Yjs' pair-by-pair order (`StructStore.PairwiseMergeForTests`, option `merge: "pairwise"`), inserts as items of their own in both | the struct store of every client (`Replica.StoreDump`: kind, id, length, deletion, content, left/right, origins, parent, key, keep, redone, marker flag; each type's map entries and markers) and the observations |
| `walk-index` | the marker walks through the run-head index (`ClientStructs`: the walk-left to the run's head, the walk right and the middle walk jumping within a run, the callers' walks from the marker), asked from the first step (option `walk: "eager"`) | Yjs' loops item by item (`YDoc.RunHeadIndexDisabledForTests`, option `walk: "plain"`) | where every marker lookup ends (item and index, `YDoc.MarkerLookupsForTests`), the store as `merge-order` (markers included) and the observations; with run extension as the script has it and off |
| `run-extension` | inserts that continue a run extend it in place | every insert an item of its own (`YDoc.RunExtensionsDisabledForTests`, option `extend: false`), merged pair by pair | the store as above, search markers included (between steps every marker sits where Yjs' would), and the observations; once as the script has it (quiet clients extend runs), once with every client quiet (script option `quiet`) |

The test suites (`Ycs.Tests`, `Ycs.Corpus`, `Ycs.Fuzz`, `Ycs.Properties`) recount the struct lists after every
transaction (`StructStore.CheckAfterTransactionsForTests`: the start clocks, the leaves and their directory (fill,
first starts and positions, the has-break and has-format bits, the tree of the sums), the run-head index's flags,
links and per-leaf sums and counts; lists over 1,024 structs every count/256th time); `Ycs.Corpus` uses leaves of 2
positions and `Ycs.Properties` leaves of 4 (`ClientStructs.LeafCapacityForTests`; `YCS_LEAF_CAPACITY` sets it in
`Ycs.Tests` and `Ycs.Properties`), so that short lists split and merge leaves (`YCS_NO_RECOUNT=1` leaves the recount
out, to see what a property alone finds). `walk-index` fails when the index misses an update: the refresh of an item
another one was integrated before, the leaf sums after a split, the leaf sums after a merge.

Each fails on a sabotaged change (a marker index off by one in a merge, a map entry not moved to the struct a
run merges into, the split before the observers left out, the surrogate condition of the run extension dropped);
`run-extension` also on the version of the run extension the review refused (f857189).

### Laws Yjs breaks

A property violation is decided against Yjs before anything changes: the script-level properties
(diff, undo/redo, relative positions, GC invisibility, commutativity, event replay) are scripts the
oracle runs as they are (`YCS_PROP_EXPLAIN`); `interop/properties/laws.mjs` runs merge-partition and
idempotence, which do more than a script, against Yjs with the same random choices
(`node laws.mjs <property> < script.json`). Where Yjs breaks the law the same way, the property
leaves the case out or is stated as Yjs satisfies it, and `YjsLaws.cs` holds a script that shows
the break, recorded with Yjs' observations (`fixtures/laws.jsonl`) and replayed by
`Recorded_ObservationsEqualYjs`, so that Ycs keeps breaking it exactly as Yjs does. Where Ycs alone
breaks it, it is a bug of Ycs (see below). Yjs 13.6.32 breaks:

| Law | Why (Yjs and Ycs alike) | The property |
| --- | --- | --- |
| idempotence, undo/redo, diff, event replay, snapshot reads, encode round trip, conversion | Splitting an item after a lone surrogate or between the halves of a surrogate pair replaces them with U+FFFD in place, without an update or an event. Splits happen where no change asks for one: applying an update the document already has splits items at its structs' origins (`integrateStructs` calls `getMissing` before it finds the struct known), an undo or a retain splits at its ends. A lone surrogate is also written as U+FFFD. | generated without lone surrogates (the round trips and conversion) or without characters outside the BMP too (the others) |
| merge-partition, diff-update | A text cleans up its formatting after a remote transaction (`cleanupYTextAfterTransaction`, a local transaction of the receiver), deleting the formats that are redundant at that moment, which depends on what the transaction brought: the same updates applied one by one, merged into one, or as a diff, end with different attributes. | when a receiver cleaned up, the content is compared without attributes (and diff-update's state vector without the receivers' own clients) |
| gc-invisible | Observers of different types are called in the order in which the transaction first changed each type, and a deleted item collected into a GC struct changes none. A GC struct waits for no parent, so a document that lacks the parent integrates it, and what its client did after it, at once. A relative position in a collected type does not resolve. | each observer's calls are compared in order, not the order between observers; resolutions into deleted types are left out; from the first step at which something waits in either variant, only the end content of the clients with nothing waiting is compared; the stacks of an UndoManager scoped to the document are left out (it captures every transaction, and how many empty cleanup transactions an update opens depends on gc) |
| relpos-stable | A relative position on an item that an undo or a redo re-creates follows it only on the replica that made it (`Item.redone` is local). (The first violation was no break: the property assumed the full sync its scripts end with, which shrinking removed.) | only after a full sync (every client at the same state vector, nothing pending), and without an undo or a redo after the position was taken |
| undo-all-restores | Redoing every change re-creates a nested type that was created and then edited, and not every edit made inside it is redone. | the redos are checked only in scripts that edit no nested type |

### Normalizations and known failures

`KnownFailures.cs` lists every divergence found and not fixed, most severe first, each with its
minimal script, its severity and why. An entry may carry:

* `Matches`: recognizes a mismatch it causes (the soak, the fuzzer and the recorder use it);
* `Normalize`: rewrites both sides' observations so that a divergence that shows in nearly every
  script does not hide the others (none is needed today: the ones the first runs needed, for
  `ToDelta` opening a transaction, the missing `stack-item-updated` and pending structs left out of
  the encodings, went with their fixes in `modernize`);
* `Property`: the signature of a property violation it explains;
* `Documented`: not a bug of Ycs (a documented difference, or a Yjs bug); `DebugOnly`.

`PinnedTests` replays every entry against the Yjs observations recorded for it
(`fixtures/pinned.jsonl`), with every other entry's normalization: Inconclusive (with the reason)
while Ycs still differs, failing once it agrees, so that the entry, with its normalization, goes with
the fix. A recorded fixture that a known failure explained when it was recorded is Inconclusive with
that name; any other mismatch fails.

### From a minimal failing script to a pinned test

1. Take the minimal script the soak (or `PropertyTests`, or the fuzzer) printed, check it with
   `YCS_PROP_EXPLAIN` (for a property violation, also check Yjs does not break the property on the
   same script: the script-level properties produce scripts the oracle runs as they are).
2. Add an entry to `KnownFailures.All` with the script, a severity, what Yjs does and what Ycs does,
   and a `Matches` (or `Property`) narrow enough not to hide other bugs.
3. `YCS_PROP_RECORD=1 ... --filter Name=RecordFixtures` (records its Yjs observations and marks the
   fixtures it explains; the recorder fails on a mismatch nothing explains), then
   `dotnet test tests/Ycs.Properties`: the new pinned test is Inconclusive.

When the bug is fixed the pinned test fails: move the entry to `FixedFailures.cs` (with what the fix was) and
record the fixtures again. Its script stays as a regression case: a divergence's is recorded in
`fixtures/fixed.jsonl` and replayed by `Recorded_ObservationsEqualYjs`, a property's is checked by
`FixedProperties_Hold`.

### Adding an operation

1. Add it to `Run.local` (or `Run.step` for a network step) in `oracle.mjs` and to `Client.Local`
   (or `ScriptRun.Dispatch`) in `Adapter.cs`, observing the same things in the same canonical form
   (numbers as `view`/`View` render them, missing values left out).
2. Generate it in `Generator.cs` (a new `OpKinds` constant, in `OpKinds.Paired` to get its pairwise
   fixtures), and list it in `RecordedTests.Fixtures_CoverEveryOperationAndPair`.
3. Run a short soak; record the fixtures.
