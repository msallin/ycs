# Plan: layout steps 6 and 7 in work packages

The packages that carry out steps 6 and 7 of [`design-item-layout.md`](design-item-layout.md). Each package is one
concern, leaves every suite green and the update bytes identical, and is done by one implementing agent. Packages that
run at once own no file in common. A package marked *full review* gets an independent review before it merges.

## Decisions taken with this plan (2026-11-16)

* **Views of structs that are not in the store** (the GC filter runs on planned, decoded items:
  `StructStore.cs` `IntegrationPlan`): a view's hint can be detached, holding the decoded object; 6A fixes its
  layout so that step 7 keeps it.
* **A map item's key in step 7**, while content is still an object: a second `object[]` page set beside `Refs`,
  folded into one in step 8 (not a `KeyedRef` allocation per map item).
* **A collected struct's slot keeps its Left and Right**, as Yjs' orphan does; `IsItem` reads false.
* **`ItemRef.New` reaches the slab** through `parent.Doc.Store`.
* **A collected type keeps its item's facts** (`Parent`, the id, `Deleted`) without its struct (7.0e), because the GC
  struct can merge away and its slot be reused. The holder table of design 2.4 gains `AbstractType._item` and
  `YDoc._item`.
* **Plenum** no longer consumes ycs: step 6's migration is `docs/api.md` alone; open question 5 of the design closes.

## Step 6: the public API break

After step 5. 6A, 6B and 6C run in parallel (6B branches from 6A's first commit, which adds `ItemKind.cs`); 6D after
all three; 6E after 6D.

| pkg | concern | owns | invariant | proof | risk |
| --- | --- | --- | --- | --- | --- |
| 6A | `ItemView` (internal), `ItemKind`, a detached flag on the objects | new `Utils/ItemView.cs`, `Utils/ItemKind.cs`; `Structs/Item.cs`, `Structs/GC.cs` (the flag); `ItemRef.Operations.cs` (`MergeItemWith` sets it); `StructStore.cs` (`ReplaceStruct` sets it); `tests/Ycs.Tests/ItemViewTests.cs`; a hook in `YEvent.cs` | a view reads what design 3.2 specifies; at an offset its content is a read-only slice, never `Splice`; inside a transaction a view reads what its object reads | a test mode comparing every reachable view with its object in every observer call (trace replay, event traces, corpus); the post-merge rule (H7), GC views, default views, equality, hashing, detached views. Sabotage: drop the flag in `MergeItemWith`, `GC.MergeWith` or `ReplaceStruct` | medium |
| 6B | `DecodedStruct` | new `Utils/DecodedStruct.cs`; `UpdateOperations.cs` | each decoded struct maps 1:1 to a detached record (Skip and GC included) | differential against today's `Structs` over the corpus and the update-operation fuzz inputs. Sabotage: a Skip read as GC | low |
| 6C | tests stop reading internals through public classes | new `Utils/StructStore.ForTests.cs`, `Types/AbstractType.ForTests.cs`; `tests/Ycs.Properties/Adapter.cs` (`StoreDump`); `tests/Ycs.Corpus/{ItemListInvariants,YcsAdapter}.cs`; `tests/Ycs.Fuzz/Consistency.cs`; the internal readers in `tests/Ycs.Tests` | `StoreDump` text unchanged, kind names from a mapping, not `GetType().Name` | old and new dump equal on every recorded properties script. Sabotage: a dropped marker field fails `run-extension` | low, load-bearing |
| 6D | the break (`feat!:`, one commit): done | `YEvent.cs`, `YText.cs`, `YDoc.cs` (`GcFilter`), `UndoManager.cs`, `AbstractType.cs`, `YArrayBase.cs`, `DeleteSet.cs`, `StructStore.cs`, `UpdateOperations.cs`; visibility of `Structs/*`; `PublicApi.txt`; `docs/api.md` | no public member exposes a struct or content class; behaviour byte-identical | `PublicApiTests`; every suite; the Native AOT sample publishes without warnings | medium |
| 6E | docs | `design-item-layout.md`, `testing.md` | | | low |

## Step 7: the slab, content still as objects

Order, green at every commit: preparation on today's objects, then the slab as a shadow of the objects (writes to
both, reads from the objects, compared after every test transaction), then reads from the slab, then slot reuse
while the objects still exist (every access asserts the slot still belongs to its object: the stale-handle
detector), then the objects removed, then the holders as plain ints.

