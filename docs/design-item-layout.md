# Design: items as structs in a slab, linked by handles

How the store holds its structs, as the code is. Parity target: Yjs 13.6.32, the same content, events and update bytes.
No unsafe code: arrays of structs and `ref` to their elements only. The figures come from `bench/Ycs.Compare`
([README](../bench/Ycs.Compare/README.md)) and `bench/Ycs.Layout` ([README](../bench/Ycs.Layout/README.md), `results/`);
the open items are in section 5.

## Summary

Every struct of a document (an item or a GC struct) is a 64-byte slot of the document's `Slab`, linked to its neighbours
by 32-bit handles (1.6). The characters of a String and the values of an Any are ranges of append-only arenas that
compact (1.7, 2.3). An update decodes into a staging table; no struct object exists (1.8, 2.5). A client's structs are
in clock order in leaves of at most 128 positions with a run-head index, which makes Yjs' marker walks jump within a
chain (1.2, 1.3). The library holds structs through the internal `ItemRef`; callers read them through the public
`ItemView` (3).

Measured:

* Held memory is 49-64% of the object layout's on the six workloads of the comparison, and on documents of fewer than
  about 20 structs as much as it or more (2.2); allocation is 38-87% lower on most editing workloads
  ([Compare](../bench/Ycs.Compare/README.md), "Step 8 and 9 against the object layout"). A load allocates no struct
  object (`apply-updates` 11.9 MiB against 59.3).
* The run-head index takes `array-delete-random` from 57 s to 0.9 s and `text-append-1txn-observed` (100,000
  characters, one transaction) from 49 s to 0.8 s (Compare, "After layout steps 1 and 2").
* The costs: typing and observed appends are slower than on objects (`trace-replay` +19%, `text-append` +26%; the
  gaps and what was tried: Compare, "Step 8 and 9 against the object layout"); the leaf order costs 7-10% on
  `apply-state` ("The leaf order").

## 1. How the code is organised

### 1.1 `ClientStructs`

`ClientStructs` (`src/Ycs/Utils/ClientStructs.cs`) holds one client's structs in `StructStore.Clients`: the handles, the
start clocks beside them and the end clock. `FindIndex` (Yjs' `findIndexSS`, with its pivot, after a look at the last
struct) and the galloping `FindIndex(clock, ref hint)` for in-order lookups search the starts only.

* **Invariant**: the structs are contiguous by clock: the end of position i is the start of i + 1; `End` is the end of
  the last. Every change of the list goes through `ClientStructs` (`Add`, `InsertSplit`, `RemoveMerged`, `Replace`,
  `LastExtended`).

### 1.2 The run-head index

