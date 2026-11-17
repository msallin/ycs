# Plan: layout steps 8 and 9 in work packages

The packages that carry out steps 8 and 9 of [`design-item-layout.md`](design-item-layout.md), after step 7 as
[`plan-layout-6-7.md`](plan-layout-6-7.md) built it (`ItemRef` is (slab, handle), content objects in `Refs`, map keys
in `Keys`, `ItemRef.Adopt` the one entry from decoded to store, the 7.1 check after every test transaction). Every package
keeps the bytes identical and every suite green (unit, properties, corpus, fuzz, budgets, `interop/run.sh`), and runs the
7.1 check plus its own after every test transaction, in Release and Debug, with leaves of 2 and 4 and page 0 of the slab
at one slot. *Full review* marks a package reviewed independently before it merges; the others the coordinator reads as a
diff at the merge.

File ownership: two packages that run at once own no file in common, with one exception: `tests/Ycs.Budgets/Budgets.cs`
is shared by rows (a package changes only the rows of the scenarios it changes, with a comment naming the package; the
coordinator merges the rows).

## Decisions

* **Garbage in the arenas** is collected content and copied-away runs only: a deleted but uncollected item keeps and
  encodes its content (gc off, kept items, snapshots). Design 2.3 is to be corrected accordingly (8.12).
* **D1, slack**: a tail table in the arena, keyed by a run's end offset, giving its reservation end; a split and an
  adjacent merge keep the end; compaction clears the table. No slot field.
* **D2, runs longer than a page** get a page of their own with as much room again (the large object heap is accepted
  above 32 Ki characters), so typing or pushing 100k into one run stays amortised O(1). Its price is allocation: a run
  of n characters copies about 2n characters on its way, so `text.type-100k-in-one-transaction` allocates about 530 KB
  after 8.6 against the 218 KB of the object layout (open question 1). Held memory is what the layout is for; accepted.
* **D3, compaction** runs at the end of the outermost batch (beside `Slab.AllowReuse`), when dead > live and the garbage made since the
  last compaction is ≥ one largest page and ≥ the slot count (the walk costs O(slots); each compaction is paid for by
  that much new garbage, whatever it freed); never mid-batch outside the test modes.
* **D4**: a run copied away (a merge that cannot extend) has its old values nulled at once, so the weak-reference
  proof holds before any compaction.
* **D5, `Keys` into `Refs` (8.11): not built.** Measured after 8.8 on `map-set-distinct` (100,000 keys): 24.9 MiB held,
  the `Keys` page about 0.77 MiB (3.1 %). A map item of a hot kind holds no `Refs` entry since 8.8, so folding the key
  into `Refs` turns one page of 8 bytes per slot into another of the same size and saves nothing; a cold map item would
  need a wrapper object for content and key. Revisit only if a workload mixing hot and cold map values shows a saving.
* **D6**: an internal `SlotKind` (with Skip and Free = `Slab.FreeKind`) mapped to the public `ItemKind`.
* **D7**: a detached view's hint is (staging table, index, version) from 9.4b; after the staging is released or reused
  the view looks its id up in the store and throws if the struct is not there.
* **D8, staging**: one staging per `UpdateReader` lease (its re-entrancy rule). An `UpdateOperations` call rents tables
  from a per-thread pool (`StagingTable.Rent`/`Return`; the tables kept hold about 256 KiB between them): a merge of k
  updates uses k+1 (a reader per update, each holding its current struct only, `Truncate`d before every read, and the
  pending struct, which takes the reader's table when the struct moves whole and is `Truncate`d and copied into when it is
  cut). A streaming table forgets the clients it streamed past beyond 64. `DecodeUpdate` owns a table of its own. Moving
  staging pages into an empty document's arena is deferred until measured.
* **Staging is not the slab** (design 2.5 says "the same slots"): a staged struct has no type-table index and no
  `YType` yet, so its client, parent and payload mean other things (9.0).
* **The model's split is not ported**: `Countable` comes from the right part's kind, as Yjs takes it from the spliced
  content (`ContentDeleted` is not countable), not copied from `Info`.
* **A freed slot's `Payload`** holds the free-list link: `Slab`'s remark says so; `MergeItemWith` accounts for the right
  part's range before `Free` (`FreeSlotKeepingChars`).
* **D9, the hub's content hooks.** `ItemRef.Operations.cs` (integrate, split, merge, free, gc) touches content only
  through kind-dispatching hooks in `ItemRef.Content.cs`, one per event (store at `New`, cut at an offset, split, merge,
  release, write, copy); each kind's work is in its own partial (`ItemRef.Chars.cs`, `ItemRef.Values.cs`, the cold kinds
  through the object). So the String and the Any lane each own one partial and neither owns the hub (8.m).
