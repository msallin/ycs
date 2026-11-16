# Plan: layout steps 8 and 9 in work packages

The packages that carry out steps 8 and 9 of [`design-item-layout.md`](design-item-layout.md), after step 7 as
[`plan-layout-6-7.md`](plan-layout-6-7.md) plans it (`ItemRef` is (slab, handle), content objects in `Refs`, map keys
in a second page set, `ItemRef.Adopt` the one entry from decoded to store, the 7.1 check after every test
transaction). Every package keeps the bytes identical and every suite green (unit, properties, corpus, fuzz, budgets,
`interop/run.sh`), and runs the 7.1 check plus its own after every test transaction, in Release and Debug, with leaves
of 2 and 4 and page 0 of the slab at one slot. *Full review* marks a package reviewed independently before it merges.

## Decisions taken with this plan (2026-11-16)

* **Garbage in the arenas** is collected content and copied-away runs only: a deleted but uncollected item keeps and
  encodes its content (gc off, kept items, snapshots). Design 2.3 is corrected accordingly.
* **D1, slack**: a tail table in the arena, keyed by a run's end offset, giving its reservation end; a split and an
  adjacent merge keep the end; compaction clears the table. No slot field.
* **D2, runs longer than a page** get a page of their own with 2× slack (the large object heap is accepted above 32 Ki
  characters), so typing or pushing 100k into one run stays amortised O(1).
* **D3, compaction** runs at the end of the outermost batch (beside `Slab.AllowReuse`), when dead > live and dead ≥ one
  largest page; never mid-batch outside the test modes.
* **D4**: a run copied away (a merge that cannot extend) has its old values nulled at once, so the weak-reference
  proof holds before any compaction.
* **D5**: folding `Keys` into `Refs` (8.11) is optional, built only if `map-set-distinct` held memory gains ≥ 3 %.
* **D6**: an internal `SlotKind` (with Skip and Free = `Slab.FreeKind`) mapped to the public `ItemKind`.
* **D7**: a detached view's hint becomes (staging, version) in step 9; after the staging is released or reused the view
  looks its id up in the store and throws if the struct is not there.
* **D8, staging**: one staging per `UpdateReader` lease (its re-entrancy rule), one per `UpdateOperations` call, never
  reset during the call; pages kept per thread up to about 256 KiB. Moving staging pages into an empty document's arena
  is deferred until measured.
* **Staging is not the slab** (design 2.5 says "the same slots"): a staged struct has no type-table index and no
  `AbstractType` yet, so its client, parent and payload mean other things (9.0).
* **The model's split is not ported**: `Countable` comes from the right part's kind, as Yjs takes it from the spliced
  content (`ContentDeleted` is not countable), not copied from `Info`.
* **A freed slot's `Payload`** holds the free-list link: `Slab`'s remark says so, and Debug asserts nothing reads a Free
  slot's content; `MergeItemWith` accounts for the right part's range before `Free`.

## Step 8: inline content and the arenas

Order: 8.0 ∥ 8.1; 8.2a ∥ 8.2b ∥ 8.2c; 8.9 then 8.10 (kinds without an arena); String: 8.3 (shadow), 8.4 (compaction),
8.5 (reads), 8.6 (objects removed); Any: 8.7 (shadow and reads), 8.8 (objects removed); 8.11 (optional), 8.12. Cold
kinds (Format, Embed, Binary, Json, Doc) stay objects in `Refs`. From 8.3 on the packages are sequential:
`ItemRef.Operations.cs` is their hub.

