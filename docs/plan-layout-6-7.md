# Plan: layout steps 6 and 7 in work packages

Steps 6 and 7 of [`design-item-layout.md`](design-item-layout.md) are done (1.6 and 1.7 there describe the code as it
is). What is left of the plan is the status of each package and the items that stay open.

## Step 6: the public API break: done

| pkg | concern | status |
| --- | --- | --- |
| 6A | `ItemView`, `ItemKind`, a detached flag on the objects | done |
| 6B | `DecodedStruct` | done |
| 6C | tests stop reading internals through public classes | done |
| 6D | the break (`feat!:`, one commit) | done |
| 6E | docs | done |

## Step 7: the slab, content still as objects: done

| pkg | concern | status |
| --- | --- | --- |
| 7.0m | pure move of the integration plan, pending stores and test hooks into partial files | done |
| 7.1 | store consistency check (`StructStore.Checks.cs`) | done |
| 7.2 | `Slab`, `TypeTable`, client table, standalone | done |
| 7.0a | the decoded form gets its own algorithms back | done |
| 7.0e | a collected type keeps its item's facts | done |
| 7.0b | the rest store as per-client decoded lists | done |
| 7.0c | the integration plan's pre-checks no longer key by `Item` | done |
| 7.0d | one adoption point from decoded to store (`ItemRef.Adopt`) | done |
| 7.3a to 7.3c | shadow: slots and free, links and parent, content and key | done |
| 7.4 | reads from the slab | done |
| 7.6 | reuse and page release; the XML siblings of a collected type answered by id | done |
| 7.5a | objects removed: `ItemRef` is (slab, handle), the Debug generation check | done |
| 7.5b | holders as `ItemHandle` | done |
| 7.7 | performance pass | done |
| 7.8 | measurements and docs | done (bench/Ycs.Compare/README.md, "Step 7 against the object layout") |

## Open items

* Slot reuse at the end of the outermost batch is defensive: a stricter sabotage (reuse after each transaction's merge
  pass) found no reader that fails. The rule stays; one unit test pins it.
* `Keys` is a second page set beside `Refs`; folding it into `Refs` is 8.11 of
  [`plan-layout-8-9.md`](plan-layout-8-9.md), optional.
* The decoded form (`Item`, `GC`, `Skip`) is removed in step 9 (9.6), which also moves `UpdateOperations`,
  `EncodingUtils` and `LazyStructWriter` to staging tables.