1. 7.0m first; then 7.1, 7.2, 7.0a, 7.0e in parallel; 7.0b then 7.0c; 7.0d after both chains.
2. 7.3a, 7.3b, 7.3c (shadow).
3. 7.4 (reads from the slab).
4. 7.6 (reuse and page release, objects still alive).
5. 7.5a (objects removed), 7.5b (holders as ints).
6. 7.7 (performance), 7.8 (measurements and docs).

| pkg | concern | owns | invariant | proof | risk |
| --- | --- | --- | --- | --- | --- |
| 7.0m | pure move: integration plan, pending stores, test hooks into partial files | `StructStore.cs`, new `StructStore.{IntegrationPlan,Pending,ForTests}.cs` | no behaviour change | build, suites | low |
| 7.1 | store consistency check on today's objects | new `StructStore.Checks.cs`; its call in `Transaction.cs` | links mirror; orders contiguous; no holder (type starts, live types' `_item`, map values, markers, push hint, `YDoc._item`) at a detached struct; types never reused | after every test transaction. Sabotage: a missed `RetargetMapEntry`, a missed `OnMerge` | low |
| 7.2 | slab, type table, client table, standalone | new `Utils/Slab.cs`, `Utils/TypeTable.cs`, unit tests | geometric page 0 (16 slots doubling to 1,024); per-page free lists; reuse only with no cleanup pending; page released when all its slots are free; Debug poisons a grown page 0's old array | unit tests: page 0 from 1 slot, release, free-list soundness | low |
| 7.0a | the decoded form gets its own algorithms back | `Item.cs`, `GC.cs`, `Skip.cs`, `UpdateOperations.cs`, `LazyStructWriter.cs` | `Write`, merge and split for `MergeUpdates` run on decoded objects without `ItemRef`; `ItemRef` serves store structs only | bytes of `MergeUpdates`, `DiffUpdate`, conversion, rest-store encoding equal on corpus and fuzzer | low-medium |
| 7.0e | a collected type keeps its item's facts | `AbstractType.cs`, `ContentType.cs`, `YDoc.cs`, `ContentDoc.cs` | a collected type answers `Parent`, its id and `Deleted` without its struct | collect a nested type, merge its GC struct away, read `Parent`, a relative position, insert. Sabotage: read the struct | medium, *full review* |
| 7.0b | the rest store as per-client decoded lists | `StructStore.Pending.cs`, `StructStore.IntegrationPlan.cs`, `EncodingUtils.cs`, `ClientStructs.cs` | no `StructStore` or `ClientStructs` holds decoded objects | pending and retry tests, `interop/js/pending-orders`, fuzzer, pending encodings byte-equal | medium |
| 7.0c | the integration plan's pre-checks no longer key by `Item` | `StructStore.IntegrationPlan.cs` | a `PlanItem` (store `ItemRef` or decoded `Item`) replaces `Item` keys; a collected parent keys as itself and is judged deleted by `ItemDeleted`, not by its stale struct (`KeyOf`, `LandsLast`; reachable when a peer that still has the type inserts next to an item inserted locally into the held collected type) | hardening, conflict resolution, update fuzzer, map-value splits. Sabotage: key by id alone | medium-high, *full review* |
| 7.0d | one adoption point from decoded to store | `StructStore.IntegrationPlan.cs`, `Item.cs`, `GC.cs`, `ItemRef.cs` (`Adopt`) | decoded structs enter the store only through `ItemRef.Adopt`; afterwards `Of`/`ToStruct`/`ToItem` have no callers outside `ItemRef.cs` (IL test) | corpus byte for byte, `walk-index`, soak | medium-high, *full review* |
| 7.3a | shadow: slots and free | `ItemRef*.cs`, `Item.cs`/`GC.cs` (handle), `ClientStructs.Leaves.cs`, `StructStore.cs` | every store struct has one live slot; GC replacement takes the handle over; merge frees, no reuse yet; scalars written to both | 7.1 plus slot-equals-object after every transaction (Release, Debug, leaves 2 and 4) | high, *full review* |
| 7.3b | shadow: Left/Right handles, Parent through the type table | `ItemRef*.cs`, `TypeTable.cs`, `AbstractType.cs`, `ContentType.cs` | links and parents in slots equal the objects | as 7.3a. Sabotage: a missed `rightRight.Left` | medium |
| 7.3c | shadow: content and key in Refs | `ItemRef*.cs`, `Slab.cs` | Refs equal content and key; free slots hold null | as above; a weak reference shows a merged right part's content released | medium |
| 7.4 | reads from the slab | `ItemRef.cs` | objects become write-only mirrors, still compared | every suite, the comparison, budgets | medium |
| 7.6 | reuse and page release | `Slab.cs`, `Transaction.cs` | design 2.4 (H4): reuse only after the outermost batch; Debug asserts slot owner == object on every access; decided before it starts: the XML siblings of a collected type (`NextSibling`, `PrevSibling`, `InsertAfter` read the old struct's links, and a collected struct merges into a deleted left neighbour or, nested, becomes a GC struct that merges, so its slot is freed and reused: `InsertAfter` would link a new item beside another type's struct) either pin a collected type's slot against reuse, or refuse a collected ref, or answer as Yjs' orphan through captured data (a documented deviation) | step 5's H4 property with real reuse. Sabotage: reuse at transaction end fails H4; poisoning freed slots changes nothing | high, *full review* |
| 7.5a | objects removed: `ItemRef` is (slab, handle) | `ItemRef*.cs`, `Item.cs`/`GC.cs` (decoded only), `StructStore*.cs`, `AbstractType.cs`, `ContentType.cs`, `DeleteSet.cs` | no store `Item`/`GC` allocated; Debug generation check | every suite, corpus byte for byte, differential fuzz at leaves 2/16/128, soak | high, *full review* |
| 7.5b | holders as plain ints | `ClientStructs*.cs`, `AbstractType.cs`, `YArrayBase.cs` | no holder at a Free slot | as above | medium |
| 7.7 | performance | `ItemRef*.cs`, `StructStore.Conflicts.cs`, `YArrayBase.cs` | `ref Slot` only after a method's last allocation | runs with page 0 at 1 slot (sabotage: a ref kept across `New`); `ab.py`; budgets lowered | medium |
| 7.8 | measurements and docs | `docs/*`, `bench/Ycs.Compare/README.md`, `Budgets.cs` | | | low |

Where stale handles can hide once slots are reused (7.6): a collected type's `_item` (the facts of 7.0e cover all but the XML siblings, named in the 7.6 row, and `YXmlFragment`'s tree walker walking up) and `YDoc._item`; search markers
and the push hint when `OnMerge` is skipped (it runs only for a `YArrayBase` parent); map values when
`RetargetMapEntry` is missed; `ContentType.Open` frames; integration-plan dictionaries kept past `Clear`; anything step
5 missed.

Design corrections owed (6E and 7.8): step 7's file list in design section 4 lacks `UpdateOperations.cs`,
`EncodingUtils.cs`, `LazyStructWriter.cs` and `Skip.cs`; design 2.4 says a struct becomes GC in place, today
`ReplaceStruct` puts in a new object (7.0e and 7.3a settle it); `Ycs.Properties` reads public internals through
`UnsafeAccessor` and breaks at 6D unless 6C lands first.