| pkg | concern | owns | invariant | proof / sabotage | risk |
| --- | --- | --- | --- | --- | --- |
| 8.0 | `Arena<T>` alone | new `Utils/Arena.cs`, `tests/Ycs.Tests/ArenaTests.cs` | page k holds min(256·2^k, 32 Ki) characters or min(64·2^k, 8 Ki) values; no run crosses a page; a run longer than a page gets its own page with slack (D2); appends at the write end or inside a run's tail reservation (D1); a dead counter; `Compact(relocate)` moves live runs off pages below half full and frees them; Debug poisons a freed page; `Violations()` | page sizes, oversized runs, two runs growing alternately copy O(log n) times (counted), the relocation map, tiny pages. Sabotage: tail reservations kept across a compaction | low |
| 8.1 | slot kind and content accessors on `ItemRef`, still from the objects | `ItemRef.cs`, `ItemRef.Operations.cs`, new `Utils/SlotKind.cs`, `StructStore.Checks.cs` | the kind in `Info` equals the `Refs` object's at every write (`New`, `Adopt`, split, gc, `ReplaceStruct`); `Countable` from the kind at `New` and split, kept at gc; accessors `Kind`, `Chars`, `ValueAt`/`Values`, `TypeOf`, `Cold`, `AppendContentTo`, `CopyContent`, `AppendChars`/`AppendValues` | kind check after every transaction; the right part of a split collected countable item is not countable. Sabotage: copy `Info` at the split | medium |
| 8.2a | text callers on the accessors | `YText.cs`, `YXmlText.cs` | outside `ItemRef*.cs`, `Content*.cs` and the decoded form nothing reads `ItemRef.Content` or tests a content class (IL test, whitelist shrinking to empty at 8.8/8.10) | every suite byte-identical; budgets equal or lower | low |
| 8.2b | array and map callers | `YArrayBase.cs`, `YArray.cs`, `YMap.cs`, `AbstractType.cs`, `AbstractType.Snapshot.cs`, `YXmlFragment.cs` | as 8.2a | as 8.2a | low |
| 8.2c | the other callers | `YEvent.cs`, `UndoManager.cs`, `PermanentUserData.cs`, `AbsolutePosition.cs`, `YDoc.cs`, `ItemView.cs`, the store-side reads in `StructStore.IntegrationPlan.cs` | as 8.2a | as 8.2a | low |
| 8.9 | Deleted inline (no object, length is the slot's) | `ItemRef*.cs`, `ContentDeleted.cs` (decoded form only), `DeleteSet.cs` | gc in place sets kind Deleted and keeps `Countable`; a split's right part is not countable | every suite, gc on and off. Sabotage: drop the kept bit | low-medium |
| 8.10 | Type inline (payload = type-table index) | `ItemRef*.cs`, `ContentType.cs` (store side to new `Structs/TypeContent.cs`), `TypeTable.cs`, `AbstractType.cs` | index given at `New`/`Adopt`, never reused; no Type slot names a collected entry; a collected type is a GC slot | H5, the 7.0e tests, nested-collect and hooks properties, undo redo of a nested type. Sabotage: index given at integration | medium, *full review* |
| 8.3 | char arena as a shadow of String content | `StructStore.cs` (owns the arenas), `ItemRef*.cs` | every live String slot's range equals its object's text; live ranges disjoint and inside a page's used part; dead count equals a recount; collected slot has no range; undo's `Copy` gets a fresh range; a split gives `payload + diff` with U+FFFD in place at a surrogate pair; a merge extends when adjacent, else copies with slack; run extension appends | check after every transaction, also with tiny pages and "always copy on merge". Sabotages: no in-place U+FFFD; `Copy` shares the range; a merge extends when not adjacent | high, *full review* |
| 8.4 | compaction | `Arena.cs`, new `StructStore.Compaction.cs`, `Transaction.cs` | only slots and the tail table hold offsets; compaction skips Free and pending slots, keeps deleted-uncollected runs, drops tails; runs at the outermost batch end (D3) | forced compaction after every transaction and mid-batch (each `AddStruct`, each local insert, between gc and merge); Debug poison; v1/v2 state equal before and after; gc-off, snapshot and undo suites in this mode. Sabotages: skip one slot; keep the tails; treat `Deleted` as dead | high, *full review* |
| 8.5 | String reads from the arena (objects a compared mirror) | `ItemRef*.cs` | as 8.3 | every suite, forced compaction modes | medium |
| 8.6 | String objects removed from the store | `ItemRef*.cs` (`Adopt` converts decoded strings) | no `ContentString` allocated for a store item | budgets lowered (`text.insert-char-at-caret`, `undo.*`); corpus, soak | medium |
| 8.7 | value arena for Any: shadow and reads | `StructStore.cs`, `ItemRef*.cs` | as 8.3; values nulled at gc, at `ReplaceStruct` to GC, and when a run is copied away (D4) | weak references: a value is collectible after delete+gc, after a copying merge then delete+gc, after a nested type's collection, all before any compaction. Sabotage: no nulling of a copied-away range | high, *full review* |
| 8.8 | Any objects removed | `ItemRef*.cs`, `Adopt` | no `ContentAny` for a store item | `array.push-100k`, `map.set` budgets lowered | medium |
| 8.11 | fold `Keys` into `Refs` (optional, D5) | `Slab.cs`, `ItemRef.cs` | | `map-set-distinct` held memory | low |
| 8.12 | performance, measurements, docs | `docs/*`, `bench/Ycs.Compare/README.md`, `Budgets.cs` | | `ab.py` | low |

## Step 9: decoding into staging tables

Order: 9.0 ∥ 9.1 (after 8.0); 9.2 (staging as a shadow of the objects); 9.3 (update operations); 9.4 (integration plan;
after 8.2c and 9.3); 9.5 (adoption; after 8.8 and 8.10); 9.6; 9.7.

| pkg | concern | owns | invariant | proof / sabotage | risk |
| --- | --- | --- | --- | --- | --- |
| 9.0 | `StagingTable` alone | new `Utils/StagingTable.cs`, tests | per struct: id (client index into its own list, clock), length, kind (Skip, GC), origins, parent as root name or id, `parentSub`, info; characters and values in its own arenas; a Type stages as its type ref and name, no `AbstractType`; cold objects in its `Refs`; each client's structs a contiguous clock-ordered range; `Reset` keeps pages up to a cap and bumps a version; Debug poisons on `Reset` | unit tests | low |
| 9.1 | strings read as spans | `IUpdateDecoder.cs`, `UpdateDecoderV1.cs`/`V2.cs`, `lib0/Lib0Reader.cs`, `lib0/Decoding/StringDecoder.cs` | v1 decodes strict UTF-8 into the span, v2 slices the string column; characters and exceptions as `ReadString`'s | differential on fuzzed input; invalid UTF-8 still `YUpdateException` | low |
| 9.2 | the struct cursor stages, shadowing the objects | `EncodingUtils.cs` (`StructCursor`/`ReadStruct`), `LazyStructReader.cs`, `UpdateReader.cs`, `DecodedStruct.cs` | `DecodedStruct` from staging equals it from the objects for every struct; limits unchanged; a lease owns its staging, a re-entrant read gets its own | corpus; fuzzer (same exception at the same struct). Sabotage: shared staging on re-entrancy fails the hooks properties | medium |
| 9.3 | `UpdateOperations` on staging, one writer from staged fields | `UpdateOperations.cs`, `LazyStructWriter.cs` | `MergeUpdates` slices like `splice` (U+FFFD at a pair); `DiffUpdate`'s offset write slices without replacement, as Yjs' `write(encoder, offset)`; `DecodeUpdate` builds records from staging | bytes equal on corpus and update-operation fuzz; differentials against Yjs. Sabotages: a Skip read as GC in a merge; the replacement in the wrong path | medium, *full review* |
| 9.4 | the integration plan on staging | `StructStore.IntegrationPlan.cs`, `StructStore.Pending.cs` | `PlanItem` is a store handle or a staged index; `ClientStructRefs` are index ranges; the rest is staged ranges encoded before the lease is released; kind tests read the staged kind | hardening, conflict, pending tests, `interop/js/pending-orders`, fuzzer, pending encodings byte-equal. Sabotage: encode the rest after the release (poison) | high, *full review* |
| 9.5 | adoption from staging | `ItemRef.cs` (`Adopt(staging, i, offset)`), `Item.cs` (`ResolveReferences` moves), `ItemView.cs`, the types' `Read` | characters and values copied from the offset, U+FFFD first when the one before the offset is a high surrogate; `AbstractType` created at adoption; cold objects move; detached views get the new hint (D7) | corpus byte for byte, `walk-index`, soak, GC-filter tests. Sabotage: adopt at an offset without the replacement. Still to build here: the decoder-to-staging read path (a Type's type ref and name read directly, no objects), and presizing the table from the struct count (9.2's shadow is filled from objects and gated by `UpdateReader.StageForTests`) | high, *full review* |
| 9.6 | decoded objects removed | `Item.cs`, `GC.cs`, `Skip.cs`, decoded paths of `Content{String,Any,Deleted,Type}.cs`, `UpdateReader.cs`, the `ItemRefSignatureTests` whitelist | a load allocates no `Item`, `GC`, `Skip` or hot content object | `COMPARE_ALLOC_TYPES=1` on the load workloads; the remote-update and pending budgets tightened | medium |
| 9.7 | measurements and docs | `docs/*`, `bench/Ycs.Compare/README.md`, `Budgets.cs` | | | low |

## Where corruption can hide

Debug checks first (8.0, 8.1, extended in 8.3): `Arena.Violations()`; a content check after every transaction (kind
matches; ranges inside a live page's used part, crossing none; live ranges of an arena pairwise disjoint; dead count
equals a recount; no range on a collected slot); test switches (tiny arena pages, always copy on merge, forced
compaction per transaction and mid-batch); Debug poison of freed arena pages and of staging on `Reset`; an encoding
compared before and after compaction, kept after 8.6/8.8 when no object remains to compare with.

* Arena offsets across compaction: Free and pending slots (`Payload` holds the free-list link); deleted-uncollected
  runs stay; tails dropped; page 0 and the last page walked; no local holds an offset or span across a compaction
  point (`YText.WriteTo` hands spans to the caller's writer: Debug poisoning exposes one kept across user code).
* A split's slice: `payload + diff` for String and Any only (Json splits as a cold object); the right part's
  `Countable` from its kind; `Marker` cleared.
* Surrogate pairs at a split: in-place U+FFFD is safe only while ranges are disjoint; the same rule for
  integrate-at-offset and `SliceStruct`, not for an offset write; `CanExtendRun`'s `EndsWithHighSurrogate` reads the
  arena; a step-6 view at an offset inside a pair reads a lone low surrogate where Yjs' split gives U+FFFD.
* Format and embed: cold kinds between String runs; the text walks and the run-head index's live-format flag test the
  kind.
* Undo's redone content: `UndoManager`'s `Copy()` gets a fresh range.
* Snapshots and gc off: deleted items' text is read and encoded.
* Staging: re-entrant leases, reads after the release, staged types without an `AbstractType`.

Design corrections owed (8.12, 9.7): design 2.3's garbage rule; the file lists of steps 8 and 9 in design section 4
(step 8 also `ItemRef.Adopt`, `UndoManager.cs`, `YEvent.cs`, `PermanentUserData.cs`, `YXmlFragment.cs`, `YMap.cs`,
`YArray.cs`, `AbstractType*.cs`, `AbsolutePosition.cs`, `YDoc.cs`, `StructStore.IntegrationPlan.cs`, `ContentType.cs`,
`ItemView.cs`, `Transaction.cs`, `TypeTable.cs`, `Slab.cs`; step 9 also `EncodingUtils.cs`, `LazyStructWriter.cs`,
`StructStore.Pending.cs`, `Lib0Reader.cs`, `StringDecoder.cs`, `ItemView.cs`, `DecodedStruct.cs`, and `Item.cs`/`GC.cs`/
`Skip.cs` for the removal); the run extension writes to content objects (`YText.cs`, `YArrayBase.cs`): 8.2a/8.2b route
it through `AppendChars`/`AppendValues`.
