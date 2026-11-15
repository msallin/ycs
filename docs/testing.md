# Testing

What each project guards, and how to run, soak and update them. CI
(`.github/workflows/ci.yml`) runs all of it on every push.

| Project | Guards | Run |
| --- | --- | --- |
| `tests/Ycs.Tests` | Behaviour: unit tests, updates written by real Yjs (byte-identical re-encoding), 40 randomized concurrent sessions replayed from Yjs, 1,425 differential traces of the shared types and their events (see below), and the public API baseline (`PublicApi.txt`). | `dotnet test tests/Ycs.Tests` |
| `tests/Ycs.Corpus` | Convergence with Yjs on a recorded corpus of Yjs 13.6.32 sessions (`interop/corpus/`): the observer's state byte for byte, and for the scripted sessions (concurrent formatting) every client's, replayed with a Ycs document per client (`ClientReplayTests`); and the UndoManager, step by step, on random sessions run in Yjs (`UndoDifferentialTests`, `interop/corpus/undo-gen.mjs`). | `dotnet test tests/Ycs.Corpus` |
| `tests/Ycs.Fuzz` | Robustness of `ApplyUpdate`/`ApplyUpdateV2` against damaged input. | `dotnet test tests/Ycs.Fuzz -c Release` |
| `tests/Ycs.Properties` | Differential testing against real Yjs over the whole API (texts, arrays, maps, XML, nested types, subdocuments, transactions, undo, snapshots, relative positions, events, every network path and encoding), on recorded scripts (no Node) and in a live soak; properties that need no oracle; a structure-aware update fuzzer. See [below](#model-based-differential-testing-testsycsproperties). | `dotnet test tests/Ycs.Properties -c Release` |
| `tests/Ycs.Budgets` | Allocated bytes and time per operation for a fixed set of scenarios. | `dotnet test tests/Ycs.Budgets -c Release --logger "console;verbosity=detailed"` |
| `samples/Ycs.AotSample`, `samples/Ycs.WasmSample` | Native AOT and browser-wasm: publish without trim/AOT warnings and run. | see the README |
| `interop/run.sh`, `interop/corpus/roundtrip.sh` | Live round-trips against real Yjs (Node). | see the README |
| `bench/Ycs.Benchmarks` | Not a test: the editing-trace benchmark behind the README's numbers. | `bench/Ycs.Benchmarks/get-trace.sh`, then `dotnet run -c Release --project bench/Ycs.Benchmarks -- <trace.json.gz>` |

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

## Fuzzing (`tests/Ycs.Fuzz`)

The seeds are every update of the Yjs corpus (applied on top of the updates before it) and
updates generated in C# (`Seeds.cs`), in v1 and v2. `Mutator.cs` damages them: bit flips,
truncation, inserted, deleted, duplicated, swapped and overwritten chunks, var-uints replaced by
huge values, random bytes. For each input (`Harness.cs`):

* it finishes within 2 s (it runs on a worker thread; a hung one is abandoned);
* it allocates at most 64 MiB (for an input over 4 KiB, 16 KiB per input byte). The test process runs with a
  512 MiB GC heap limit (`runtimeconfig.template.json`), so a decoder that trusts a huge length
  fails at once with `OutOfMemoryException`, and the throw site names the allocation;
* it throws nothing but `YUpdateException` (`Harness.AllowedExceptionTypes`);
* when it throws, the document's text, state vector and v1 encoding are unchanged.

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

## Model-based differential testing (`tests/Ycs.Properties`)

One operation language, two interpreters: `interop/properties/oracle.mjs` runs a script against
real Yjs (pinned to 13.6.32, as the corpus), `tests/Ycs.Properties/Adapter.cs` (the only file of the
project that touches the Ycs API) runs it against Ycs. Both print, after every step, the same
canonical JSON observation, and the two are compared character for character.

### What each layer proves, and what it does not

| Layer | Proves | Does not prove |
| --- | --- | --- |
| Recorded fixtures (`RecordedTests`, `interop/properties/fixtures/*.jsonl`) | On 190 pairwise scripts (every unordered pair of the 19 operation kinds, and each with itself, made concurrently by two or three clients, then exchanged in random order with duplicates and mixed formats), 150 random scripts and 80 fuzzed ones, every step's observation equals Yjs'. Needs no Node. | Anything outside these 420 scripts. Intermediate steps are stored as hashes (`he`, `hs`), so a failure says which step differs; `YCS_PROP_EXPLAIN` (with Node) says how. |
| Live soak (`SoakTests.LiveSoak_ScriptsAgreeWithYjs`, `interop/properties/soak.sh`) | The same on any number of fresh scripts (seeded, deterministic); each distinct mismatch is shrunk to a minimal script. | Behaviour the generator does not produce (see the coverage table); timing-dependent behaviour (an UndoManager's capture timeout is 0 or effectively infinite). |
| Properties (`PropertyTests`, `Properties.cs`) | Laws that need no oracle, on generated scripts: convergence, encode round trip, merge of any partition, diff, v1/v2 conversion, undo/redo, snapshot stability, relative positions, GC invisibility, naive recomputation, the Lean item-list invariants, idempotence, commutativity, event replay, clean rejection of fuzzed updates. | That the laws match Yjs: where Yjs itself breaks one (a split surrogate pair, a lone surrogate, a format without attributes) the property's generator leaves that out, and says so. `EventReplay_HoldsForYjs` checks the event-replay property on Yjs. |
| Structure-aware fuzzer (`UpdateFuzz.cs`, `UpdateFuzzTests`) | Updates encoded by the tests' own v1 encoder from a model (several synthetic clients in any order, runs split where Yjs merges, origins inside other runs, parents that arrive later, Skip and GC structs, overlapping/repeated/unsorted delete sets, clocks near 2^31 - 1, nesting near 256, clients with no structs) are applied, or rejected with `YUpdateException` and the document unchanged; with Node, as Yjs applies them. | Byte-level damage (that is `tests/Ycs.Fuzz`); v2 input other than Ycs' and Yjs' own conversions of the model's v1. |

A divergence that is a bug in Ycs is pinned in `KnownFailures.cs` (see [pinning](#from-a-minimal-script-to-a-pinned-test)).

### The language

A script is `{"clients": [{"id", "gc"}...], "roots": [{"name", "kind": "text"|"array"|"map"|"xml"}...], "steps": [...], "compact"?}`.
Client ids are fixed (conflicts resolve the same on both sides); every client has every root,
observed by a shallow and a deep observer. Every operation is total: indexes are clamped, a path
that does not resolve to a type of the right kind makes the step a recorded no-op (`"r": "skip"`),
an exception is observed as `"err": true`. So a script stays valid when steps are removed, which is
what makes shrinking work.

Local steps have `"c"` (the client) and `"path"` (`[root, index or key, ...]`, through arrays,
XML children and map keys):

| `op` | Fields | API |
| --- | --- | --- |
| `insert`, `delete`, `format`, `embed`, `delta` | `at`, `text`, `len`, `attrs`, `value`, `delta` (`[{insert, retain, delete, attrs}]`), `sanitize` | `YText`/`YXmlText` `Insert`, `Delete`, `Format`, `InsertEmbed`, `ApplyDelta` |
| `setAttr`, `rmAttr` | `key`, `value` | `YText`/`YXmlElement` `SetAttribute`, `RemoveAttribute` |
| `ains`, `push`, `unshift`, `delete`, `xafter` | `at`, `values`, `ref` | `YArray`/`YXmlFragment` `Insert`, `Push`, `Unshift`, `Delete`, `InsertAfter` |
| `set`, `mdel`, `clear` | `key`, `value` | `YMap` `Set`, `Delete`, `Clear` |
| `transact` | `origin`, `ops` (local steps without `c`) | `YDoc.Transact` |
| `um`, `undo`, `redo`, `stop`, `umclear` | `id`, `scope` (paths or `"$doc"`), `timeout` (`"0"` or `"inf"`), `origins` | `UndoManager` |
| `snap`, `readSnap`, `docFromSnap` | `id`, `prev` | `CreateSnapshot`, `ToDelta(snapshot, prev)`, `ToArraySnapshot`, `GetAllSnapshot`, `CreateDocFromSnapshot`, `EncodeSnapshot(V2)` |
| `rpos`, `rres` | `id`, `at`, `assoc` (`rres` on any client) | `RelativePosition.FromTypeIndex`, `EncodeRelativePosition`, `RelativePositionToJSON`, `DecodeRelativePosition`, `AbsolutePosition.TryCreateFromRelativePosition` |
| `observe` | | `Observe`/`ObserveDeep` on a nested type |

Network steps: `deliver` (`from`, `k`: the k-th update `from` made, `to`, `via`: `v1`, `v2`,
`v1to2`, `v2to1`), `sync` (`from`, `to`, `via`; y-protocols' step 1 / step 2 through
`SyncProtocol`; with `diff`, step 2 is `DiffUpdate` of the whole state), `merged` (`from`:
`[[client, k]...]`, `to`, `via`, `diff`: `MergeUpdates(V2)`, `DiffUpdate(V2)`, `ParseUpdateMeta`,
`EncodeStateVectorFromUpdate`), `raw` (`to`, `u`: base64 bytes, `via`; the fuzzer's updates).

Values: JSON null, booleans, strings (lone surrogates included), integers, decimals; and tagged
`{"$i": "4294967296"}` (an integer), `{"$f": "NaN" | "Infinity" | "-Infinity" | "-0" | 16 hex digits}`
(a double by its bits), `{"$n": "123"}` (BigInt), `{"$b": base64}` (bytes), `{"$t": "text"|"array"|"map"|"xmlf"|"xmle"|"xmlt", "s"?, "v"?, "n"?, "a"?}`
(a new shared type with prelim content), `{"$doc": {"g", "autoLoad"}}` (a subdocument). Object keys
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
| text insert/delete/format/embed/delta, text attributes | ● | ● | ● | ● | ● (delta, keys) | |
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
`YXmlTreeWalker` (not in the language yet).

### Running

```bash
dotnet test tests/Ycs.Properties -c Release                       # recorded, properties, fuzz rejection: about 8 s
(cd interop/properties && npm ci)                                 # once, for everything below
interop/properties/soak.sh 200000 1 8                             # live soak: scripts, first seed, Node workers
YCS_PROP_SEEDS=3000 dotnet test tests/Ycs.Properties -c Release --filter "FullyQualifiedName~PropertyTests"
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

### Normalizations and known failures

`KnownFailures.cs` lists every divergence found and not fixed, most severe first, each with its
minimal script, its severity and why. An entry may carry:

* `Matches`: recognizes a mismatch it causes (the soak, the fuzzer and the recorder use it);
* `Normalize`: rewrites both sides' observations so that a divergence that shows in nearly every
  script (`ToDelta` opening a transaction, `stack-item-updated`, pending structs left out of the
  encodings) does not hide the others;
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

When the bug is fixed the pinned test fails: delete the entry and record the fixtures again.

### Adding an operation

1. Add it to `Run.local` (or `Run.step` for a network step) in `oracle.mjs` and to `Client.Local`
   (or `ScriptRun.Dispatch`) in `Adapter.cs`, observing the same things in the same canonical form
   (numbers as `view`/`View` render them, missing values left out).
2. Generate it in `Generator.cs` (a new `OpKinds` constant, in `OpKinds.Paired` to get its pairwise
   fixtures), and list it in `RecordedTests.Fixtures_CoverEveryOperationAndPair`.
3. Run a short soak; record the fixtures.