* **D10, a run grows at its page's fill mark.** The elements of a page past `Used` belong to nobody, so a run that ends
  at `Used` extends in place on any page, not only the write page, and a page of its own (D2) starts with `Used` at the
  run's end and no tail entry (8.3a). `Extend` tests the fill mark before the tail table.
* **D11, the budgets while two forms coexist.** A package that shadows (8.3 did; 9.4a does for the reader) raises the
  budget rows it makes heavier, with a comment naming the package that lowers them again (8.6, 8.8, 9.6).

## Done

Step 8: 8.0 (`Arena<T>`), 8.1 (slot kind and the content accessors), 8.2a/b/c (every caller on the accessors), 8.9
(Deleted inline), 8.10 (Type inline, `Payload` the type-table index). Step 9: 9.0 (`StagingTable`), 9.1 (strings read as
spans), 9.2 (`StructCursor.NextStaged`; `UpdateReader` always stages), 9.3 (`UpdateOperations` on
staging, `DecodeUpdate` from staging), 9.4a (the integration plan on staging; the planned raise of a remote-update
budget row (D11) was not needed). **8.3** (the char arena as a shadow of String content, `Slab.Chars`,
`ItemRef.Chars.cs`, `CharsViolation` after every test transaction, the switches tiny pages and `CopyOnMergeForTests`) is
built on `layout-8-3` and under review. Its reviewer may note two performance points, both for 8.3a, neither a
correctness issue: `Arena.Extend` looks the tail table up before it tests the write end, and a page of its own carries a
tail entry, so typing into a run longer than a page costs two or three dictionary operations a keystroke.

## Step 8: inline content and the arenas