`ClientStructs.RunHeads.cs`. Yjs' walk-left (`while (p.left.id.client === p.id.client && p.left.id.clock +
p.left.length === p.id.clock)`) and the walks from a search marker would visit every item of a run. The index answers
them without the visit.

* **Invariant**: the struct that ends at a struct's clock is its predecessor in its client's order, so position i is
  *linked* exactly when its `Left` is the struct at i - 1, and a *break* otherwise (position 0, a GC struct, a struct
  after one). A flag byte per position holds `Break`, `Visible` and `Format`; the visible length is the clock length
  when the flag says visible. A walk from i ends at the last break at or before i, having subtracted the visible
  countable length of the positions it stepped onto. The flag is taken from the final `Left` after conflict resolution,
  never from origin ids. A live non-countable item (a format) is flagged so that a text's walks stop at it.
* **The walks**: `RunHead` (walk left), `SeekLeft` (the middle loop), `SeekRight` (the walk right and the callers'
  walks from the marker). The right walk's rule: a jump passes only positions whose visible sum stays below the target
  and never the run's last position; Yjs' loop takes the last steps, so when the sum reaches the index exactly at an
  item's end the walk steps on to that item's right neighbour, whichever it is (`RunHeadIndexTests`).
* **Kept current at**: `Add`, the four split sites (`InsertSplit`), both merge passes (`RemoveMerged`: incremental up
  to 32 structs, a rebuild beyond), `Replace`, `ExtendRun` (`LastExtended`), and through `StructStore.Refresh` at
  `ItemRef.MarkDeleted`, `ContentDeleted.Integrate` and the relink of a right neighbour in `ItemRef.Integrate`. The
  index is current at every lookup, mid-transaction included.
* **Short walks stay literal** for 32 steps (`YDoc.LiteralWalkStepsForTests`, 32 by default), so an ordinary edit pays no position lookup; a
  collected type's items fall back to the literal walk (`StructStore.TryFindPosition`). An item continuing a collected
  struct is rejected (`YUpdateException`, as Yjs throws), so a GC struct is never an item's `Left`.
* **Checks**: a recount of every list after every test transaction (`StructStore.CheckAfterTransactionsForTests`); the
  `walk-index` property (each lookup through the index against Yjs' loops item by item, with run extension on and off);
  `interop/markers` and `interop/properties` soaks against Yjs.

### 1.3 The leaf order

`ClientStructs.Leaves.cs`. A client's structs, starts and flags are in leaves of at most 128 positions
(`ClientStructs.LeafCapacity`; `LeafCapacityForTests` sets another in the tests), each with its visible sum, break count
and live-format count. From two leaves on there is a directory: the leaves, their first starts (the search by clock: the
last leaf, the leaf of the last lookup, then a binary search), their first positions (the search by position, the same
way), a has-break and a has-live-format bit per leaf in 64-bit words, and a Fenwick tree of the leaves' sums. Positions
are global ints.

* **Invariant**: the leaves in order hold the structs contiguous by clock; every leaf holds 1 to 128 structs (one leaf
  without a directory, 0 before the first `Add`); each directory entry's first start and first position, each leaf's
  sum and counts, the bits and, when not marked stale, the tree equal a recount (`Violations`).
* **Kept current at**: a split shifts within the left part's leaf (a full leaf first splits in two) and adds 1 to the
  later leaves' first positions; a merge removes from one leaf or more, drops emptied leaves, and a leaf at or below a
  quarter then merges with a neighbour when they fit; `Add` opens a new leaf when the last is full; a list of one leaf
  has no directory (it comes at the 129th struct and goes when merges leave one leaf). A change of the leaves inserts or
  removes their bits in place and marks the tree stale; the next read rebuilds it (O(leaves)).
* **The walks**: the run's head is the last break in the position's leaf, else in the last leaf before it whose bit is
  set; the run's end the same way forward. `RunHead`'s sum, `SeekRight` and `SeekLeft` go position by position in the
  first and last leaf and jump whole leaves through the tree (the leaf where the sum reaches the target is found by one
  descent), bounded by the run's last leaf and, for a text, the next leaf with a live format. A lookup is O(128 + log
  leaves + leaves/64). The tree over the leaves' sums keeps typing into an observed text in one transaction
  logarithmic; the has-live-format bit stops the walk right of a text at a leaf with one.
* **Why 128**: measured against 64 and 256 ([Compare](../bench/Ycs.Compare/README.md), "The leaf order"). The leaf order
  also spares the shift of thousands of later structs at every split and merge; a flat order would make a delete 2.2-2.9
  times slower ([Layout](../bench/Ycs.Layout/README.md), "Editing").
* **Checks**: the recount covers the directory; `Ycs.Tests` runs also with leaves of 2 and 4, the corpus with leaves of
  2, the properties with leaves of 4; merges of 3 to 81 structs ending before, at and after a leaf boundary
  (`RunHeadIndexTests`).

### 1.4 `ItemRef`

`Structs/ItemRef.cs`, `Structs/ItemRef.Operations.cs`. The library holds the structs of the store through `internal
readonly struct ItemRef : IEquatable<ItemRef>`: (slab, handle). Yjs' `Item` algorithms are members of it (integrate,
split, merge and its test, delete, mark deleted, gc, keep, visibility, write, next and prev, run extension).

* **Shape**: `default` is Yjs' null (`IsNull`); `==`, `Equals(ItemRef)` and `GetHashCode` are written out; `IsItem`,
  `IsGC`, `IsSameKind` (the merge rules' `GetType()` comparison). The members every kind has (`Delete`, `Integrate`,
  `CanMergeWith`, `MergeWith`, `Write`) dispatch by kind. `ItemRef.New` is Yjs' `new Item(id, left, left &&
  left.lastId, right, right && right.id, parent, parentSub, content)`. A staged struct enters the store only through
  `ItemRef.Adopt` (1.8), called by `IntegrationPlan.Execute` alone (`ItemRefAdoptionTests` reads every method body).
* **Cost**: no allocation, no box (`ItemRefBoxingTests` reads every method body of the library and fails on a `box` or
  `constrained.` of an `ItemRef`), no `ValueType.Equals`, no interface dispatch. The accessors are `AggressiveInlining`:
  `Integrate` and the walks read more of them than the JIT inlines by its budget.
* **Traps**: an `ItemRef` compared with `null` binds to the lifted operator and is always false (CS8073 is an error in
  `src`); `(object)type._item ?? type` boxes and is never null; `x.GetType()` names `ItemRef`; `is { }` always matches.
* **Invariant**: no struct class exists but `ItemRef` (the slot) and the staged form (`StagingTable`, `DecodedStruct`).
  The Debug-only generation check and the rule for code that reads several fields of a slot are in 1.6.

**The public boundaries.** No public member names an internal struct type, and none converts. Callers read an item
through `ItemView` (3.2: a store view by id, or the detached view of a decoded struct the integration plan hands to
`GcFilter`, made in `ItemView.Detached` only) and an update's structs through `DecodedStruct` (3.3). `YDoc.GcFilter` and
`UndoManagerOptions.DeleteFilter` take an `ItemView`.

### 1.5 Ids across free points

A struct is freed or replaced at a cleanup's merge pass and at a type's collection. Nothing that can outlive either
holds a struct without its id (2.4).

| holder | holds |
| --- | --- |
| `Transaction._mergeStructs` | `List<ItemId>`: the id a split or a collection added; the merge pass finds the struct that holds it by the clock |
| `YArrayBase.EnumerateRaw` (every iteration of an array or an XML fragment), `YText.WriteTo` (`ItemRef.WriteCharsTo`) | the item it stands on, its id and `StructStore.Departures` read before the caller's code runs; `StructStore.NextAfter` goes on from the item when the count is unchanged, else from the struct that holds the id now (one lookup by id) |
| the XML tree walker (`YXmlFragment.Walk`) | the current node, its id and the count, the same way |
| `YArrayBase._searchMarkers` of a collected type | cleared with the push hint when the type is collected (`ForgetList`) |

`Departures` counts the structs that left a list: merged into their left neighbour (`ItemRef.MergeItemWith`) or replaced
by a GC struct (`StructStore.ReplaceWithGC`). It is the enumerators' validity check.

* **After a merge an enumerator continues from the struct that holds its id now** (`EnumeratorLifetimeTests`). That is
  where Yjs' orphaned item continues unless the list changed right of it since: an item that arrives right of the
  merged struct after the merge is visited (Yjs' orphan skips it, `a,c,d,z` against `a,c,d,y,z`). When the id lies in a
  GC struct the type was collected, and the walk ends: for the list enumerators that is where Yjs ends too; a tree
  walker standing in the collected type ends where Yjs' orphan walks up through its old parent to the next node
  (`e1,c` against `e1,c,e3`). Throwing on a changed store, as .NET collections do, would break code that edits while
  iterating.
* **Checks**: `Ycs.Properties` scripts whose handlers make nested transactions (`GenOptions.Hooks`,
  `fixtures/hooks.jsonl`, `HookScriptTests`), `EnumeratorLifetimeTests`, `CollectedTypeMarkersTests`.

### 1.6 The slab

`Utils/Slab.cs`, `Utils/TypeTable.cs`, `Utils/ClientTable.cs`, `Structs/ItemHandle.cs`, `Structs/ItemRef*.cs`. Every
struct of a store is a slot of its document's `Slab`.

* **A slot** is the 64-byte sequential struct of 2.1 with no reference field. Its kind (`Info`, bits 8 to 15) is the
  content's wire reference number: 0 for a GC struct, `Slot.FreeKind` (0xFF) for a slot on a free list. `Payload` holds
  the free-list link of a free slot, the type-table index of a Type slot and the arena offset of a String or Any slot
  (1.7); a Deleted and a GC slot hold 0.
* **Pages** (2.2): page 0 starts at 1 slot (`Slab.FirstPageSlotsForTests` changes it for the test runs) and doubles by
  copy up to 1,024; the next pages hold 1,024 and never move. A handle is `page << 10 | index`; a read is two array
  indexes. Beside each page two `object[]` pages, `Refs` (the content object of a cold kind) and `Keys` (a map item's
  key), are allocated on first use, null for a free slot; beside the slab, `Redone` (`Dictionary<int, ItemId>`, flag
  `HasRedone`) holds the id each redone item names. `Keys` stays a second page set: folding it into `Refs` saves nothing
  (a map item of a hot kind holds no `Refs` entry; measured on `map-set-distinct`: the `Keys` page is 3% of 24.9 MiB).
* **`ItemRef`** is (slab, handle), two fields in Release (a test asserts them). Two references are equal when they name
  the same slot of the same slab. In Debug it also keeps the slot's generation (below). Code that reads several fields
  of a slot takes one `ref Slot` or `ref readonly Slot` after the method's last allocation (`Allocate` may move page 0)
  and reads it once per step.
* **A client and a parent are indexes.** `ClientTable` numbers the clients of a document and `TypeTable` its types,
  both never reused; a slot names them in four bytes each. A type gets its index when its item is made (`ItemRef.New`,
  `Adopt`); a collected type's entry is cleared and stays (a slot inserted into a collected type puts it back). Each
  type ever created costs one 8-byte entry: 1,000 nested maps created and collected leave 1,001 entries.
* **Free and reuse.** A merge frees the right part's slot (`ItemRef.FreeSlot`: its kind becomes Free, its objects and
  redone id are dropped, its generation changes). It joins the free list only at `Slab.AllowReuse`, which `Transaction`
  calls at the end of the outermost batch, nested transactions of observers and `Update` handlers included (2.4). A
  page, but page 0, left with no live slot is released then. Debug poisons a slot when it joins the free list and the
  old array of a grown page 0.
* **A GC struct is the slot of the item it replaces.** `StructStore.ReplaceWithGC` calls `ItemRef.BecomeGC`, which
  rewrites the slot in place (client, clock and length stay; `Info` is Deleted alone; links, origins and parent are
  none; content, key and redone id are dropped), puts the same handle back in the client's list and counts a departure
  (1.5). Every `ItemRef` and `ItemHandle` to it stays valid and reads a GC struct. A GC struct merges into a GC struct
  to its left like any struct, which frees its slot.
* **Holders** keep an `ItemHandle`, not an `ItemRef` (2.4): the leaves of `ClientStructs`, a type's start, item and map
  values (`YType._startHeld`, `_itemHeld`, `_map`), the search markers and the push hint. A type keeps its document's
  slab. `YDoc._item` stays an `ItemRef` (a subdocument's item is in its parent's slab).
* **Generations, Debug only.** Each slot has a generation that a free changes. An `ItemRef` made from a handle or an
  `ItemHandle` keeps the generation of the slot when it was made, and a read through a reference that finds another
  one throws `InvalidOperationException` (`Slab.CheckGeneration`): a holder that outlived its struct fails at the
  read. In Release there is no generation and a read costs the index.
* **The store check.** `StructStore.CheckForTests` runs after every test transaction: links mirror, the orders are
  contiguous, the free lists are sound, no holder names a freed slot, a type item names its type, and a type's entry
  in the table is never reused. The test runs also set leaves of 2 positions, page 0 to `YCS_SLAB_FIRST` slots (1:
  every allocation of it copies) and, with `YCS_POISON=1`, poison the handle of a collected type (2.4).

### 1.7 Content in the arenas

`Utils/Arena.cs`, `Utils/Slab.Compaction.cs`, `Utils/StructStore.Compaction.cs`, `Utils/SlotKind.cs`,
`Structs/ItemRef.Content.cs` and the partials it dispatches to (`ItemRef.Chars.cs`, `ItemRef.Values.cs`,
`ItemRef.Content.Collections.cs`, `ItemRef.Content.Other.cs`, `ItemRef.Content.Text.cs`, `ItemRef.Runs.cs`,
`ItemRef.Write.cs`). The content of an item is in its slot or in the slab beside it, by kind (`SlotKind`: the wire's
reference numbers, with `Skip` for the staged form only and `Free` for a slot on a free list):

| kind | where the content is | the slot's `Payload` |
| --- | --- | --- |
| `String` | `Slab.Chars`, an `Arena<char>`: UTF-16 code units | the offset of the range; `Length` is its length |
| `Any` | `Slab.Values`, an `Arena<object?>`: the caller's objects, one per element | the offset of the range; `Length` is its length |
| `Deleted`, `GC` | nothing | 0 |
| `Type` | the type table; one element | the table index (`ItemRef.TypeOf` throws for a collected type) |
| `Json`, `Binary`, `Embed`, `Format`, `Doc` | an object in `Refs` (`ItemRef.Cold`; `SlotKinds.HasObject`, `IsCold`) | 0 |

* **The arena** (2.3) knows runs only as the (offset, length) pairs its caller holds. An offset is `page << 32 |
  index`. The slab is the caller: a String or Any slot owns the range its `Payload` and `Length` name, and no other
  structure keeps an offset.
* **One dispatcher per event, one partial per kind.** `ItemRef.Operations.cs` (integrate, split, merge, free, collect)
  reaches content only through the hooks of `ItemRef.Content.cs`: store at `NewItem`, split (`SplitContent`), merge
  (`MergeContent`), release (`ReleaseContent`), write, copy, delete, collect, integrate. A String's work is in
  `ItemRef.Chars.cs`, an Any's in `ItemRef.Values.cs`, the cold kinds' through their `IContentEx` object, a Type's in
  `TypeContent.cs`, a Deleted's in the hub.
* **Split** gives the right part the range from `payload + diff`: no copy, no allocation, for a String and an Any. A
  cut between the two halves of a surrogate pair writes U+FFFD over both in place (the ranges of an arena are
  disjoint). The right part holds the end of the left's reservation, if the run had one (2.3). A Type cannot be split
  (it has one clock).
* **Merge** (the right neighbour's range joins the left's): when the right range follows the left in the arena nothing
  moves; else the left run is extended (`Arena.Extend`: in place, in its reservation, or copied away with slack) and
  the right range is marked dead. A merge of Any values clears the right range and, when the run moved, the old left
  range, so the caller's objects are collectible at once. `Length` is the sum only after the content merged: two
  windows (`AppendChars` and `AppendValues` before the caller adds to `Length`, `MergeItemWith` before `Length +=
  right.Length`) have a slot whose `Length` lags its range, and no compaction hook sits in them.
* **Release.** A range ends when its item is collected (`BecomeGC`, `BecomeDeletedContent`), when its slot is freed
  (`FreeSlot`; after a merge `FreeSlotKeepingChars`, because the left run took the range over) or when the run was
  copied away. Release marks the range dead (`Arena.MarkDead`) and, for Any, clears its elements (`Arena.Clear`). A
  deleted item that is not collected keeps its kind and its range: its encoding reads them (gc off, kept items,
  snapshots). `BecomeDeletedContent` keeps the item's `Countable` bit, as Yjs does; the right part of a split takes it
  from its own kind (`SlotKinds.IsCountable`), as Yjs takes it from the spliced content.
* **Reads.** `ItemRef.Chars` and `ItemRef.ValueSpan` are spans of the arena, valid until user code runs or a batch
  ends. What outlives that is a copy: `Elements` and `Values` (an `ItemView`'s and an event's content), and `Stored`,
  which the list enumerators read through an array rented from `ArrayPool<object>` that the enumerator returns in its
  `finally`. `WriteCharsTo` asks the writer for its span first and reads the arena after, from the struct that holds
  the item's id then. `ValueFreezer.OnRead` wraps what leaves the arena as a read-only value.
* **Writes.** `WriteItem` reads the slot once and encodes a String from the arena span, an Any value by value
  (`WriteValues`), a cold kind through its object. `NewCopyOf` (the undo manager's redo) gives the copy a range of its
  own.
* **Checks.** Every test transaction runs `StructStore.CheckForTests`, which, for the arenas, recounts
  (`Arena.Violations`): every String and Any slot's range lies in the used part of a held page, live ranges are
  disjoint from each other and from the tail reservations, `Live`, `Dead` and the tail table agree with a recount, and
  no element outside a live range of the value arena holds a value (`Arena.UnclearedOutside`: every exit of a range
  clears it). Debug fills the pages a compaction frees with a marker, so a span held across one reads it. The test
  modes force compaction (2.3).

### 1.8 Staging tables and adoption

`Utils/StagingTable.cs`, `Utils/StructStaging.cs`, `Utils/UpdateReader.cs`, `Utils/LazyStructReader.cs`,
`Utils/LazyStructWriter.cs`, `Utils/EncodingUtils.cs` (`StructCursor`), `Utils/UpdateOperations.cs`,
`Utils/StructStore.IntegrationPlan*.cs`, `Utils/StructStore.Pending.cs`, `Utils/PlanItem.cs`, `Utils/ItemView.cs`,
`Structs/ItemRef.Adopt.cs`. An update is read into a `StagingTable` and nowhere else (2.5).

* **The read.** `UpdateReader.Read` (one reader per thread and open lease, two kept: the pending update's retry reads
  inside the lease of the update that set it aside) decodes with a `StructCursor`, whose `NextStaged` stages one
  struct at a time and makes the checks that need no document (ranges, no empty struct, no client twice, the struct
  limit, an origin or parent of the same client only at an earlier clock). A String's characters come from the
  decoder as a span (`IUpdateDecoder.ReadStringSpan`) and are copied into the table once; a Type's reference and name
  are read by `ContentType.ReadHeader`; a cold kind is read as its object. Disposing the lease resets the table.
* **The apply.** `YDoc.ApplyUpdate` decodes the whole update before it touches the document, then
  `StructStore.ApplyUpdate` plans on the staged fields: `IntegrationPlan` works on `PlanItem`s (a store item by id, or
  a staged one by table and index, never equal to each other), keeps Yjs' order, checks nesting and garbage
  collection on the staged fields (a staged Type is its kind; no `YType` exists yet), and `EncodeRest` writes what
  cannot integrate yet as a v2 update (`LazyStructWriter`) for the pending store, which is read again, into a table,
  when it is retried. `Execute` calls `ItemRef.Adopt` per struct.
* **Adoption** (`ItemRef.Adopt(table, i, transaction, offset, integrate)`, the one entry from staged to store; it reads
  the table and never changes it) resolves the references in Yjs' order (the origins' structs split where an origin
  points into one; the parent from a root name, the left's or right's, or an id found in the store as a Type slot),
  then makes the slot: characters and values are copied into the arenas from `offset`, a cold object becomes the
  slot's (`ContentJson.Tail` builds the right part of an offset adoption without changing the table), a Type's `YType`
  is created here and given its table index, a GC struct is trimmed. A neighbour that is a GC struct, or a collected
  parent, makes the item enter as a GC struct. A failed integration frees the slot.
* **`UpdateOperations`** (`MergeUpdates`, `DiffUpdate`, `ConvertUpdateFormat`, `EncodeStateVectorFromUpdate`,
  `ParseUpdateMeta`, `DecodeUpdate`, `DescribeUpdate`) works on tables rented from the thread's pool
  (`StagingTable.Rent`, `Return`). A merge of k updates holds k + 1 tables: a `LazyStructReader` per update, holding
  its current struct only (`Truncate` before every read), and the pending struct, which takes the reader's table when
  the struct moves whole and is `Truncate`d and copied into (`StructStaging.CopyFrom`: the cut as Yjs' `splice` makes
  it) when it is cut. `LazyStructWriter.WriteStruct` writes a staged struct as `ItemRef.Write` writes a slot, both
  through `ItemRef.WriteItemHeader`.
* **`DecodeUpdate`** keeps a table of its own in the public `DecodedUpdate`; `DecodedUpdate.Structs` builds the public
  `DecodedStruct` records from it on first use (`StructStaging.Decoded`).
* **`ItemView.Detached`** is the view of a staged struct that the integration plan hands to `GcFilter`: (table, index,
  version). While the table's version matches it reads the staged fields; after a reset or truncate it looks its id up
  in the store and throws `InvalidOperationException` when the store has no such struct.

## 2. The layout

### 2.1 A slot

Sequential layout, no reference field (the garbage collector does not scan the slab). Clocks are 64-bit: Yjs clocks go
to 2^53, and 32-bit clocks with an escape table cost three dictionary entries per struct for a client a hostile update
pushes past 2^32 in three structs (section 5).

| field | bytes |
| --- | ---: |
| `Clock`, `OriginClock`, `ROriginClock` (the origins' clients below; -1: no origin) | 24 |
| `Length`, `Client` (index into the client table), `OriginClient`, `ROriginClient` | 16 |
| `Left`, `Right` (handles, -1 none) | 8 |
| `Parent` (index into the type table; -1: a GC struct) | 4 |
| `Info` (keep, countable, deleted, marker, has-redone; the kind in bits 8..15) | 4 |
| `Payload` (an arena offset, a type index, or a free slot's list link) | 8 |
| **total** | **64** |

Beside the slab, by handle: `Refs`, `Keys`, `Redone` (1.6). Origins stay ids: Yjs compares and encodes them as ids, a
split changes which struct holds an origin, and an origin may name a GC struct.

Content kinds (1.7): `String` (the char arena), `Any` (the value arena: the caller's objects, shared), `Deleted` and
`GC` (nothing), `Type` (the type table), `Skip` (staging only, 2.5), and the cold kinds `Format`, `Embed`, `Binary`,
`Json`, `Doc` (their object in `Refs`). A collected item keeps its `Countable` bit when it becomes `Deleted` in place,
as in Yjs.

### 2.2 Pages, and small documents

A first page of 1,024 slots (64 KB) and arena pages of 64 Ki characters (on the large object heap) would make "hello"
about 200 KB. Pages are geometric instead:

* **Slab**: page 0 starts at 1 slot and doubles by copy up to 1,024; pages 1 and on hold 1,024 slots and never move. A
  handle is `page << 10 | index`. While page 0 grows, no `ref Slot` may live across an allocation: slots are taken by
  `ref` only after the last allocation of a method (the test runs start page 0 at one slot, so the first allocations
  copy, and Debug builds poison the old array).
* **Arenas**: page k holds min(16 · 2^k, 32 Ki) characters or min(16 · 2^k, 8 Ki) values, so every page stays below 85
  KB; a run longer than a page gets a page of its own.

Bytes per document (`bench/Ycs.Layout small`,
[`results/2026-11-17-8-14-small.md`](../bench/Ycs.Layout/results/2026-11-17-8-14-small.md); the object layout from the
same bench built at 044bb73,
[`results/2026-11-17-8-12-small-044bb73.md`](../bench/Ycs.Layout/results/2026-11-17-8-12-small-044bb73.md); the machine
shared but the figures deterministic):

| document | object layout | built layout | built / object |
| --- | ---: | ---: | ---: |
| empty, a root text | 1,296 | 1,304 | 1.01 |
| text "hello" (1 struct) | 3,113 | 3,329 | 1.07 |
| 20 characters typed at random positions, 2 deleted (20 structs) | 8,576 | 7,256 | 0.85 |
| 200 typed, 20 deleted (198 structs) | 49,888 | 27,656 | 0.55 |
| 1,000 typed, 100 deleted | 227,216 | 94,696 | 0.42 |
| 5,000 typed, 500 deleted | 1,086,745 | 430,969 | 0.40 |
| 20,000 typed, 2,000 deleted | 4,310,177 | 1,686,786 | 0.39 |
| empty, a root map | 1,184 | 1,176 | 0.99 |
| map, 20 keys | 10,179 | 8,235 | 0.81 |
| map, 1,000 keys | 355,906 | 186,290 | 0.52 |

Many clients (20,000, one map entry each): 665 bytes per client in the object layout, 590 built. The built layout pays
from a few structs; a document of one struct holds 216 bytes more than before: the first slab page is one slot (64
bytes) and the arena's first page 16 characters, and the rest is the slab's and the arena's own objects and the client
and type tables. The collections that a document may not use are made at their first use
(`bench/Ycs.Layout/README.md`, "Where the bytes of a small document went"). There is no separate representation for
small documents.

### 2.3 The arenas

The character arena lives on `Slab` (`Slab.Chars`), the value arena beside it (`Slab.Values`); each is made on first
use. A String or Any slot's `Payload` is the offset of its range, `Length` the range's length. A split gives the right
part `offset + diff` (no copy). Typing at a run's end appends in place when the run ends at its page's used part or
inside its reservation; otherwise the merged run is copied to the write end with as much slack again, kept in a tail
table (`TailTable`: run end to reservation end), so two runs growing alternately copy each other O(log n) times, not
once per keystroke. A run longer than a page has a page of its own, twice its length, and no table entry (a transaction
that types 100,000 characters in one run allocates about 2.4 times what the object layout did, 530 KB against 218 KB,
because that run doubles by copy; the budget row says so, and it is accepted: held memory and time are what the layout
is for).

Garbage is collected content and copied-away runs only. A deleted item that is not collected keeps its kind and its
range: its encoding reads them (gc off, kept items, snapshots).

Compaction is document-wide (`Slab.CompactChars` and `Slab.CompactValues`, started by
`StructStore.CompactIfWorthwhile`). It runs at the end of the outermost batch, beside `Slab.AllowReuse`, for each arena
whose dead elements exceed its live ones and whose garbage made since its last compaction amounts to a largest page and
to the number of slots (`Arena.WorthCompacting`: the walk costs O(slots), so each compaction is paid for by that much
new garbage, whatever it freed); never in the middle of a batch outside the test modes. One walk of the slab's pages
lists the ranges of every slot of the arena's kind, String or Any (a free slot, pending reuse included, holds none; a
deleted but uncollected one is listed). The arena copies the runs of every page less than half live (the write page
included) to a new write end, tells the slab each run's new offset, frees those pages (Debug fills them with a marker
character) and drops the tail table. Only the slots and the tail table hold an offset: the readers of a text
(enumerators, `YText` positions, `ItemTextListPosition`, events) hold handles and read the slot's range in the moment
they use it (`ItemRef.Chars`, `CopyCharsTo`, `WriteCharsTo`, the elements of a String, the encoding of an item; a
String slot holds no object: the arena is the one source of its text). A span of the arena is consumed before any call
that runs user code or ends a batch; `WriteCharsTo` asks the writer for its span first and slices the arena after, from
the item it re-reads (an item that is no longer a live String of its length writes nothing). The offsets the item
operations hold in locals do not live across a call that can compact. Every exit of an Any range clears its values (the
item is collected or freed, a merge or an extension copies the run away, a compaction moves it), so the arena does not
keep the caller's objects alive until a compaction.

The test modes force compaction: `YCS_COMPACT=always|midbatch` compacts after every transaction's cleanup, and mid-batch
at each struct added, each item made, each append to a run and between gc and merge; a store of more than 2,048 slots
compacts every count/64th time (`YCS_COMPACT_EVERY=1` lifts that), so that a test stays linear in its edits;
`YCS_ARENA_PAGE` sets tiny arena pages and `YCS_ARENA_COPY=1` makes every merge copy (`docs/testing.md`).

### 2.4 Handles: lifetime and slot reuse

* **Split**: the left part keeps its handle; the right part gets a new slot. **Merge**: the right part's slot is freed
  (kind `Free`, linked in its page's free list at the batch's end). **GC**: an item whose parent type is collected
  becomes a GC struct in its own slot (`StructStore.ReplaceWithGC`, `ItemRef.BecomeGC`): the slot is rewritten in
  place, the handle stays in the list, and every reference and holder to it stays valid and reads a GC struct. An item
  that only loses its content (`Deleted`) stays, with its handle.
* **Reuse**: a freed slot is reused only when the cleanup queue (`_transactionCleanups`) is empty: after the outermost
  batch, nested transactions from observers and `Update` handlers included. A merge pass of a nested transaction that
  read a reused slot would merge at a position Yjs does not, and the update bytes would differ. The rule is
  defensive: a stricter variant that reuses right after each transaction's merge pass found no reader that fails; one
  unit test pins the policy. A page whose slots are all free is released (1,000,000 merged items would otherwise leave
  64 MB of free slots).
* **No generation counters in Release.** A generation per slot (4 bytes, 6% of a slot) tells that a slot changed; in
  Debug only, a slot has one and an `ItemRef` keeps it (a test asserts that `ItemRef` has two fields in Release,
  `_slab` and `_handle`). What a holder outside the store needs to know is whether the slot still holds *its* struct,
  and "the slot is live and its id is the held id" answers that exactly, because an id names one struct start. So
  every holder that can outlive a free point holds an id (and at most a handle as a hint, validated by that check):
  delete sets, stack items, snapshots, relative positions, `Transaction._mergeStructs`, the public enumerators, and
  `ItemView` (3). Handles without ids are held only by what split and merge keep current: type starts, map entries,
  search markers, the push hint, the clock order. A Debug check after every test transaction walks them: none points
  at a `Free` slot. These holders keep an `ItemHandle`: the `int` handle alone in Release (a test asserts the one
  field), and in Debug the handle with the slot's generation. The slab is the owner's: the document's for a type, the
  list's for the leaves of `ClientStructs`, the marker's own. A read rebuilds the `ItemRef` with that generation, so a
  holder that outlived its struct throws in Debug, and the store check also compares the generation. `YDoc._item`
  stays an `ItemRef`: a subdocument's item is in its parent's slab, which the subdocument does not hold.
* **Type table**: an index is never reused; a collected type's entry is cleared and its `YType` keeps its index, and
  its search markers and `_start` are cleared at collection (`TypeContent.cs`), so a user-held collected `YArray`
  writes no marker flag through a handle that now names another type's item.
* **A collected type keeps its item's facts.** The struct that held a collected type can merge away and its slot be
  reused while user code holds the type (or, for a subdocument, the `YDoc`); a handle read after that names another
  struct. So the facts that survive are copied at collection, before the struct is replaced, and read from the copy:

  | holder | read after collection | kept as |
  | --- | --- | --- |
  | `YType._item` | `Parent`, `IsRoot`; the item's id (relative positions, `WriteItem`'s parent id, `YEvent.Path`); whether it is deleted (`Transaction`, `CanExtendRun`, `Integrate`); its key (`YEvent.Path`); its keep (`KeepItemAndParents`, which continues at `ParentType`); the scope walk of `UndoManager` (`ParentType`) | `_collectedParent`, `_collectedId`, `_collectedParentSub`, `_collectedKeep`; deleted is always true |
  | `YDoc._item` (a subdocument) | the parent document (`Load`, `Destroy`) | `_collectedParentDoc` |

  Whatever sets `_item` again (`YType.Integrate`, `YDoc.SetItem` from `ContentDoc.Integrate`) clears the facts; an
  insert refuses a type or a document that has an item (or a collected one) before any item is made, so `_item` is set
  once, until `YDoc.Destroy` clears it. The rule: code that may meet a collected type asks `IsRoot`, `ParentType`,
  `ItemId`, `ItemParentSub`, `ItemDeleted` or `ItemKeep`, never `_item`. A test switch
  (`YType.PoisonCollectedHandleForTests`, `YCS_POISON=1` for `Ycs.Tests`) replaces the handle after collection with an
  unrelated live item, as a reused slot would; the suite passes with it on.
* **The position of a collected type**: the XML siblings (`NextSibling`, `PrevSibling`, `InsertAfter`) need where the
  type was, not a fact. A collected type holds an id (`_collectedId`), and `YType.CurrentItem` finds the struct that
  holds it now (a type item has length 1): the deleted item its own struct merged into, or, when its parent was
  collected too and the struct is a GC struct, none (the siblings are none, as Yjs' deleted right chain gives, and
  `InsertAfter` throws `ArgumentException`). `InsertAfter` splits that item to end at the id (`GetItemCleanEnd`). No
  slot is pinned: a pin would keep the old struct's links, which name neighbours whose slots are freed and reused in
  turn; an id names one struct start whatever is freed. The deviation from Yjs: its orphaned item keeps the links it had
  when it left the list, ours answers from the store now (a sibling inserted after the merge shows). Answering by id
  is also the only correct way for `InsertAfter`: in yjs 13.6.32 `insertAfter(orphan, x)` corrupts the local list (the
  local doc shows `right` while a peer decoding the update sees `x, right`), so local and remote diverge.

### 2.5 Decoding, and the operations without a document

The decoder writes an update's structs into a `StagingTable` (1.8). A staged struct is not a slot: it has the slot's size
(64 bytes: four 64-bit and eight 32-bit fields) and a similar shape, but its fields mean other things, because it
belongs to no store yet. It has no handles (its neighbours are the structs before and after it in the table), its
client is an index into the table's own client list, not the store's client table, its parent is a root name or the id
of the parent's item (there is no type-table index, and no `YType` exists before adoption), and its kind is
`StagedKind`, which includes `Skip` (a gap `mergeUpdates` writes) and has no Free. `Payload` is an offset into the
table's own character or value buffer for a String or an Any, an index into its object list for a cold kind or a
Type's node name, and -1 for none. The kind, the parent kind and a Type's reference number sit in `Info`.

The table is a set of flat buffers that double when full (`StagedStruct[]`, `char[]`, `object?[]` for values, `object?[]`
for everything else an update holds as an object: parent names, keys, cold objects, type names), not pages: nothing is
addressed by a handle that must stay valid while it grows. A client's structs are one contiguous range in clock order
(`BeginClient`, `RangeOf`, `FindByClock`, which keeps the last hit as its hint). No struct is an object. A table belongs
to one owner (`UpdateReader`, one per thread and open lease; `UpdateOperations` rents others from the thread's pool) and
is reset when it is released, keeping at most 256 KiB of buffers between all the tables kept; `Version` changes at every
reset and truncate, so a reader that kept an index can tell. Indexes, spans and refs are valid until the next add or
reset (Debug poisons the buffers on a reset).

The integration plan, the pending rest, the detached views and the garbage-collection pre-check read the staged fields,
and `ItemRef.Adopt` is where a staged struct gets its slab slot. `UpdateOperations` (`MergeUpdates`, `DiffUpdate`,
`DecodeUpdate`) works on staging tables, so there is one representation and one encoder: `LazyStructWriter` writes a
struct from its staged fields, `ItemRef.Write` from its slot, both through `ItemRef.WriteItemHeader`. A String's
characters are copied twice after the decoder has them (into the table, then into the arena), so a load allocates more
than the document holds: ten loads of the trace's final state allocate 38.1 MiB, against 33.1 MiB on the object layout.

### 2.6 Clients

The client table is a `long[]` and a `Dictionary<long, int>`; a slot names its client by index. Per client the clock
order is one flat leaf until it holds 128 structs (1.3), so a client with one struct costs what it costs on objects.
Measured (`bench/Ycs.Layout small`): a document of 20,000 clients with one map entry each holds 590 bytes per client
(665 on the object layout).

## 3. References: `ItemRef` inside, `ItemView` outside

### 3.1 `ItemRef` (internal)

`internal readonly struct ItemRef : IEquatable<ItemRef>`, with the members of Yjs' `Item` that the library uses (`Id`,
`Length`, `Left`, `Right`, `Deleted`, `Countable`, origins, `Parent`, ...), so that ported code keeps reading like Yjs.
It is (slab, handle) (1.6), and the long-lived holders inside the store keep an `ItemHandle` (2.4).

* **Lifetime**: valid until its struct is merged away or its type collected. Code may hold one across a split (the left
  part keeps its handle) but not across a cleanup's merge pass or a type's collection, except in the holders of 2.4 and
  1.5 that those update.
* **Equality**: the same slab and the same handle, written out.
* **Threads**: as the document: one thread at a time.

### 3.2 `ItemView` (public)

`public readonly struct ItemView : IEquatable<ItemView>`: the document, the id, and an internal hint: the slab handle of
the struct it was made from, valid when its slot is live and holds the id (a detached view holds the staging table, the
struct's index and the table's version instead, 1.8).

* **What it reads**: the struct that holds its id *now*, as if split at the id without splitting it. With S the struct
  that holds the id and o = id.clock - S.clock: `Length` = S.Length - o; `Content` = S's content from o; `LeftOrigin` =
  (client, clock - 1) when o > 0, else S's; `Left` = the view of (client, clock - 1) when o > 0, else S's left;
  `RightOrigin`, `Right`, `Deleted`, `Countable`, `Keep`, `Parent`, `ParentSub` = S's; `Redone` = S's moved by o (as
  Yjs' `splitItem` moves it); `LastId` = S's. `Kind` (`String`, `Any`, `Json`, `Binary`, `Embed`, `Format`, `Type`,
  `Doc`, `Deleted`, `GC`) tells what Yjs' filters test with `instanceof`; a GC struct reads `Parent` null, empty content,
  `Deleted` true.
* **Cost**: a read checks the hint; when it is stale, one lookup by id (40-60 ns).
* **Lifetime**: valid as long as the document, across any split, merge, GC, slab growth or slot reuse: an id names one
  position in its client's clocks forever, and the store never drops a clock it has (a collected item becomes a GC
  struct of the same range). On `default(ItemView)` every member throws `InvalidOperationException`.
* **Equality**: the same document (by reference) and the same id; the hash is the id's.
* **Threads**: as the document; a view read inside a transaction sees the splits made so far.
* **Difference from Yjs**: within a transaction and its observers a view reads what Yjs' item reads. After the cleanup's
  merges it reads the current struct, where Yjs' orphaned item keeps its old fields: for three items a, b, c merged
  into one, `Changes.Added` read afterwards gives "abc", "bc", "c" where Yjs gives "abc", "b", "c". The public
  enumerators (`YArrayBase.EnumerateRaw`, the XML tree walker) hold an id and a hint the same way (1.5).

### 3.3 The public surface

No public member names `Item`, `GC`, `Skip` or `AbstractStruct`; they are not types of the library. The public
members that expose a struct are:

* `ChangesCollection.Added/Deleted` are `ISet<ItemView>`; `YEvent.Adds/Deletes` take an `ItemView`; `YDoc.GcFilter`,
  `YDocOptions.GcFilter` and `UndoManagerOptions.DeleteFilter` take an `ItemView`; `TypeMapEnumerate` is `private
  protected`.
* `DecodedUpdate.Structs` is `IReadOnlyList<DecodedStruct>`: a record (`Id`, `Length`, `Kind` including `Skip` and `GC`,
  `LeftOrigin`, `RightOrigin`, `Parent` as a root name or an id, `ParentSub`, `Content`), detached from any store; the
  store integrates `DecodedUpdate.Raw`. `ItemKind` names the kinds.

`tests/Ycs.Tests/PublicApi.txt` pins the surface (`YCS_ACCEPT_API=1` regenerates it); `docs/api.md` documents it.

## 4. Not done, and why

* A Fenwick tree over positions and an `int[]` of lengths: leaf sums from the starts are as fast and cost nothing per
  struct (the tree is over the leaves' sums, 1.3).
* A full B+tree: two levels hold a client of a million structs in a directory of 8,000-16,000 leaves; revisit if
  measured.
* Generation counters in Release: the id check is exact (2.4); Debug has them (1.6).
* A separate small-document representation: geometric pages cost at most about 400 bytes a document (2.2).
* Inline scalars and a rope per text: every read would box, every offset would shift.
* A struct of arrays or a hot/cold split of the slot: not measured.
* Pooled item objects: they make stale references worse.
* Folding `Keys` into `Refs`: saves nothing (1.6).

## 5. Open items

* **32-bit clocks with an escape table** (12 bytes, 19% of a slot): measure whether the gain is worth an escape that
  hostile updates can force for a whole client.
* **Pending structs as a staging table.** A retry of what waits decodes, plans and encodes all of it again (about 27 ns
  per waiting struct, 10 ms at the default 131,072 structs). Keeping the set-aside structs as a staging table instead
  of an encoded update would skip the encode and decode and roughly halve the constant. Not built: the sample
  server's limit (4,096) caps the retry near 100 µs. Skipping the clients whose `Missing` clock the plan did not pass
  would not help (each update unlocks one struct) and changes semantics. Build it if a server needs the default
  limits.
* **Type-table index reuse.** The table grows by 8 bytes per type ever created. A free list of indexes needs a
  generation to stay safe for a user-held collected type; measure first on a workload that creates and collects many
  nested types.
* **A load copies each String twice** (decoder buffer, table, arena) until the reader writes into the arena directly;
  deferred until measured.
* **The leaf order's cost where nothing splits**: `apply-state-v1` +7%, `apply-state-v2` +9%, `concurrent-20` +10% on
  the object layout; seven variants (the cursor not rewritten when unchanged, a plain binary search in the leaf, no
  pivot among the leaves, the pivot from 8 leaves on, a 32-bit division, the indexer's cursor test inlined, a flat form
  below one leaf) gave nothing beyond 2% ([Compare](../bench/Ycs.Compare/README.md), "The leaf order").
* **The remaining gaps against the object layout** (`trace-replay` +19%, `text-append` +26%, mid-document typing
  +30%, `text-append-1txn-observed-25k` +22%, `map-set-same` +15%, `array-push` +8%): spread over the transaction with
  no phase above 10%; no clean timing round of the whole table exists (the machine was never quiet).
