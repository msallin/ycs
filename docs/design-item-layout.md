# Design: items as structs in a slab, linked by handles

Status: proposed, 2026-11-15. Measured with a model (`bench/Ycs.Layout`, results in its README); not implemented.
Parity target unchanged: Yjs 13.6.32, the same content, events and update bytes. No unsafe code: arrays of structs
and `ref` to their elements only.

## Summary

Today an item is a 128-byte object plus a content object (40 bytes for a string, 88 for a single array value), plus
a reference in its client's `List<AbstractStruct>`. The design puts each struct in a 52-byte slot of a slab, links
slots by 32-bit handles, keeps per client the structs in clock order as two dense arrays (start clocks and handles),
keeps characters and values in append-only arenas, and moves the rare fields (redone ids, cold content objects) to
side tables. Measured on the comparison's documents, the model holds **35-45% of today's memory** (the estimate of "a
third less" was too modest: it is about 60% less), finds a struct by id 1.6-1.9× faster, splits 2.5× faster without
allocating, walks Yjs' marker loop 8× faster as the literal loop, and in about 100 ns (instead of 0.5 ms) with a
run-head index that ends exactly where Yjs' loop ends. Recommendation (section 8): **go with changes**: first the
two separable parts (the start-clock array and the run-head index, on today's objects), then the public API break,
then the slab.

## 1. The representation

### 1.1 Where structs live

* **A slab per document**: `Slot[]` in pages of 1,024 slots (53 KB: below the large object heap, at most one page of
  slack, no copy when it grows). A handle is the slot's index (page = handle >> 10). A slot never moves; its handle is
  its identity for as long as the struct exists.
* **A client table**: `long[] ClientIds` plus `Dictionary<long, int>`; a slot names its client by index.
* **Per client, the clock order**: the client's structs sorted by clock, as `StructStore.Clients` orders them today,
  held as `uint[] Starts` (start clocks, searched) and `int[] Handles`. First as two flat arrays (what the model
  measures: 8 bytes per struct, as a `List` reference today), then as a B+tree of leaves of 128 positions (section
  3.3) so that a split shifts at most a leaf.

Rejected: one array per client whose position is the handle (a split shifts 52-byte slots, 6.5× the bytes `List.Insert`
shifts today, and changes every later handle); a per-client chunked array with (chunk, index) handles (a chunk split
changes handles). The slab plus a separate order index gives stable handles and cheap order changes.

### 1.2 What a slot holds

Sequential layout, 4-byte fields, no reference: the slab is not scanned by the garbage collector.

| field | bytes | today (in `Item`) |
| --- | ---: | --- |
| `Clock` (uint; see 1.4) | 4 | `Id.Clock` 8 |
| `Length` | 4 | 4 |
| `Client` (index into the client table) | 4 | `Id.Client` 8 |
| `Left`, `Right` (handles, -1 none) | 8 | 16 (references) |
| `OriginClient`, `OriginClock` (left origin: -1 none) | 8 | 16 + a flag |
| `ROriginClient`, `ROriginClock` | 8 | 16 + a flag |
| `Parent` (index into the type table; -1: a GC struct) | 4 | 8 (`object`: a type, an id or a root name) |
| `Info` (keep, countable, deleted, marker, has-redone; the content kind in bits 8..15) | 4 | 4 |
| `Payload` (by kind, below) | 8 | `Content` 8 + the content object |
| **total** | **52** | 128 + content + 16 of object header (included in 128) |

Beside the slab, by handle:

* `Refs` (`object[]`, same pages): the key (`parentSub`) of a map item, or a cold content object, or a small
  `KeyedRef(key, content)` for an item that has both. Allocated per page on first use, so a document without maps or
  cold content has none (8 bytes per item less than the model, which keeps a full array).
* `Redone` (`Dictionary<int, ID>`): the few items an undo redid (flag `HasRedone`).
* The clock escape table (1.4).

Origins stay ids (client index, clock), not handles: Yjs compares and encodes them as ids, the item that holds an
origin id changes when it is split, and an origin may point at a GC struct. Parent is a type, never an id or a name,
once a struct is integrated (pending structs, 1.6, carry the other forms).

### 1.3 Content kinds

| kind | where the content is | `Payload` |
| --- | --- | --- |
| `String` | the document's char arena: append-only pages of 65,536 chars; a run never crosses a page | arena offset |
| `Any` | the document's value arena: pages of 16,384 `object` (the boxes the caller gave, shared) | arena offset |
| `Deleted` | nothing (the length is the slot's) | - |
| `Type` | the type table (`TypeEntry`: start handle, map `Dictionary<string, int>`, item handle, root name, the `AbstractType` object) | type index |
| `GC` | nothing | - |
| `Format`, `Embed`, `Binary`, `Json`, `Doc` (cold) | their `IContent` object in `Refs` | - |

A string run's characters: a slice of the arena. A split gives the right part `offset + diff` (no copy). Typing at a
run's end appends to the arena; if the run's characters end at the arena's end the merge only lengthens the slot (the
common case), otherwise the merged run is copied to the end (as `ContentString`'s builder threshold does today). A
deleted item's characters are garbage in the arena; a page whose live characters drop below half is compacted (its
live runs copied to the end, their payloads updated by one walk of the slab; the page is freed). The value arena works
the same way. Rejected: a rope or gap buffer per text type (every item would need an offset into it that shifts with
every insert), and inlining scalars into `Payload` (24 bytes of box less per number, but every read would allocate a
box; possible later for small integers with a cache of boxes).

### 1.4 Clocks above 2^32

Yjs clocks go to 2^53. A slot's clock and the origin clocks are 32-bit; the value `uint.MaxValue` means "in the escape
table" (`Dictionary<(int Handle, byte Field), long>`), and `Starts` becomes `long[]` for a client that has such a
clock. No document measured here (nor any plausible one: 4 billion characters from one client) uses it; it costs one
compare on read. A test with clocks near 2^53 (the corpus has a case) keeps it honest.

### 1.5 Handles across splits, merges, GC and destruction

* **Split**: the left part keeps its handle; the right part gets a new slot (copied, clock and length and payload
  adjusted, left origin = (client, clock - 1)), linked in, inserted into the client's order.
* **Merge** (the transaction cleanup, the delete set's merge pass): the right part's slot is freed onto a free list
  (`Left` links the free slots, kind `Free`). Freed slots are reused only after the transaction that freed them ended.
* **GC** (an item whose parent was collected): the slot is rewritten in place to kind `GC` (parent -1, no payload, refs
  cleared); its handle stays. Adjacent GC structs then merge like items.
* **Invariant**: a handle is held across a transaction boundary only by the structures that merge and split update
  (type starts, map entries, search markers, the push hint, the run-head index, the clock order). Everything that
  outlives a transaction holds ids (delete sets, stack items, snapshots, relative positions, the public views, 2.2).
  A Debug check (as `SearchMarkerViolations` today) walks the slab after each test transaction: no live structure
  points at a free slot.

### 1.6 Pending structs and the integration plan

Pending updates are kept as bytes already (`PendingStructs.Updates`), so no struct outlives an integration unresolved.
The decoder writes the structs of an update into a staging table of the same slots (plus, per staged struct, the
parent as root name or parent id, which a slot cannot hold), and the plan orders the staged structs as today; a staged
struct gets its slab slot when it is integrated. Strings are decoded from UTF-8 straight into the char arena: a load
allocates no string and no item.

## 2. The reference holders, and the public API

### 2.1 Inside the library

| holder | today | designed |
| --- | --- | --- |
| `StructStore.Clients` | `Dictionary<long, List<AbstractStruct>>` | client table + clock orders |
| `AbstractType._start`, `_item`, `_map` | `Item`, `Item`, `Dictionary<string, Item>` | handle, handle, `Dictionary<string, int>` (same entry size: 24 bytes) |
| search markers (`ArraySearchMarker.P`), `_pushHint`, `YXmlFragment._currentNode` | `Item` | handle (markers move on merge as today, `OnMerge`) |
| `Item.Left/Right/Parent/ParentSub` | references, `object`, `string` | slot fields, `Refs` |
| `Transaction._mergeStructs` | `List<AbstractStruct>` | `List<int>` |
| conflict resolution sets (`StructStore.Conflicts`) | `HashSet<AbstractStruct>` | `HashSet<int>` (or a bitmap over the slab span touched) |
| integration plan (`_stack`, `_chain`, `_keyChain`, `_parentDepths`, `_keys`) | items | staged handles |
| delete set merge pass (`TryToMergeWithLefts`) | `List<AbstractStruct>` positions | clock-order positions; a merge frees the right slot |
| `UndoManager` stack items | delete sets (ids) | unchanged; `DeleteFilter` gets a view (2.2) |
| events: `ChangesCollection.Added/Deleted`, `YEvent.Adds/Deletes`, the delta computation | `ISet<Item>`, items | ids internally (`Adds` is a clock compare against the before-state, as in Yjs); views outward |
| `Redone`, `FollowRedone` | `ID?` field | side table |
| snapshots, relative positions | ids | unchanged (they resolve ids through the store) |
| encoders (`WriteStructs`, `Item.Write`) | virtual `Write` on objects | a switch on the slot's kind over the clock order |

### 2.2 The public API: every member that exposes a struct

From `tests/Ycs.Tests/PublicApi.txt`:

* `AbstractStruct` (public abstract class, protected constructor; `Id`, `Length`, `Deleted`), `GC`, `Skip`, `Item`
  (`Left`, `Right`, `Next`, `Prev`, `LastId`, `LeftOrigin`, `RightOrigin`, `Redone`, `Countable`, `Keep`, `Marker`,
  `Parent`, `ParentSub`, `Deleted`, and the mutating `SplitItem(Transaction, int)`).
* `ChangesCollection.Added`, `ChangesCollection.Deleted` (`ISet<Item>`); `YEvent.Adds(AbstractStruct)`,
  `YEvent.Deletes(AbstractStruct)`.
* `YDoc.GcFilter`, `YDocOptions.GcFilter` (`Predicate<Item>`); `UndoManagerOptions.DeleteFilter` (`Func<Item, bool>`).
* `AbstractType.TypeMapEnumerate()` (protected, `KeyValuePair<string, Item>`); `YArrayBase.InsertGenericsAfter(
  Transaction, Item, ...)` (protected).
* `DecodedUpdate.Structs` (`IReadOnlyList<AbstractStruct>`: the structs of an update decoded without a document).
* `IContent` and the public `Content*` classes (reachable only through the above).

The smallest deliberate break (`feat!:`), one commit before the storage changes:

* **`Item` becomes `readonly struct ItemView`**: (document, id). Read-only members as today's (`Id`, `Length`,
  `Deleted`, `Countable`, `Keep`, `Parent` as `AbstractType`, `ParentSub`, `LeftOrigin`, `RightOrigin`, `Redone`,
  `LastId`, `Left`, `Right`, `Next`, `Prev` as `ItemView?`, and `Content` as `IReadOnlyList<object>`); each resolves
  the id through the store (one lookup), so it is valid as long as the document lives, whatever split or merge
  happened since. Equality by id. `Marker` and `SplitItem` are dropped (internal state and a mutation that bypasses
  the transaction's merge rules).
* `ChangesCollection.Added/Deleted` become `ISet<ItemView>`; `YEvent.Adds/Deletes` take an `ItemView`; the filters take
  an `ItemView`; `TypeMapEnumerate` yields `ItemView`s; `InsertGenericsAfter` becomes `private protected` (it takes a
  handle; nobody outside derives from `YArrayBase` usefully, its other constructors being internal state).
* `AbstractStruct`, `GC`, `Skip` and the `Content*` classes become internal; `DecodedUpdate.Structs` becomes
  `IReadOnlyList<DecodedStruct>`: a record (`Id`, `Length`, `Kind`, `LeftOrigin`, `RightOrigin`, `Parent` as a root
  name or an id, `ParentSub`, `Content` as `IReadOnlyList<object>`), which is what that API is used for (inspecting an
  update), detached from any store.

Migration for a user: `item.Left as Item` becomes `item.Left` (an `ItemView?`); `e.Changes.Added.Contains(item)`
works as before with views; a `GcFilter` lambda keeps its body. Code that called `SplitItem` or read `Marker` has no
replacement (neither is meaningful outside the library).

## 3. Algorithms

### 3.1 Find by (client, clock): `findIndexSS`

Today a binary search with Yjs' pivot over a `List<AbstractStruct>`: each probe loads an item object (a cache miss) for
its clock and length. Designed: the same search over `uint[] Starts` (contiguous; the end of position i is the start of
i + 1, so the length is not read). Measured 1.6-1.9× (99 → 59 ns on the trace document, 72 → 38 ns on 100,000 structs).
It is 27% of loading a state today, so about 12% of a load from this alone. Separable: today's store can keep the start
array beside its list (section 7, step 1).

### 3.2 Split and merge

A split is a slot copy, two link updates and an insert into the client's order; no allocation (today: an `Item` and a
spliced content object, 168-260 bytes). Measured 2.5× including the lookup, but still O(n) with the flat order: the
insert shifts 8 bytes per later struct, as `List.Insert` does today (`array-delete-random`: 5.7 → 2.3 µs per split on
a 50,118-struct client). With the B+tree order (leaves of 128 positions, internal nodes with the leaves' first clocks)
a split shifts at most 127 positions: O(log n). A merge frees the right slot and removes its position (the same cost in
reverse). The delete set's merge pass removes positions in one compaction pass per client, as today.

### 3.3 The marker walk: Yjs' walk-left exactly, in O(log n)

Yjs' `findMarker` ends with

```js
while (p.left !== null && p.left.id.client === p.id.client && p.left.id.clock + p.left.length === p.id.clock) {
  p = p.left; if (!p.deleted && p.countable) pindex -= p.length
}
```

**Invariant.** The struct of a client whose clock range ends at `p.id.clock` is unique: it is the struct before `p` in
the client's clock order. So the loop's condition is exactly "`p`'s list-left neighbour is the struct before `p` in its
client's order". Call a position i of a client's order *linked* when that holds for the struct at i, and a *break*
otherwise (position 0 is always a break; a GC struct is never a list neighbour, so a struct after one is a break). From
position i, the loop moves left over linked positions and stops at the first break at or before i: it ends at the
*run head* `h = max { j <= i : j is a break }`, having subtracted the visible countable length of positions [h, i).
Both are functions of the order, the links and the visibility only, so any structure that computes them gives Yjs'
result: the same item and the same index, not an approximation.

**The run-head index** (per client, over the clock order): a break bit per position, a summary bit per 64 bit-words,
and the visible countable length per position summed per 64 positions under a Fenwick tree. `Head(i)` is one masked
word, else a scan of summary words (one per 4,096 positions) and one word; the subtraction is two prefix sums. In the
B+tree order the leaf carries its break bits and its visible sum and the inner nodes carry sums and a has-break flag,
so all of it is O(log n). Maintenance: a split inserts a linked position (bit 0) after the left part, a merge removes
one, an integration sets or clears the bits of the new struct and of its right neighbour (its `Left` changed), a
delete changes one visible length, a run extended in place (`ExtendRun`) adds to one. Each is O(1) plus a Fenwick or
tree update.

Measured on `array-delete-random` (one item of 100,000 values, split by 50,000 single deletes into a chain of 50,118
structs, which is the case that takes 60 s today): 2,000 walks from random items, 24,767 steps on average: today 511 µs
per walk (21 ns a step: pointer chasing over items allocated over time), Yjs' loop over the slab 63.5 µs (2.6 ns a
step), the run-head index 97 ns, with the same end item and index for all 2,000 walks. The walk right from the marker
(Yjs' first loop) crosses the same chain in this document; the same index answers it inside a chain (a Fenwick search
for the position where the remaining index is reached, checked against the chain's end), so `findMarker` as a whole
becomes O(log n) in a chain and stays Yjs' loop across chains. Expected: `array-delete-random` from about 60 s to well
under a second. **Separable**: the invariant needs only the clock order and the list links, which today's store also
has (section 7, step 2).

The reference properties (`markers-invisible`, `merge-order`, `run-extension`) keep comparing against the literal
loop: an internal switch (`RunHeadIndexDisabledForTests`) runs Yjs' loop instead of the index, and the property engine
runs every scenario both ways and compares the markers (`DescribeSearchMarkers`) after every transaction.

### 3.4 Integration and conflict resolution

`Item.Integrate` and `ResolveConflict` become functions over handles: the same steps, the same order (Yjs' loop over
`o = o.right` with the two sets of conflicting items and items before the origin). The sets hold ints. The origins'
lookups are 3.1's. No change of behaviour is possible by construction as long as the steps are ported one to one; the
property engine and the corpus check it.

### 3.5 GC, reads, encoding

GC: a collected item's content becomes kind `Deleted` in place (today a new `ContentDeleted` object, 24 bytes); a
collected type's items become `GC` slots in place. Reads (`ToString`, enumerations) walk `Right` handles over the slab
and copy arena slices: sequential where the list follows the clock order, no virtual calls. Encoding walks each
client's order and switches on the kind; the bytes are those of today's encoder (same structs, same order, same
fields).

## 4. Memory

### 4.1 Bytes per item by kind

Today from `bench/Ycs.Benchmarks sizes`; model measured by `bench/Ycs.Layout` on the same documents; "designed" adds
what the model leaves out (sparse `Refs`: -8 for an item without key or cold content).

| kind | today | model (measured) | designed |
| --- | ---: | ---: | ---: |
| character, one item each | 186 | 73 | 65 |
| array element (an integer) | 258 | 80 | 72 |
| map entry (distinct key, an integer) | 278 | 110 | 110 |
| array element and a deleted one (collected), per pair | 429 | 150 | 134 |

Where the bytes go (model): slot 52, `Refs` 8, clock order 8 (+ up to 8 of doubling slack, 2.5 measured), plus a
character's 2 bytes, an array value's arena reference 8. Not shrunk by the layout: the characters themselves (2 bytes
each in both), map keys and the type's dictionary entry and bucket (about 32 bytes per key, the same `Dictionary` in
both), boxed values (24 bytes per number, shared with the caller in both), and the GC structs (a slot of 68 bytes
where a `GC` object and its list entry are 48: the only kind that grows; collected types are rare and their GC structs
merge).

### 4.2 The four workloads (and two more)

Measured by `bench/Ycs.Layout` (managed heap after a full GC; the documents only), then the expected end state on the
comparison's scale: `bench/Ycs.Compare` counts what the workload's setup keeps alive as well (the inputs, the same for
both libraries), so the end state is its ycs figure minus the saving measured here.

| workload | today, model run | model | saving | ycs today (Compare) | **ycs designed** | YDotNet (Compare) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-random-100k` | 17.8 | 7.0 | 10.8 | 28.4 | **17.6** (16.8 with sparse refs) | 22.5 |
| `concurrent-20` (20 documents) | 26.5 | 11.7 | 14.8 | 26.9 | **12.1** (11.1) | 25.4 |
| `map-set-distinct` | 28.8 | 12.8 | 16.0 | 38.2 | **22.2** | 30.9 |
| `array-insert-random` | 13.5 | 5.0 | 8.5 | 19.5 | **11.0** (10.6) | 18.1 |
| `array-delete-random` | 14.4 | 5.0 | 9.4 | 22.8 | **13.4** | 14.9 |
| `trace-replay` | 2.2 | 1.0 | 1.2 | 2.8 | **1.6** | -13.4 (noise) |

(MiB. The Compare figures are managed + native; ycs' native part, up to 3.3 MiB, is noise around zero.) The designed
ycs holds less than YDotNet on every workload; `text-random-100k` moves from 26% above it to 22-25% below.

A text document loaded from its state holds more than the same document edited, today (`text-random-100k` 22.4 against
17.8 MiB: decoded strings outlive their slices); the designed decoder copies characters into the arena, so a loaded
document holds what an edited one holds.

## 5. Allocation

* **Per edit**: today an insert allocates an `Item` (128) and its content (40-88) unless the run is extended in place
  (`ExtendRun`, unobserved types only), a split another pair, a GC a `ContentDeleted`. Designed: nothing per edit; a
  page of slots (53 KB) per 1,024 new structs, an arena page per 65,536 characters or 16,384 values, the transaction's
  reusable lists as today. The observed-type path (an editor binding, an undo manager) gets the same zero allocations
  as the unobserved one, which today it does not.
* **Per load**: today every struct allocates its item and content and every string its object (items are 40% of a
  load's allocations, `bench/Ycs.Compare` README). Designed: the pages and arenas only, about the memory the document
  holds (the trace: about 1 MiB a load instead of 3.2). Expected effect on `apply-state` (59 ms today against YDotNet's
  39): the allocation share and the 27% of the lookup shrink; 1.5-2× is plausible, not measured (decoding into the slab
  was not modelled).
* **GC pauses**: the slab is free of references, so a full collection scans `Refs` pages and the arenas' value pages
  only, not one object per item.

## 6. Parity: by construction, and what the tests must add

By construction: the encoder writes the same structs in the same order with the same fields (the struct boundaries
are unchanged: splits and merges happen where they happen today); events are computed from the same before-state and
delete set; the integration steps are ported one to one. What can go wrong is bookkeeping, so the tests add:

* **A store consistency check** after every transaction in the unit, property and corpus tests (Debug or a test
  switch): `Left`/`Right` mirror each other; each client's order is contiguous by clock and matches the slots' clocks;
  no live structure (start, map entry, marker, push hint) points at a free slot; the run-head index equals a recount;
  arena offsets are within their pages; a type's length equals its visible countable sum.
* **Handle reuse**: tests that keep `ItemView`s and events across merges, GCs and slot reuse, and read them afterwards.
* **The walk** both ways (`RunHeadIndexDisabledForTests`), as part of `markers-invisible`; `merge-order` and
  `run-extension` unchanged; a property that runs every scenario with run extension off and on and with the index off
  and on and compares states and events byte for byte.
* **Clocks above 2^32** (the escape table) in the corpus and the fuzzer; arena compaction under a fuzzed mix of
  typing and deleting (content and encoded bytes before and after a forced compaction).
* The existing gates unchanged: 100% line and branch coverage of `src`, the Yjs corpus, the differential and property
  tests against Yjs, the budgets (which then tighten: allocation per edit and per load drops).

## 7. Migration, in steps that each keep every gate green

1. **Start clocks beside the list** (a `ClientStructs` class: today's `List<AbstractStruct>` plus a `long[]` of starts,
   kept by every insert, split, merge and replacement; `FindIndexSS` searches it). Independent of everything else.
   About 300 lines. Buys: 1.6-1.9× on lookups, about 10% on `apply-state`.
2. **The run-head index** over `ClientStructs` (break bits, visible sums), kept with splits, merges, integrations,
   deletes and run extensions; `FindMarker`'s walks use it; the internal switch and the property compare it with Yjs'
   loop. About 500 lines plus tests. Buys: `array-delete-random` from about 60 s to under a second (the two walks of
   `findMarker` over the chain are nearly all of its time; the walk right needs the index's chain search too), and
   quadratic typing into an observed type in one transaction becomes linear (expected, not measured). Independent of the
   slab: the invariant holds for objects too.
3. **The public API break** (`ItemView`, `DecodedStruct`, internal `AbstractStruct`/`GC`/`Skip`/`Content*`), with
   today's storage underneath (a view resolves its id through today's store). `feat!:`. About 600 lines.
4. **Handles behind an internal façade**: every internal use of `Item`/`AbstractStruct` goes through an `ItemRef`
   struct (wrapping today's object), so that the next step swaps one implementation. Mechanical, about 2,000 lines
   touched (`StructStore` 62 sites, `YArrayBase` 35, `Item` 32, `YText` 29, `UndoManager` 23, the rest under 15 each).
5. **The slab**: `ItemRef` becomes a handle; slots, client table, type table, `Refs`, the free list; content still as
   objects in `Refs`. About 1,500 lines. Memory: about half of the saving.
6. **Inline content**: the char and value arenas with compaction, `Deleted` and `Type` inline, the decoder writing
   into the arenas. About 800 lines. The rest of the saving and the load allocations.
7. **The B+tree clock order** (leaves of 128, carrying the run-head bits and sums): splits O(log n). About 500 lines.
8. **Sparse `Refs` pages, the 32-bit clocks with the escape table**: about 300 lines, 8 + 8 bytes per item.

Steps 1 and 2 are worth doing alone and first; 3 can ship with them; 4 to 6 are the rewrite and only pay together;
7 and 8 are independent refinements after 5.

## 8. Recommendation

**Go, with changes.** The numbers decide for the layout: it holds 35-45% of today's memory on every workload measured,
which puts ycs below YDotNet everywhere (section 4.2), and removes the per-edit and per-load allocations. The changes
to the plan as estimated:

* Do steps 1 and 2 first, on today's objects: they are small, separable, and fix the two open performance problems
  (the lookup share of loading, and the 60 s marker walk) without waiting for the rewrite, and the rewrite then
  inherits their tests.
* Do not inline scalars or use a rope per text: the arenas are simpler and the measured gain is already larger than
  estimated.
* Keep origins as ids, not handles, and make the public surface id-based (`ItemView`), so that handle reuse can never
  be observed outside the library.

If only part is done: steps 1 and 2 alone buy the lookup speed-up (1.6-1.9× on finding, about 10% of a load) and the
marker walk (about 60 s to under a second on `array-delete-random`), and nothing on memory. The memory saving needs
steps 4 to 6 together (5 alone gives about half of it).

## 9. Risks, and the alternatives rejected

Risks:

* **Stale handles** (a handle kept past a merge that freed its slot, then reused): mitigated by the invariant of 1.5,
  the delayed reuse, the consistency check in every test transaction, and an id-based public surface.
* **Distance from Yjs' code**: today's port reads like Yjs line by line, which makes porting Yjs fixes easy; slot code
  over handles reads differently. Mitigated by porting function by function with Yjs' names and keeping the literal
  loops behind the test switches.
* **Arena garbage** in documents with heavy deletion between compactions (bounded by the half-page rule: at most twice
  the live characters).
* **GC structs grow** (68 against 48 bytes); documents that collect many types hold a little more.
* **The size of the change**: steps 4 to 6 touch most of `src`; each step is green on its own, but the review load is
  large (about 4,300 lines).

Alternatives rejected:

* **Keep objects, shrink them** (redone to a side table, 12-byte origins, the content inline for strings): `Item`
  about 96 bytes plus the header and the list reference; about 186 → 130 per character (30% instead of 60%), and every
  edit still allocates.
* **Pooled, pointer-free `Item` objects**: pooling saves allocation, not size (the headers and references stay), and
  pooled objects make stale references worse, not better.
* **Struct of arrays** (one array per field): a walk reads `Left`/`Right`, `Info`, `Length`, `Client` and `Clock`, five
  cache lines a step instead of one. A hot/cold split is the useful middle: the origins (16 bytes) are read only by
  integration and encoding and could move to a parallel array, leaving a 36-byte hot slot; worth measuring after step 5,
  not before (the memory is the same).
* **Flat per-client slot arrays with positions as handles**: see 1.1.
* **A rope or gap buffer per text, inline scalars**: see 1.3.