| pkg | concern | owns | invariant | proof / sabotage | risk |
| --- | --- | --- | --- | --- | --- |
| 8.3a | the arena's growth path (D10) | `Utils/Arena.cs`, `tests/Ycs.Tests/ArenaTests.cs` | a run that ends at its page's `Used` extends in place whatever the page; a run longer than `max` gets a page of twice its length with `Used` at its end and no tail entry; `Extend` tests the fill mark before the tail table; `Live`, `Dead` and the tail table recount as before (`Violations`) | extension at the fill mark of a non-write page; an own page extends without a tail and copies to a new own page when full; two runs growing alternately still copy O(log n) times (counted); `text.type-100k-in-one-transaction`'s time. Sabotages: the fill-mark test alone (a run with a reservation must still extend into it, not copy); an own page's run extended past its array | low |
| 8.m | the hub's content hooks and the partial files: a pure move (D9) | `Structs/ItemRef.Operations.cs`, `ItemRef.Content.cs`, `ItemRef.Chars.cs`, `ItemRef.Content.Collections.cs`, `ItemRef.Content.Text.cs`, new `ItemRef.Adopt.cs` (`Adopt`, `ResolveReferences`), `ItemRef.Write.cs` (`Write`, `WriteItem`), `ItemRef.Runs.cs` (`CanExtendRun`, `ExtendRun`), `ItemRef.Values.cs` (`ValueAt`, `Values`, `AppendValues`, `CanExtendAnyRun`, the Any hooks on the object); `Utils/SlotKind.cs` (`IsMergeable`, `HasObject`); `Types/YText.cs` (one line: `CanExtendRun` takes `SlotKind.String`) | the hub reads no `Content` and tests no content class: `NewItem` fills the slot from a kind and a length and calls `StoreContent`; `IntegrateItem`, `SplitItem`, `MergeItemWith`, `FreeSlot`, `BecomeGC`, `BecomeDeletedContent` call `CutContentHead`, `SplitContent`, `MergeContent`, `ReleaseContent`; `CanMergeItemWith` asks `Kind.IsMergeable()`; `CanExtendRun` takes a `SlotKind` and compares it with `Kind` (equal to the object's class by 8.1's invariant); the String accessors (`Chars`, `AppendChars`, `CopyCharsTo`, `WriteCharsTo`) live in `Chars.cs`, the Any ones in `Values.cs`; the String hooks of 8.3 become the String cases of the dispatchers, the object path the Any cases and the default. Behaviour unchanged: every suite byte-identical, the budgets equal | the IL tests (`ContentAccessArchitectureTests`, `ItemRefAdoptionTests`, the box test) with the new partials; `CharArenaShadowTests`. Read as a diff of moves | low |
| 8.5 | String reads from the arena (the object a compared mirror) | `ItemRef.Chars.cs`, `ItemRef.Write.cs`, `ItemRef.Runs.cs`; `tests/Ycs.Tests/CharArenaShadowTests.cs`, `StringSpanTests.cs` | `Chars`, `CopyCharsTo`, `WriteCharsTo`, `AppendContentTo`/`Elements` of a String, `WriteItem`'s String branch and `CanExtendRun`'s surrogate test read `Slab.Chars.Slice(payload, length)`; `WriteCharsTo` asks the writer for its span *first* and slices the arena after (user code in `GetSpan` may run a batch, and a batch end may compact: the slice is taken after it, from the item re-validated by `YText.WriteTo`'s holder rule); `CharsViolation` still compares the range with the object | every suite, tiny pages, `CopyOnMergeForTests`; a writer whose `GetSpan` edits the text. Sabotage: slice before `GetSpan` with a compaction forced in the writer (8.4's switch; until 8.4 merges, the Debug poison of a released page is the detector) | medium |
| 8.4 | compaction of the char arena (D3) | `Utils/Arena.cs` (`Compact` carries the caller's tag per live range: the slot handle), new `Utils/StructStore.Compaction.cs`, `Utils/Transaction.cs` (the trigger beside `AllowReuse`), `Utils/StructStore.ForTests.cs` (the modes), `tests/Ycs.Tests/StoreChecks.cs`, `tests/Ycs.Corpus/StoreChecks.cs`, new `CompactionTests.cs` | only live String slots hold offsets: compaction walks the clients' lists (a freed slot released its range; a pending slot is not in a list), keeps deleted-uncollected runs, drops the tail table, writes each moved run's new offset into its slot; it runs at the outermost batch end when dead > live and the garbage made since the last compaction is ≥ the largest page and ≥ the slot count; the v1 and v2 state and `ToString` of every text are equal before and after | forced compaction after every transaction and mid-batch (after each `AddStruct`, each local insert, between gc and merge) in the unit, corpus, property, gc-off, snapshot and undo suites, with tiny pages; Debug poison of freed pages; `CharsViolation` after each. Sabotages: skip one slot; keep the tails; count `Deleted` as dead; compact inside a nested transaction | high, *full review* |
| 8.6 | String objects removed from the store | `ItemRef.Chars.cs` (`New(..., ReadOnlySpan<char>)`, `StoreChars`), `ItemRef.Content.cs` (`StoreContent` without a `ContentString` case; `NewCopyOf` for the undo manager's redo), `Utils/SlotKind.cs` (`HasObject`: String false), `Structs/ContentString.cs` (the decoded form only: the string and `Read`/`Write`), `Utils/StructStore.Checks.cs` (the mirror comparison goes; a String slot holds no object), `Types/YText.cs` (the two `new ContentString` call sites), `Utils/UndoManager.cs` (`NewCopyOf`); `CharArenaShadowTests` → `CharArenaTests`; `ContentStringTests`; budget rows | no `ContentString` is allocated for a store item: a local insert copies the caller's string into the arena, a redo copies the item's range, `Adopt` copies the decoded string (until 9.5); the arena is the one source of a String's text | budgets lowered: `text.type-100k-in-one-transaction` (about 530 KB, D2), `text.insert-char-at-caret-100k`, `undo.*`, `text.to-string-after-edit-100k`; the IL test's allow-list shrinks; corpus, soak, `COMPARE_ALLOC_TYPES=1` shows no `ContentString` on the editing workloads | medium |
| 8.7 | value arena for Any: shadow and reads | `ItemRef.Values.cs`, `ItemRef.Content.Collections.cs`, `ItemRef.Write.cs` (the Any branch), `Utils/Slab.cs` (`Values`), `Utils/StructStore.Compaction.cs` (the second arena; a moved range's old values cleared), new `Utils/StructStore.Checks.Values.cs`, `Utils/StructStore.ForTests.cs` (one hook line), `Structs/ContentAny.cs` (a span of its list); new `ValueArenaShadowTests.cs`; budget rows | every live Any slot's range equals its object's values; the 8.3 invariants for values (disjoint, inside a used part, dead recount, no range on a collected slot, a split gives payload + diff, a merge extends or copies with slack); a range's values are nulled (`Arena.Clear`) at gc, at `ReplaceWithGC`, at `BecomeGC`, when a run is copied away (D4) and when a never-integrated item's slot is freed (`Adopt`'s failure path); `ValueAt` applies `ValueFreezer.OnRead` as today | check after every transaction with tiny pages and copy-on-merge; weak references: a value is collectible after delete+gc, after a copying merge then delete+gc, after a nested type's collection, after a failed adoption, all before any compaction; forced compaction modes. Sabotage: no nulling of a copied-away range; a compaction that moves a range without clearing the old one | high, *full review* |
| 8.8 | Any objects removed | `ItemRef.Values.cs` (`New(..., ReadOnlySpan<object>)`, `StoreValues`), `ItemRef.Content.cs` (`StoreContent`, `NewCopyOf`), `ItemRef.Content.Collections.cs`, `ItemRef.Content.Other.cs` (`Elements`), `Utils/SlotKind.cs`, `Structs/ContentAny.cs` (decoded only), `Utils/StructStore.Checks.Values.cs`, `Types/YArrayBase.cs` (`ContentAny.Own` → the span overload; `_spareValues` stays the insert's buffer), `Types/YType.Api.cs` (`ContentAny.Single`), `Types/YType.cs` (the poison hook's item); budget rows | no `ContentAny` for a store item; `Values`/`Elements` of an Any item is a read-only view over the arena range (one small object, as `ContentView` was), valid until the item changes | `array.push-100k-in-one-transaction`, `map.set-existing-key`, `array.delete-random-100k` lowered; `COMPARE_ALLOC_TYPES=1`; the IL allow-list shrinks to the cold kinds | medium |
| 8.13 | performance pass (below) | `ItemRef.Write.cs`, `ItemRef.Runs.cs`, `ItemRef.Operations.cs`, `Utils/Arena.cs`, `Utils/Slab.cs`, `Utils/DeleteSet.cs`, `Utils/EncodingUtils.cs`, `Utils/ClientStructs*.cs`, `Utils/Transaction.cs`, `Types/YText.cs`, `Types/YArrayBase.cs`; `bench/Ycs.Compare/README.md`, `results/`; budget rows | no semantic change: corpus byte for byte, every suite; each commit one hypothesis, kept when `ab.py` shows ≥ 3 % on its workload (or the code is simpler) | `ab.py 4 6` on a quiet machine (load under 1.5), 044bb73 against the branch; the budgets re-baselined | low |
| 8.12 | measurements and docs for steps 8 and 9 (9.7 merged into it) | `docs/*`, `bench/Ycs.Compare/README.md`, `Budgets.cs` | design 1.8/1.9 describe the code as it is; the corrections owed below are made; the held-memory table against 044bb73 | `ab.py`; `bench/Ycs.Layout small` rerun on the built layout | low |

8.11 is not built (D5). 8.9 and 8.10 are done.

## Step 9: decoding into staging tables

| pkg | concern | owns | invariant | proof / sabotage | risk |
| --- | --- | --- | --- | --- | --- |
| 9.4a | the integration plan on staging | `Utils/StructStore.IntegrationPlan.cs` (begins with a pure-move commit of the garbage-collection pre-check into new `StructStore.IntegrationPlan.GcCheck.cs`), `Utils/StructStore.Pending.cs`, `Utils/StructStore.cs` (`ApplyUpdate` takes the lease's table and delete set), `Utils/PlanItem.cs` (`PlanItem.Staged(table, i)`: equal when the same table and index, hash the index), `Utils/UpdateReader.cs` (the staging always filled; `Lease.Staging`), `Utils/StagingTable.cs` (a find by clock within a client's range), `Utils/LazyStructWriter.cs`; `PlanItemTests`, `IntegrationPlanReuseTests`, `PendingRetryTests`, `Hardening/*` | the plan's input is the table: `ClientStructRefs` is (client index, next) over the client's range, the stack and the steps hold indexes, `FindMissing` and the kind tests read the staged fields, `FindPlanned` searches the range by clock; `Rest` is per-client (start, count) ranges, encoded (`LazyStructWriter.WriteStruct`) and recorded in `PendingStructs._clocks` before the lease is released; `Missing` as today (yjs/yjs#824's early record kept); `Execute` and the pre-check still adopt and read the decoded object at the same (client, k), through one adapter `StructAt(i)` that 9.4c and 9.5 remove; the retry re-reads the pending update into a lease and plans its table | hardening, conflict, pending and retry tests, `interop/js/pending-orders`, the fuzzer; the rest encoded from staging byte-equal to `WriteRest` from the objects on the corpus (a test keeps both until 9.6); a remote-update budget row raised (D11). Sabotages: encode the rest after the release (the Debug poison shows); a Skip recorded in `_clocks`; a client of the rest keyed by its index instead of its id | high, *full review* |
| 9.4b | detached views on staging (D7) | `Utils/ItemView.cs`, `Utils/ItemView.ForTests.cs`, `Utils/PlanItem.cs` (`GcView`) | a detached view holds (table, index, version) and reads the staged fields (kind, length, origins, parent name or id, key, content, text) while the version matches; after it, it looks its id up in the store and throws `InvalidOperationException` when the struct is not there; `ItemView` no longer holds an `AbstractStruct` | `ItemViewTests`, `GarbageCollectionCheckTests`, the gc-filter properties; a filter that keeps the view and reads it after the update (in the store: the store's struct; set aside: throws). Sabotage: a view that reads a reset table | medium |
| 9.4c | the garbage-collection pre-check on staged items | `Utils/StructStore.IntegrationPlan.GcCheck.cs`; `Hardening/PlanItemKeysTests`, `GarbageCollectionCheck*Tests` | `CheckNesting`, `CheckGarbageCollection`, `MapValueSplits`, `DeletedTypes`, `LandsLast`, `LiveNonValues`, `Target`, `KeyOf` read `PlanItem.Staged` members (a staged item's "nested type" is its kind being Type; its content object is not read); `StructAt` is no longer called here | the gc-check tests and properties, the corpus with gc on; the fuzzer. Sabotage: a staged Type whose depth is taken from a created `YType` (there is none) | medium |
| 9.5 | adoption from staging | `Structs/ItemRef.Adopt.cs` (`Adopt(table, i, transaction, offset, integrate)`, `ResolveReferences` from the staged fields), `ItemRef.Chars.cs` (`StoreChars` from the offset), `ItemRef.Values.cs` (`StoreValues` from the offset), `ItemRef.Content.cs` (`StoreContent` of a staged cold object or type), `Structs/TypeContent.cs` and `ContentType.cs` (a type made from its reference and name: what `ContentType.Read` does today, at adoption), `Utils/StructStaging.cs`, `Utils/StructStore.IntegrationPlan.cs` (`Execute` from indexes; `StructAt` gone), `Utils/StructStore.cs`; `ItemRefAdoptionTests`, `AdoptFailureTests`, `StructStagingTests` | characters and values are copied from the offset, U+FFFD first when the character before the offset is a high surrogate; the `YType` is created at adoption, not at decode (its table index given then, as `New` gives it); a cold object moves from the table's refs to the slot's `Refs`; a subdocument's `YDoc` (made at decode by `ContentDoc.Read`) moves as a cold object; a GC struct is trimmed by the offset; a failed integration frees the slot and its ranges (`FreeSlot`) | corpus byte for byte, `walk-index`, soak, the GC-filter tests, nested types and XML names, subdocuments; the hooks properties (a re-entrant lease). Sabotage: adopt at an offset without the replacement; a type's index given at decode | high, *full review* |
| 9.6 | decoded objects removed | `Structs/Item.cs`, `GC.cs`, `Skip.cs`, `AbstractStruct.cs` (deleted), `Structs/ContentString.cs`, `ContentAny.cs`, `ContentDeleted.cs` (deleted; their `_ref` constants become `SlotKind` values where the store still names them), `ContentType.cs` (`Read` is the factory of 9.5), `Utils/EncodingUtils.cs` (`ReadStruct`, `StructCursor.Next`, `ReadClientStructRefs` on objects go; the reader uses `NextStaged`), `Utils/StructStaging.cs` (`Stage` goes), `Utils/UpdateReader.cs` (`Structs` goes), `Utils/StructStore.IntegrationPlan.cs` (the comparison test of 9.4a goes); `ItemRefSignatureTests` (its allow-list empties), `ContentAccessArchitectureTests` | a load allocates no `Item`, `GC`, `Skip` or hot content object; `DecodedStruct` and `PublicApi.txt` unchanged | `COMPARE_ALLOC_TYPES=1` on the load workloads; the remote-update and pending budgets lowered below their step-7 values | medium |
| 9.7 | merged into 8.12 | | | | |

## Order, and the lanes

Each package is sized for one agent in about an hour (8.m and 8.3a about half). The lanes share no file while they run;
a package waits for the ones its row's files were last changed by.

| slot | lane A: String | lane B: arena and compaction | lane C: staging | notes |
| --- | --- | --- | --- | --- |
| now (8.3 under review) | | | 9.4a | 9.4a's files are untouched by 8.3 |
| 1 (8.3 merged) | 8.m | 8.3a | 9.4a (continues) | 8.m and 8.3a are half-hour packages |
| 2 | 8.5 | 8.4 | 9.4b ∥ 9.4c | 8.4 after 8.3a (both own `Arena.cs`); 9.4b and 9.4c after 9.4a |
| 3 | 8.6 | 8.7 (after 8.4 and 8.5) | | 8.6 ∥ 8.7: the String lane owns `Chars.cs`, `Content.cs`, `Checks.cs`; the Any lane `Values.cs`, `Slab.cs`, `Compaction.cs`, `Checks.Values.cs` |
| 4 | 8.8 (after 8.6 and 8.7) | | | one agent may take 8.7 then 8.8 back to back |
| 5 | 8.13 (performance) | | 9.5 (after 8.8, 9.4b, 9.4c) | disjoint: 8.13 owns the hub, `Write.cs`, `Runs.cs`, `EncodingUtils.cs`; 9.5 owns `Adopt.cs`, `Content.cs`, `Chars.cs`, `Values.cs`, the plan |
| 6 | | | 9.6 (after 9.5 and 8.13: both own `EncodingUtils.cs`) | |
| 7 | 8.12 (docs and measurements) | | | |

The critical path is lane A: 8.m, 8.5, 8.6, 8.8, then 9.5, 9.6, 8.12, about seven agent-hours; lanes B and C fill the
slots beside it. Full reviews: 8.4 (slot 2), 8.7 (slot 3), 9.4a (slot 1), 9.5 (slot 5).

What the hub still needs after 8.m: `NewItem` (a kind and a length), `IntegrateItem`, `SplitItem`, `MergeItemWith`,
`CanMergeItemWith`, `FreeSlot`, `BecomeGC`, `Gc`, `BecomeDeletedContent`, `MarkDeleted`, `Delete`, `Next`/`Prev`,
`KeepItemAndParents`, `IsVisible`. 8.5 to 8.8 and 9.5 do not open it; 8.13 does, alone in its slot.

## 8.13: the performance pass

Step 7 measured, against the object layout (044bb73) on a loaded machine (load 2.2 to 3.0, so differences under about
10 % are noise): `text-append-1txn-observed-25k` +43 %, `encode-v1` +27 %, `map-set-same` +27 %, `text-append` +18 %,
`trace-replay` +17 %, `text-random` +10 %, `array-push-1txn` +9 %, `apply-updates` +8 %; the random-position array and map
workloads equal or faster; held memory 15 to 27 % lower (bench/Ycs.Compare/README.md, "Step 7 against the object layout").
The pass runs after 8.8, when a split and a merge of a String or an Any allocate nothing and the content object's
interface calls, casts and `GetType()` tests are gone from the hot paths, so that it measures the code that stays.

Method: build 044bb73 and the branch into separate folders; `ab.py 4 6` on a quiet machine (load under 1.5), first
without any change, to replace the loaded numbers; then one hypothesis per commit, measured alone on the workloads it
names, kept when it gains ≥ 3 % there or makes the code simpler; `DOTNET_JitDisasm` to count the bounds checks of a hot
method before and after (no sampling profiler runs on the machine: `perf_event_paranoid` is 4). The budgets are
re-baselined at the end; the README gets a "Step 8" section with the table and what each change did.

Hypotheses, from reading the hot paths (the numbers in parentheses are the extra cost per operation the step-7 table
implies, to be rechecked):

1. **`encode-v1`** (+1.3 ms over 10 encodings of the trace document). `ItemRef.WriteItem` reads the slot through about
   ten accessors, each an indexed read with two bounds checks; `LeftOrigin` and `RightOrigin` are built as `ID?` twice
   (the parent test, then the header), `Kind` twice, `Id`, `Length`, `ParentSub` and `Content` once each;
   `EncodingUtils.WriteStructs` reads `structs[i]` through the leaf directory for every i. Change: `WriteItem` takes one
   `ref readonly Slot` and writes the clients from the client table straight off it; `WriteStructs` walks the leaves
   (the enumerator) instead of indexing. After 8.5 a String is written from the arena span (no `ArrayPool` copy of a
   builder-held run).
2. **`map-set-same`** (+120 ns per transaction: a new slot, the old value deleted, collected and merged into the Deleted
   run before it, the slot freed and reused at the batch end). (a) `TryToMergeWithLefts` tests `CanMergeWith` and then
   `MergeWith` tests `CanMergeItemWith` again, and `IsSameKind` reads both slots once more: three full comparisons per
   merge; a merge that the pass has checked skips the test. (b) `MarkDeleted(Transaction)` reads `Parent`, `Countable`,
   `ParentSub` three times, `Id` twice and `Length` separately: one slot read. (c) `IntegrateItem`'s map path calls
   `parent.TryGetMapItem(parentSub)` up to three times (the conflict start, the right, the previous value). (d)
   `NewItem` and `SplitItem` call `SetKey(handle, null)` (`PageAt`, `CheckLive`) when there is no key; `Slab.Free` does
   `PageAt`, `CheckLive`, two object-page writes and `_redone?.Remove` for every freed slot. (e) `SplitItem` resolves
   `Parent` to the type and asks `TableIndex` back, and reads `ParentSub` as a string to `SetKey` it: copy the left
   slot's `Parent` index and key object instead.
3. **Typing** (`text-random`, `trace-replay`, `text-append`: a split, an integration, a run extension or a merge per
   keystroke). Re-measure after 8.6 first: a split no longer allocates (`ContentString.Splice` and its string concat at
   a surrogate pair are gone, `SetRef` is not called), and `CanExtendRun` tests the kind and the arena's last character
   instead of `Content?.GetType()`, `Content is ContentString` and the builder's last character. Then: (a) `CanExtendRun`
   reads `Parent`, `ParentSub`, the slot and the content in four reads: one; (b) 2(e) for the split; (c) the arena's
   fast path (8.3a: no dictionary operation per keystroke); (d) `IntegrateItem` reaches `transaction.Doc.Store` five
   times through three loads each; (e) the per-keystroke bookkeeping outside the slab (`NoteRunExtended` → `StructsOf`
   and `LastExtended`, `AddChangedTypeToTransaction`, `RecordRunExtension`, `GetState` by dictionary twice) is the same
   code as 044bb73's: compare by reading, not by guessing.
4. **Observed appends** (`text-append-1txn-observed-25k`: +560 ns per insert, more than slot reads explain; each insert
   makes an item, the batch end merges 25,000 items into one and the event's delta walks them). (a) 2(a) for the merge
   pass; (b) `Slab.Free` per merged item (2(d)); (c) `OnMerge` per merged item when the parent keeps search markers
   (`YArrayBase.OnMerge`), and `NoteDeparture`; (d) `Allocate` writes `Slot.Empty` and `NewItem` overwrites it (two
   64-byte writes); (e) the `Refs` object page allocated on the first use of each slab page and written per item until
   8.6 (then no longer: re-measure first); (f) the delta reads `Chars` per item: after 8.5 a span, no `GetString()`.
5. **`array-push-1txn`** (+9 %): the same path as 4 with `ContentAny` (`AppendValues` → `List.AddRange` today, the arena
   `Extend` after 8.8): 3(c) and 4(a).

What is not expected to move: the slot read by index against a field of an object the JIT keeps in a register (the 7.7
pass's conclusion), and the leaf order's cost on `apply-state` (design open question 6).

## Where corruption can hide

Debug checks first (8.0, 8.1, 8.3, extended by 8.4 and 8.7): `Arena.Violations()`; a content check after every
transaction (kind matches; ranges inside a live page's used part, crossing none; live ranges of an arena pairwise
disjoint and disjoint from the reservations; dead count equals a recount; no range on a collected slot; while the object
remains, the range equals it); test switches (tiny arena pages, always copy on merge, forced compaction per transaction
and mid-batch); Debug poison of freed arena pages and of staging on `Reset` and `Truncate`; an encoding compared before
and after compaction, which stays the check after 8.6 and 8.8 when no object remains to compare with.

* Arena offsets across compaction: only live String and Any slots hold offsets (a Free slot's `Payload` is the free-list
  link; a pending slot is in no list and released its range); deleted-uncollected runs stay; tails dropped; page 0 and
  the last page walked; no local holds an offset or span across a compaction point: a batch end inside user code
  reached from `YText.WriteTo`'s writer (8.5), from an observer or from an `Update` handler (compaction runs after the
  handlers, at the outermost batch end, with `AllowReuse`); the Debug poison exposes a span kept across one.
* A split's slice: `payload + diff` for String and Any only (Json splits as a cold object); the right part's
  `Countable` from its kind; `Marker` cleared; the tail reservation stays with the right part (it holds the end).
* Surrogate pairs at a split: in-place U+FFFD is safe only while ranges are disjoint; the same rule for
  integrate-at-offset (`CutCharsHead`) and `SliceStruct`, not for an offset write; `CanExtendRun`'s surrogate test reads
  the arena (8.5); adoption from staging at an offset replaces the first character the same way (9.5); a step-6 view at
  an offset inside a pair reads a lone low surrogate where Yjs' split gives U+FFFD.
* Format and embed: cold kinds between String runs; the text walks and the run-head index's live-format flag test the
  kind.
* Undo's redone content: `NewCopyOf` gives the copy a fresh range (8.6, 8.8).
* Snapshots and gc off: deleted items' text is read and encoded.
* Values kept alive: a value arena range that is dead but not cleared keeps the caller's objects until its page is
  released; every exit of a range clears it (8.7, D4), compaction included.
* Staging: re-entrant leases (one table per reader), reads after the release (the version), staged types without a
  `YType` (9.4c: depth from the parent chain, never from a created type), the (client, k) adapter between the table's
  ranges and the object lists (both in decode order; `AddStackToRestSS` cuts a range where it cut a list), a view that
  outlives its table (D7).

Design corrections owed (8.12): design 2.3's garbage rule; design 2.5 ("the same slots"); the file lists of steps 8
and 9 in design section 4 (step 8 also `ItemRef.Adopt`, `UndoManager.cs`, `YEvent.cs`, `PermanentUserData.cs`,
`YXmlFragment.cs`, `YMap.cs`, `YArray.cs`, `YType*.cs`, `AbsolutePosition.cs`, `YDoc.cs`, `StructStore.IntegrationPlan.cs`,
`ContentType.cs`, `TypeContent.cs`, `ItemView.cs`, `Transaction.cs`, `TypeTable.cs`, `Slab.cs`, `SlotKind.cs`; step 9 also
`EncodingUtils.cs`, `LazyStructWriter.cs`, `StructStore.Pending.cs`, `Lib0Reader.cs`, `StringDecoder.cs`, `ItemView.cs`,
`DecodedStruct.cs`, `PlanItem.cs`, `StructStaging.cs`, and `Item.cs`/`GC.cs`/`Skip.cs`/`AbstractStruct.cs` for the
removal); the design's `AbstractType` is `YType` in the code; a new 1.8 and 1.9 as the code is.

## Risks, and what each full review looks for

* **8.4 compaction**: every holder of an offset found (the walk over the clients' lists reaches every live slot; the
  tag carries the handle, not an offset-to-handle map); the trigger only at the outermost batch end, never inside a
  nested transaction or an observer; a span held across the point (`WriteTo`'s writer); the threshold's amortisation
  against a peer that alternates inserts and deletes (each compaction frees pages, so dead is bounded by live plus a
  page); the Debug poison on; the state before and after equal in v1 and v2 with gc off and snapshots; the cost of
  the walk (O(structs)) against the memory it frees.
* **8.7 the value arena**: the nulling at every exit of a range, including compaction's old ranges and a failed
  adoption; `ValueFreezer.OnRead` kept; `ContentView`'s read-only semantics kept for `Values`/`Elements`; the shared
  arena of all types of a document (a type's collection releases only its items' ranges).
* **9.4a the plan on staging**: Yjs' `integrateStructs` order unchanged (the stack, the targets by descending client,
  the yjs#824 early record); the rest encoded and its clocks recorded before the lease is released; the pending limits
  and the retry path; `SharesAClock` on ranges; `PlanItem.Staged` equality and hashing (never equal to a store item);
  what a hostile update can do with a table of many clients (the find by clock is per range).
* **9.5 adoption**: the offset path with surrogates; parent resolution in Yjs' order (root name, else the left then the
  right neighbour's, else the parent id found in the store and a Type slot); the type created once, at adoption, with
  its XML name or hook name; the subdocument's document; `Keep` and collected parents; a failed integration leaves no
  slot, range or type-table entry behind.
* **Open risks without a package**: the type table grows by 8 bytes per type ever created (an index is never reused:
  H5); a free list of indexes needs a generation to stay safe for a user-held collected type, so it is backlog, not
  step 8. D2's allocation on a 100k-character transaction (open question 1). Adoption from staging makes the
  `UpdateReader` cheaper but a load still copies each String twice (decoder buffer, table, arena) until a reader writes
  into the arena directly, which D8 defers until measured.

## Open questions for the coordinator

1. **D2's allocation**: accept that `text.type-100k-in-one-transaction` allocates about 2.4× the object layout's bytes
   (530 KB against 218 KB) because a run longer than a page doubles by copy, or chunk oversized runs across pages (which
   breaks "a run lies in one page" for every reader)? Recommendation: accept, held memory and time are what the layout is
   for; the budget row's comment says so.
2. **8.11** (D5): measured after 8.8, saves nothing; not built.
3. **The reader's shadow always on from 9.4a** raises the remote-update allocation until 9.6 (objects and table both
   filled): accept the temporary budget raise (D11) or start 9.4a only after 8.8 so that 9.5 follows at once?
   Recommendation: start now; the raise is one row with a comment, and lane C is otherwise idle.
4. **8.13 beside 9.5** (slot 5) with the file split given, or after 9.6 so that it measures the final load path too?
   Recommendation: beside 9.5; 8.12 measures everything once more at the end.
5. **8.m's dispatchers** generalise 8.3's inline `if (Kind == SlotKind.String)` hooks: if the 8.3 review changes those
   hooks, 8.m follows the review's shape. Who writes 8.m: the 8.3 author knows the hooks.
6. **Type-table index reuse**: backlog (above) or a package after 9.6? Recommendation: backlog, measured first on a
   workload that creates and collects many nested types.
