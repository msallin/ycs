# ycs second-round review: `modernize` at 8d40781 against Yjs 13.6.32

## Status

Where each finding stands on branch `fix-r2` (with `modernize` at bcda59a merged). The review below
describes the code at 8d40781; its probes are in `plenum-reviews/ycs-review-r2/` (`js/`, `probe/`),
and those that showed a finding are tests now (named in each commit).

| Finding | Status |
| --- | --- |
| C1 pending data bounded in compressed bytes, re-read without limit | fixed in 18e4297 (`UpdateLimits.MaxPendingStructs`; the re-read is decoded under the counted structs) |
| C2 default `MaxStructsPerUpdate` of 2,097,152, wrong documented cost | fixed in 18e4297 (131,072; the measured costs in api.md; settable below one struct per byte) |
| C3 no struct limit for v1 | fixed in 18e4297 |
| C4 quadratic conflict loop | fixed in 4498ff9: no per-item sets (97 s / 111 GiB to 4.8 s / 11 MiB for 40,000 after 40,000) and `UpdateLimits.MaxIntegrationSteps` (2^24); beyond it the update is applied in part (`YUpdateException.PartlyApplied`), see api.md |
| C5 `MaxPendingBytes` skipped on a retry | fixed in 18e4297 (a retry may add up to the limits: pending stays below twice them) |
| C6 `Transaction.Changed.Remove` O(n) | fixed in af0bf42 |
| C7 texts to clean up in a list | fixed in a4ee5a7 (the split's insert into the struct array, which Yjs shares, remains) |
| C8 a whole `YDoc` per decoded subdocument; children get default limits | fixed in a6f4d0c (created at integration, 157 to 119 MiB for 100,000; the parent's limits) |
| C9 `UpdateOperations` expand v2 without bound | fixed in 18e4297 (their `limits` argument; the expansion is documented) |
| E1 `undefined` subdocument option read as null | fixed in 106f12e |
| E2 non-string `guid` / `collectionid` rejected | fixed in 106f12e for `collectionid` (ignored); a non-string or undefined `guid` is still rejected on purpose (Yjs peers would not agree on the subdocument) |
| E3 late events change the order of `ChangedParentTypes` | fixed in af0bf42 |
| E4 a delta cannot express an embed of null | fixed in c85205e (`Delta.NullEmbed`) |
| E5 key order of a retain's attributes | not fixed: not reproduced (3,000 random formatting transactions, every delta equal to Yjs' in key order) |
| P1 deleting backwards copies the deleted run | fixed in 1abbf51 (40,000: 495 ms / 3.1 GiB to 316 ms / 28 MiB; undo of typing 1,774 ms to 411 ms) |
| P2 merged capture rebuilds both delete sets | fixed in b46fc29 (26.8 s / 32 GiB to 1.8 s / 70 MiB) |
| P3 deep-event sort recomputes `Path` | fixed in 6fa75de (1,019 ms to 22 ms) |
| P4 `ContentJson.GetContent` scans per call | fixed in 3746ba8 (3.6 s to 3 ms) |
| M1 three inconsistent limits | fixed in 18e4297 (one immutable `UpdateLimits`, no process-wide static) |
| M2 `docs/api.md` inaccurate on limits | fixed in 18e4297 and 4498ff9 (measured costs, how to raise the limits for trusted input, `OutOfMemoryException` named) |
| M3 stale comments | fixed in fc9b08d |
| M4 2^53 - 1 and JS coercions duplicated | fixed in dac8fdf (`JsValue`) |
| M5 orders resting on enumeration order | fixed in af0bf42 (`Transaction.Changed` is an ordered dictionary with holes; the invariant is stated at `ChangedParentTypes`, the UndoManager's set and the cleanup's set) |
| M6 the fuzz harness cannot see what this round changed | fixed in 20d6474 (observed variant, hostile seeds, a time ceiling) |

The review's probes again, on `fix-r2` (same machine, Release; "before" is 283736d):

| Probe | Before | After |
| --- | --- | --- |
| C1 `retry 6`: six 25-byte updates of 2,097,152 pending GC structs, then the trigger | all accepted; `EncodeStateAsUpdate` 25 MiB in 1.1 s, 2.35 GiB; the retry 880 MiB | each rejected in under 5 ms; the largest re-read at the limits (`bench ... limits`): 332 MiB allocated, 73 MiB kept |
| C2 `deleted`, `types`: 2,097,152 deleted items / 200,000 types in 30 bytes | 1,312 MiB allocated, 553 MiB kept / 215 to 226 MiB | rejected in under 5 ms; at the limit of 131,072: 38 MiB / 88 to 105 MiB |
| C3 `typesv1`: 200,000 texts in 800 KB of v1 | applied, 219 MiB | rejected |
| C4 `conflict`: 40,000 after 40,000 (28 bytes) | 97 s, 111 GiB | 63 ms, 8 MiB (budget; without it 4.8 s, 11 MiB) |
| C5 `bypass`: a trigger that brings a 100 KB orphan, limit 10,000 bytes | accepted, pending 100 KB | rejected, pending unchanged |
| C6 `perf changed`: 80,000 maps set and deleted in one transaction | 30.3 s | 0.15 s |
| C7 `perf fmt`: 80,000 texts formatted remotely | 14.4 s | 3.6 s (the rest is the struct-array splice Yjs shares; 40,000 whole-text formats without a split: 4.3 s to 0.22 s) |
| C8 `subdocs`: 100,000 subdocuments in 100 KB of v2 | 157 MiB | 121 MiB (now bounded by the struct limit too) |
| C9 `ops`: the `UpdateOperations` functions on a 30-byte v2 update | up to 1.46 GiB (`DescribeUpdateV2`) | rejected in under 5 ms |
| P1 `perf undotext`: 40,000 undos of typing | 2,371 ms, 3.1 GiB | 211 ms, 61 MiB |
| P3 `perf deep`: 16,000 maps under a deep observer | 1,417 ms | 59 ms |
| P4 `perf json`: `Get(i)` over 80,000 legacy values | 6,241 ms | 8 ms |

Not a finding of the review, seen in the same probe: 40,000 redos of typing allocate 1.6 GiB (438 ms
before, 684 ms after; unchanged by these fixes). Fixed in f69e124: each redo split the undone run and
copied its rest; split strings now share their characters (513 ms and 1,598 MiB to 317 ms and 72 MiB,
budget `undo.redo-after-undoing-typing-100k`).


## The review

Scope: the changes of `40d7fdb..8d40781` under `src/Ycs` (42 files, +1795 / -936), read beside
`interop/corpus/node_modules/yjs/src`, and probed. Nothing in the repository or its worktrees was changed;
builds ran on a copy (`/tmp/ycs-review-r2/ycs`). Everything quoted below was run unless it says "by reading".

## Verdict

**Correct, and fit to pin for the Plenum Blazor client, on one condition: `MaxPendingBytes` is set there
explicitly.** No divergence from Yjs in documents, bytes or events was found where the three fix branches
meet: 4,400 differential scripts (173,358 steps: text with an observer that reads `event.delta`, map and
deep observers, an UndoManager, remote formatting, out-of-order delivery with pending, snapshots, relative
positions; client ids 1..3 and 2^32+1 / 2^52+7 / 2^53-1) are equal to Yjs after every step, and the
repository's suites are green (Ycs.Tests 2,580, Ycs.Corpus 3,366, Budgets). 4.4 million inputs of the repo's
fuzz harness and about 1.2 million of my own (observed documents, snapshots, relative positions,
PermanentUserData) produced nothing but `YUpdateException`, no partial apply. `Plenum.Blazor.Tests` builds
and passes against 8d40781 (181 of 181, `-p:YcsPath=`). **What this round got wrong is the resource side of
the hardening**: the new defaults let a 30-byte update allocate 0.4 to 2.3 GiB and stay in the document, the
pending limit counts compressed bytes and is skipped on a retry, and two new quadratic paths can be driven
by an update of under 1 MiB. For Plenum, whose relay does not apply updates and whose client takes v1 only
(`TryApplyUpdate`): (1) set `YDocOptions.MaxPendingBytes` (the client's spec relies on a limit that is now
off by default; 16 MiB was the old value, the relay's stored state is at most 12 MiB); (2) keep to v1 input
and never hand an untrusted v2 update to a `YDoc` or to `UpdateOperations` without lowering
`MaxStructsPerUpdate` (to about 100,000 to 250,000) on both; (3) know that with v1 there is no struct limit
at all, so an 8 MiB snapshot of minimal structs costs the tab about 2.2 GiB and a 160 KB update can freeze
it for minutes (Yjs' JavaScript client shares both, so the room is no worse off than today, but ycs does not
yet protect it); `TryApplyUpdate` can therefore still end in `OutOfMemoryException`. Findings C1 to C4 should
be fixed in ycs before it is called hardened for a server that applies updates.

## How it was checked

| What | Where | Result |
| --- | --- | --- |
| Repo suites on a copy of 8d40781 | `tests.log` | Tests 2,580 passed (3 skipped: Release-only), Corpus 3,366, Budgets passed |
| Repo fuzz harness, random seeds | `fuzz-soak.log`, `fuzz-soak2.log` | 400,000 + 4,000,000 inputs: 362,959 applied, 4,037,041 rejected, only `YUpdateException`, no violation |
| Cross-area differential (new) | `js/cross-gen.mjs`, `cross/Program.cs`, `cross-big.log` | 600 + 3,000 + 800 scripts, 173,358 steps, 0 diverged (v1 and v2 state incl. pending, `toDelta`, map JSON, every observer call with delta/keys/path, stack sizes, snapshot bytes and snapshot deltas, relative-position bytes and resolution) |
| Observed-document fuzz (new) | `probe/Observed.cs` | 43,000 documents, 981,243 updates (a third mutated, v1 and v2), observers reading every lazy event member, UndoManager, PermanentUserData, all reads, undo/redo, local edits after each: no exception but `YUpdateException`, no non-atomic rejection |
| Snapshot fuzz (new) | `probe/Fuzz.cs` `fsnap` | 100,000 mutated snapshots, gc on and off; every snapshot read, `CreateDocFromSnapshot`, `RestoreDocument`, `SnapshotContainsUpdate`: only `YUpdateException` (and the documented `InvalidOperationException` for a gc document), document bytes unchanged, nothing slow |
| Relative positions (new) | `frel` | 200,000 mutated binary positions and 100,000 odd JSON positions: only `YUpdateException`, round trips equal |
| PermanentUserData (new) | `fpud` | 80,000 sessions of a peer writing odd shapes into `users`: no exception |
| Subdocument options (new) | `js/subdoc-gen.mjs`, `probe/Subdoc.cs` | 4,000 option values: 2,314 equal to Yjs, 270 both reject, 1,385 rejected by ycs only (E2), 31 differ (E1) |
| Strings and JSON (new) | `js/strings-gen.mjs` | 32 cases (invalid UTF-8, BOM, lone surrogate escapes in JSON/embed/format, number forms, big integers, duplicate keys): all equal to Yjs |
| Zero-length structs | `probe` `zero` | GC, Skip, ContentDeleted, ContentString, ContentAny, ContentJSON of length 0: all rejected |
| Hostile sizes and timings | `js/hostile-gen.mjs`, `probe/Program.cs`, `probe/Perf.cs` | findings below |
| Plenum client | `dotnet test --project tests/Plenum.Blazor.Tests -p:YcsPath=...` (artifacts outside the repo) | 181 of 181 |

One trap for whoever repeats the differential: Yjs' `insert` adds keys to the attributes object it is given,
so a generator that reuses attribute objects records operations Yjs never ran (my first run "found" 19
divergences that way; `POLLUTE=1` reproduces it).

## Findings

Severity classes as asked. "Regression" means introduced by `40d7fdb..8d40781`.

### Wrong document

None found.

### Crash or unbounded resource

**C1. Pending data is bounded in compressed bytes, and re-read without any struct limit.**
`src/Ycs/Utils/StructStore.cs:70` (`PendingBytes`), `:377` (the check), `:421-428` (the retry reads the
merged pending update with `maxStructs: long.MaxValue`), `UpdateOperations.cs:419` (`MergeWithPending`).
What waits is kept as a v2 update, whose run-length columns hold millions of structs in a few bytes, and
`MaxPendingBytes` counts those bytes. Evidence (`probe retry 6`, document with `MaxPendingBytes = 64 KiB`):
six updates of 25 bytes, each 2,097,152 GC structs waiting for clock 0 of their client, are all accepted;
then

- `EncodeStateAsUpdate()` (what every joining peer is sent) returns **25 MiB**, takes 1.4 s and allocates
  2.35 GiB, on every call;
- one 19-byte update that satisfies one of them makes the retry decode all six at once: **880 MiB allocated,
  847 MiB live**. K such updates cost K times that; nothing bounds K.

The same works over v1 at 4 bytes per struct (a 1 MiB v1 update of deleted items is about 30 bytes once
pending), so `MaxStructsPerUpdate` does not help. Fix: count pending **structs** (the plan knows how many it
sets aside) against a limit of its own, default `MaxStructsPerUpdate`, and decode the retry under it; or
count pending bytes in their v1 size. Regression in so far as F3/F12 made pending both unlimited by default
and part of every `EncodeStateAsUpdate`.

**C2. The default `MaxStructsPerUpdate` of 2,097,152 is not a sane default; its documented cost is wrong.**
`src/Ycs/Utils/Limits.cs:35`, `YDoc.cs:55-56`, `docs/api.md:115-116` ("about 110 bytes each ... about
230 MiB"). Measured for v2 updates of 25 to 30 bytes (`probe gcbomb | deleted | types`):

| 2,097,152 structs of | allocated | stays in the document |
| --- | --- | --- |
| GC, pending | 128 MiB (61 B each) | 25 bytes pending, see C1 |
| deleted items, pending | 416 MiB (198 B) | 30 bytes pending |
| deleted items, integrated | 1,312 MiB | **553 MiB**, for good (items without origins never merge and are never collected); its v1 state is 8.4 MiB for every peer |
| shared types (`YText`, measured at 200,000) | about 2.3 GiB (1.1 KB) | about 1.2 GiB (520 to 615 B) |

With a 192 MiB heap (`DOTNET_GCHeapHardLimit=0xC000000`) the 30-byte update ends in
`OutOfMemoryException` out of `ApplyUpdateV2`, not `YUpdateException`. A browser client is killed by one
message. Before a0e0904 the same update was capped at 5,888 structs (64 per byte + 4,096), so this is a
regression in hardening, traded for accepting Yjs' "20,000 deleted items in 42 bytes". Fix: a default in
the range of 2^16 to 2^17 (131,072 covers that case six times over, worst case about 150 MiB), documented
with the measured per-struct costs, and applications that expect larger v2 states raise it knowingly.
`Limits.MaxStructsV2` (`max(limit, bytes)`) also means the limit cannot be set below one struct per byte.

**C3. v1 has no struct limit at all.** `src/Ycs/Utils/UpdateDecoderV1.cs:42` (`MaxStructs => long.MaxValue`).
"Bounded by its size" is true but the factor is about 270: 200,000 `YText` items are 800,008 bytes of v1 and
allocate 219 MiB, 121 MiB retained (`probe typesv1`); an 8 MiB update is about 2.2 GiB. An application
that takes v1 only (Plenum) has no knob. Fix: apply `MaxStructsPerUpdate` to both formats.

**C4. The conflict loop is quadratic and now reachable with tiny updates.** `src/Ycs/Structs/Item.cs:286-320`
(two `HashSet`s per integrated item, `itemsBeforeOrigin` grows with every conflicting item scanned). n items
without origins of client 9 after n of client 5 in the same parent (`probe conflict`): n = 10,000: 6.3 s and
6.4 GiB allocated; 20,000: 23 s, 26.7 GiB; 40,000: **97 s, 111 GiB**, each from two v2 updates of **28 bytes**
(v1: 160 KB). At the default limit one update allows about 10^12 steps. Yjs is quadratic too (10 s and 48 s
for 10,000 and 20,000), so documents agree; but the old cap held this to 5,888 structs for a small update.
Fix: C2 and C3 bound it per update; inside the loop, `itemsBeforeOrigin` is only read in the branch for an
item with another origin, so it can be filled lazily (or replaced by a position test), which removes the
allocation and most of the time for the hostile shape. A time or step budget per `ApplyUpdate` would be the
only complete answer.

**C5. `MaxPendingBytes` is not checked when an update triggers a retry.** `StructStore.cs:377`
(`if (!retry && ...)`) and `:428` (the nested apply gets `long.MaxValue`). An update that satisfies a small
pending dependency and brings a large orphan part is kept whole (`probe bypass`: limit 10,000 bytes, pending
afterwards 100,024 bytes). It does not chain (the next orphan is refused while over the limit), so the
excess is one message; but every later honest out-of-order update is then refused too. Fix: on a retry
require the update's own rest to fit under the limit (bounds pending to twice the limit), or plan the retry
before executing.

**C6. `Transaction.Changed.Remove` is O(n): changing and deleting many types in one transaction is
quadratic. Regression (0701f70).** `src/Ycs/Utils/Transaction.cs:67` (`OrderedDictionary`),
`src/Ycs/Structs/ContentType.cs:71`. A transaction that sets a key in each of n nested maps and deletes them
(`probe perf changed`): n = 20,000: 1.6 s; 40,000: 6.8 s; 80,000: **30 s**, locally and as a remote update
of 863 KB from a peer without gc. It was O(1) per type with the plain dictionary. Fix: keep the order
without removing (mark the entry, e.g. a null set, and skip it in the observer loop and in
`BuildSkippedEvents`), or a dictionary plus an ordered list with tombstones.

**C7. `CleanupAfterTransaction` keeps the texts to clean in a list and searches it per item. Regression
(the 13.6 cleanup port).** `src/Ycs/Types/YText.cs:1421`, `:1452-1455` (Yjs uses a `Set`). A remote update
that formats one character in each of n texts (`probe perf fmt`): 10,000: 0.33 s; 40,000: 3.2 s; 80,000
(2.2 MB): **11.4 s**. Fix: `HashSet<YText>`.

**C8. A subdocument costs about 1.57 KB per input byte.** `src/Ycs/Structs/ContentDoc.cs:101-117`: every
`ContentDoc` read builds a whole `YDoc` (options with a fresh GUID from the CSPRNG, store, dictionaries)
before the update is known to be valid. 100,000 subdocuments are 100,025 bytes of v2 and allocate 157 MiB
(`probe subdocs`); 1 MiB is 1.6 GiB. Fix: create the document lazily (on first access of `Doc`, or at
integration), and do not generate a GUID that is overwritten. A subdocument also gets default limits, not
its parent's (`CreateDocFromOpts` builds new options): a hardened parent has unhardened children (by
reading).

**C9. `UpdateOperations` on a v2 update expands it without bound in the output.** With the default limit a
30-byte update converts to 8.4 MiB of v1, `DecodeUpdateV2` holds 447 MiB and `DescribeUpdateV2` builds a
string of 175 million characters (1.1 GiB) (`probe ops`). Merging, diffing and the state vector stream and
stay small. Follows from C2; mention it in the documentation of those three.

### Wrong event or bytes

**E1. A subdocument option that is `undefined` reads as `null`.** `ContentDoc.cs:131` (`gc`), the same for
`autoLoad`, `shouldLoad`, `meta`. For options `{ gc: undefined }` Yjs' destructuring default gives
`gc = true` and re-encodes `{}`; ycs gives `Gc = false` and re-encodes `{ gc: false }` (`js/gcundef.mjs`:
Yjs state `...0373756276 00 00`, ycs `...0373756276 01 02 6763 79 00`). 31 of 4,000 option values. Only an
odd or hostile peer writes that. Fix: read the options with a marker for `undefined` (as `ContentJson` now
does) and treat it as absent.

**E2. ycs rejects updates Yjs applies: a `guid` or `collectionid` that is not a string.**
`ContentDoc.cs:129`, `:138`. 774 and 611 of 4,000 hostile option values. Yjs keeps such a document (and for
some values can no longer encode its own state, or draws a random guid for `guid: undefined`), so rejecting
is defensible; `collectionid` is never encoded and could simply be ignored when it is not a string, which
would keep ycs and Yjs peers on the same document. Say so in `docs/api.md` (the row says "reading lets
every key override").

**E3. Events built late change the order of `ChangedParentTypes` and of deep observer calls.**
`src/Ycs/Utils/Transaction.cs:285-350`, `:471`. When an observer attaches a document listener or a deep
observer during the cleanup, the skipped types are appended: text changed before map gives Yjs
`t,inner,a,m`, ycs `m,t,inner,a` (`js/late.mjs`, `late-yjs.txt` against `late-ycs.txt`; the other seven
scenarios are equal). `SortEventsByOrderOfChange` re-orders the event lists but not the dictionary, whose
order decides the order of deep observer calls. Rare. Fix: rebuild `_changedParentTypes` in the order of
`_changed` there, and do not rely on `Dictionary` enumeration order for it (see M5).

**E4. A delta cannot express an embed whose value is `null`.** `src/Ycs/Utils/YEvent.cs:20-25` (`Delta`),
`YText.cs:153`. Yjs gives `{ insert: null }` (confirmed, case "embed null"); ycs gives an operation with
`Insert`, `Retain` and `Delete` all null, which every consumer reads as nothing. Plenum's
`TextDiff.FromDelta` skips it and is one position off until `RemoveNonText` deletes the embed. Not new, but
the embed work of this round touched exactly this. Fix: a kind on `Delta`, or a marker object for a null
embed.

**E5. Key order of a retain's attributes.** `YText.cs:80` (`attributes` is a `Dictionary` that has keys
removed and added again): after a removal a new key takes the freed slot, so the enumeration order differs
from Yjs' object. Cosmetic (by reading; the traces compare sorted keys).

### Performance

**P1. Deleting backwards with an UndoManager (or without gc) copies the whole deleted run each time.**
`src/Ycs/Structs/ContentString.cs:113-124`: a single character merged in front of a long run builds a new
`StringBuilder` of twice the length. 40,000 single deletions from the end, UndoManager attached:
**1,852 ms and 3.1 GiB** allocated; Yjs 138 ms. The same shows in 40,000 undos of typing (2.2 s, Yjs 0.19 s).
Human backspacing is far below this, programmatic deletion is not. Fix: when the right content already has
a builder, insert the left text at its front and take the builder over.

**P2. A merged capture rebuilds both delete sets per transaction.** `src/Ycs/Utils/UndoManager.cs:408-409`.
40,000 edits at random places in one capture: 18.2 s and 16.3 GiB allocated; Yjs does the same and takes
11.1 s. Only with long captures (the default 500 ms keeps stack items small). Fix: insert the transaction's
ranges into the stack item's sorted set in place.

**P3. The deep-event sort recomputes `Path` in every comparison.** `Transaction.cs:42`, `YEvent.cs:110`
(`Path` walks the siblings and allocates a stack each time; Yjs caches `_path`). One transaction changing
16,000 maps of an array with a deep observer: 1,476 ms, Yjs 429 ms. Fix: compute the path length once per
event before the sort.

**P4. `ContentJson.GetContent()` scans the content on every call. Regression (3d0c9c0).**
`src/Ycs/Structs/ContentJson.cs:38-39`, reached per index from `YArray.Get` through
`AbstractType.ValueAt`. `Get(i)` over a legacy item of 80,000 values: **6.1 s** (`ToArray()` 11 ms). Fix: a
flag set when the item is read, and a `ValueAt` on `ContentJson`.

Measured and fine: keystrokes with an observer reading `Delta` on a text of 20,000 formatted runs (200 in
under 5 ms), 200 `Format` calls there, 8,000 sets, undos and redos of one map key (under 0.1 s each),
typing 40,000 characters into one stack item (39 ms), `StableSort`, the copy of the subdocument set in
`Destroy` (100,000 subdocuments: 0.33 s), `EncodeStateAsUpdate` with honest pending.

### Maintainability

**M1. Three inconsistent limits.** `YDocOptions.MaxStructsPerUpdate` is per document,
`UpdateOperations.MaxStructsPerUpdate` (`UpdateOperations.cs:86`) is a mutable process-wide static (two
tenants or two tests cannot differ), v1 has none, and the pending re-read ignores all of them. One limits
object passed to the document and as an optional parameter of the `UpdateOperations` functions would cover
C1 to C3.

**M2. `docs/api.md` is inaccurate in the section applications rely on.** Line 115-116: "about 110 bytes
each ... about 230 MiB" (measured 61 B to 1.1 KB, up to 2.3 GiB). Line 120-124: pending is "counted as
encoded bytes" and "a server ... sets it" reads as if that bounded memory (C1, C5). Line 126: "the
integration does not fail on input alone" should name `OutOfMemoryException`. The rest of the table agrees
with the code as far as I checked it (UndoManager, `YMap.Get`/`TryGet`, `YDocOptions`, events, `Push`,
`Unobserve`); the three API breaks are coherent with each other and Plenum's client compiles unchanged.

**M3. Comments that are no longer true.** `StructStore.cs:784` says the parent comes from the right origin
first; the code (and Yjs) take the left first since 389bbe4. `YDoc.cs:131-136` has two `<summary>` blocks on
`GenerateNewClientId`, the first about clock-seeded `Random`, which describes nothing in the code.

**M4. Duplicated between the three branches.** JavaScript's 2^53 - 1 is now defined six times, three of them
new (`Limits.cs:20`, `RelativePosition.cs:201`, `PermanentUserData.cs:26`; older: `YJson`, `AnyEncoding`,
`IntDiffOptRleDecoder`); the "integer in a JSON-like value" test exists in `RelativePosition.JsonInteger`
and `PermanentUserData.AddClientId`; JavaScript coercions live in `ContentDoc.Truthy`,
`YXmlFragment.JsString` and `YText.StrictEquals`. One internal `JsValue` helper would hold them.

**M5. Yjs' orders rest on undocumented enumeration orders.** `UndoManager.cs:490/530` (`HashSet<Item>`
iterated as Yjs' insertion-ordered `Set`), `Transaction._changedParentTypes` (`Dictionary`, decides the
deep observer order). Both hold while nothing is removed; use a list plus a set, or say so at the field.

**M6. The fuzz harness cannot see what this round changed.** It applies to an unobserved document with
default options, so no event is built and no limit is exercised, and its mutator never produced a
run-length bomb in 4.4 million inputs although its own 64 MiB budget would flag one. Add the hostile seeds
of `js/hostile-gen.mjs` as pinned cases, a time and allocation ceiling for accepted updates, and an
observed variant (`probe/Observed.cs` is a start).

## Not verified yet, by risk

1. **Resource ceilings for accepted updates**: no test bounds time or memory of an update that is valid
   (C1 to C8 were all found by hand). Under WebAssembly nothing was measured at all.
2. **Exceptions from observers** in each cleanup phase (walkthrough F18 b to d are open): whether `Update`
   still fires, the state of the document and of the UndoManager afterwards, transactions started by a
   handler that then throws. Checked in f0054e7: 17 scenarios against Yjs, 9 differed; Yjs' paths are
   ported, including the unfinished batch after a throwing `Update` handler.
3. **UndoManager outside the fuzz's shape**: two UndoManagers on one document, `Undo` inside an outer
   transaction (the `_outerTransaction` bookkeeping is port-only), `StackItem.Meta` across undo and redo,
   real capture timeouts (tests use 0 or infinite), an UndoManager created or destroyed inside an observer,
   a remote update that reuses the local client id while a stack exists.
4. **Subdocuments**: `Load`, `autoLoad` and `SubdocsChanged` on remote updates with gc, two items with one
   guid, a subdocument inside a deleted and undone parent beyond the two recorded cases, hostile options
   beyond the 4,000 of this round.
5. **Late observers**: attaching or removing handlers during a cleanup (one order difference found in
   eight scenarios, E3), events kept beyond their transaction, more nested transactions than the pool of 4.
6. **`ApplyDelta`** is in no differential trace (unit and parity tests only): retain with attributes beyond
   the end, `sanitize`, embedded types, pending `ApplyDelta` on a text outside a document. Checked in
   ec2ebd9 (all four in the property engine's language): one divergence, a pending `ApplyDelta` kept
   `sanitize` where Yjs drops it, fixed in f9836c4.
7. **Snapshots on documents with gc** (only crash-fuzzed, not compared with Yjs), `computeYChange` with
   embeds and types, XML snapshot reads, `RestoreDocument` byte parity for documents with pending data.
8. **Relative positions** outside a root text: arrays, XML, nested maps, `followUndoneDeletions` through
   redone chains, the JSON form (ycs keeps an empty `tname`, Yjs drops it).
9. **XML**: `QuerySelector`, the tree walker, hooks, attribute values that are not strings, `toString` of
   nested formatted `XmlText`.
10. **v2 strings**: a surrogate pair split between two strings of the string column (raw lone surrogates in
    JSON, keys and text), which the strict UTF-8 check does not see.
11. **`UpdateOperations` against Yjs** for updates with `Skip` structs, overlapping partial updates, client
    ids above 2^32, and a diff of pending data; `obfuscateUpdate` is not ported, awareness is not present.
12. **Legacy `ContentJSON`** beyond single values: splitting, merging, `undefined` inside arrays.
13. **`GcFilter`**, `Gc = false` documents that later collect, `MaxDepth` 256 against valid Yjs documents
    nested deeper (kept on purpose, never measured against real data). The depth part: 20dbcfd,
    `UpdateLimits.MaxDepth` of 3,000, and Yjs documents at the limit round-trip.

## Reproducing

```bash
cd /tmp/ycs-review-r2
node js/cross-gen.mjs cross.json 600 1 && dotnet cross/bin/Release/net11.0/cross.dll cross.json     # BIGID=1 on both for large client ids
node js/hostile-gen.mjs hostile && node js/types-gen.mjs
dotnet probe/bin/Release/net11.0/probe.dll <gcbomb|deleted|types|typesv1|retry 6|bypass|subdocs|conflict|ops|zero|strings|subdocopts|late>
dotnet probe/bin/Release/net11.0/probe.dll perf <changed|fmt|undotext|undomap|deep|json|fmtevent>
dotnet probe/bin/Release/net11.0/probe.dll <fobs|fsnap|frel|fpud> <iterations> <seed>
node js/conflict-time.mjs; node js/undo-time.mjs; node js/deep-time.mjs                              # the Yjs side of the timings
```
