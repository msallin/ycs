# Design: items as structs in a slab, linked by handles

Status: steps 1 to 7 built (section 1); steps 8 and 9 are the design for the remaining work (sections 2 to 5), revised
after the design review and the review of steps 1 and 2 (2026-11-15). Parity target unchanged: Yjs 13.6.32, the same
content, events and update bytes. No unsafe code: arrays of structs and `ref` to their elements only. Numbers come
from `bench/Ycs.Layout` (the model; README and `results/`), `bench/Ycs.Compare` (README, "After layout steps 1 and 2")
and the two reviews (findings H, M, L: the design review's; N: the review of steps 1 and 2, also "review" alone).

## Summary

Before step 7 an item was a 128-byte object plus a content object (40 bytes for a string, 88 for a single array value),
held by its client's list. Step 7 replaced the item object by a 64-byte slot of a slab, linked to its neighbours by
32-bit handles; the content is still an object beside the slot (1.7). Steps 8 and 9 keep characters and values in
append-only arenas, the rare fields in side tables, and decode into staging tables (4). The model holds 35-45% of
the memory of the object layout on the six workloads measured, and at most about 400 bytes more on the smallest
document (section 2.2). Built before step 7, on the objects: start clocks beside each client's structs, the run-head
index that makes Yjs' marker walks jump within a chain (`array-delete-random` 57 s to 0.9 s), the leaf order of step 3,
which took back the 15-19% steps 1 and 2 cost on the two split-heavy workloads and made the walks' lookups logarithmic,
and the internal reference layer `ItemRef` of step 4, which enumerated the public boundaries and the holders across free
points (1.4). Step 5 gave those holders ids (1.5), step 6 broke the public API once (1.6), and step 7 put every struct of
the store in a slot of a slab, linked by handles, content still an object beside it (1.7).

## 1. What is built

1.1 to 1.6 record each step as it was built, on the objects of its time; 1.7 and sections 2 and 3 describe the code as it
is.

### 1.1 Step 1: start clocks beside the structs

`ClientStructs` (`src/Ycs/Utils/ClientStructs.cs`) replaces the `List<AbstractStruct>` of a client in
`StructStore.Clients`: the structs, their start clocks beside them and the end clock (since step 3 in leaves, 1.3). `FindIndex` (Yjs'
`findIndexSS`, with its pivot, after a look at the last struct) and the galloping `FindIndex(clock, ref hint)` for
in-order lookups search the starts only.

* **Invariant**: the structs are contiguous by clock: the end of position i is the start of i + 1; `End` is the end of
  the last. Every change of the list goes through `ClientStructs` (`Add`, `InsertSplit`, `RemoveMerged`, `Replace`,
  `LastExtended`); the public `Item.SplitItem` is the one path that bypasses it (as before; step 6 removes it).
* **Measured**: `apply-state-v1` 56.7 to 50.2 ms (-11%), `apply-state-v2` -4% (Compare). The model's 1.6-1.9× per
  lookup (`uint` starts beside slots) was not measured on the built `long[]` beside objects.

### 1.2 Step 2: the run-head index

`ClientStructs.RunHeads.cs`, beside the starts. What the design proposed and what was built differ; the code is right:

| proposed | built |
| --- | --- |
| break bits, a Fenwick tree over sums of 64 positions | a flag byte per position (`Break`, `Visible`, `Format`); a `long` sum per block of 512 positions, scanned |
| visible length per position in an `int[]` | the clock length (from the starts) when the flag says visible |
| a split or a merge is O(1) plus a Fenwick update | a split or a merge shifts 17 bytes per later position and updates every later block's sum: O(n) and O(n/512) |
| the walk-left only | the walk-left (`RunHead`), the middle loop (`SeekLeft`), the walk right and the callers' walks from the marker (`SeekRight`) |

* **Invariant** (Yjs' walk-left `while (p.left.id.client === p.id.client && p.left.id.clock + p.left.length ===
  p.id.clock)`): the struct that ends at a struct's clock is its predecessor in its client's order, so position i is
  *linked* exactly when `structs[i] is Item it && it.Left == structs[i - 1]` by reference, and a *break* otherwise
  (position 0, a GC struct, a struct after one). A walk from i ends at the last break at or before i, having subtracted
  the visible countable length of the positions it stepped onto. The flag is taken from the final `Left` after
  conflict resolution, never from origin ids. A live non-countable item (a format) is flagged so that a text's walks
  stop at it.
* **The right walk's rule** (H3): a jump passes only positions whose visible sum stays below the target and
  never the run's last position; Yjs' loop takes the last steps, so when the sum reaches the index exactly at an item's
  end the walk steps on to that item's right neighbour, whichever it is. The review's script (`cdeqpD`) is in
  `RunHeadIndexTests`.
* **Kept current at**: `Add`, the four split sites (`InsertSplit`), both merge passes (`RemoveMerged`: incremental up
  to 32 structs, a rebuild beyond), `ReplaceStruct` (the position and its successor), `ExtendRun` (`LastExtended`),
  and through `StructStore.Refresh` at `Item.MarkDeleted(Transaction)`, `ContentDeleted.Integrate` and the relink of a
  right neighbour in `Item.Integrate`. The index is current at every lookup, mid-transaction included.
* **Short walks stay literal** for 32 steps (`YDoc.LiteralWalkSteps`), so an ordinary edit pays no position lookup;
  a collected type's items fall back to the literal walk (`StructStore.TryFindPosition`). An item continuing a
  collected struct is rejected (`YUpdateException`, as Yjs throws; L1), so a GC struct is never an item's `Left`.
* **Proof**: a recount of every list after every transaction in the unit, property and corpus suites
  (`StructStore.CheckAfterTransactionsForTests`, blocks of 2 there); the `walk-index` property (each lookup through the
  index against Yjs' loops item by item, with run extension on and off); the stale-marker scripts; `interop/markers` and
  `interop/properties` soaks against Yjs (12,000 and 60,000 new scripts in the review, no difference). Sabotages caught:
  a jump past an item that ends at the target, ignoring formats, one too few subtracted, a missed relink or split sum.
* **Measured** (Compare, 53a35de against the branch):

| workload | before | after | change |
| --- | ---: | ---: | --- |
| `array-delete-random` | 57,169 ms | 896 ms | 64× |
| `text-append-1txn-observed` (100,000 characters, one transaction) | 48,888 ms | 790 ms | 62× |
| `apply-updates/ycs` | 259.0 ms | 308.8 ms | +19% |
| `trace-replay` | 247.1 ms | 284.2 ms | +15% |
| `concurrent-20` | 64.6 ms | 66.5 ms | +3%, held 26.9 to 28.1 MiB |

* **Where the regression comes from** (N2, interleaved medians): with no index maintenance at all and the starts
  kept, `apply-updates` is still +11% and `trace-replay` +15%; with `uint` starts +9% and +16%. So the start-clock array
  and the list class are most of it, the index at most 4 points. Each of these workloads makes about 385,000 splits and
  0.4-1.2 million merges, each shifting about 2,400 later structs. Lazy block sums would gain nothing (21 additions a
  split). The leaf order (step 3) removes the shift.

What 1.2 says of blocks of 512 positions holds since step 3 per leaf (1.3): the flags live in the leaves, the sums and
break counts per leaf, and the scans of block sums became a tree.

### 1.3 Step 3: the leaf order

`ClientStructs.Leaves.cs`. A client's structs, starts and flags in leaves of at most 128 positions
(`ClientStructs.LeafCapacity`; `LeafCapacityForTests` sets another in the tests), each with its visible sum, break
count and live-format count; from two leaves on a directory: the leaves, their first starts (the search by clock: the
last leaf, the leaf of the last lookup, then a binary search), their first positions (the search by position, the same
way), a has-break and a has-live-format bit per leaf in 64-bit words, and a Fenwick tree of the leaves' sums.
Positions stay global ints; no caller changed.

* **Invariant**: the leaves in order hold the structs contiguous by clock; every leaf holds 1 to 128 structs (one leaf
  without a directory, 0 before the first `Add`); each directory entry's first start and first position, each leaf's
  sum and counts, the bits and, when not marked stale, the tree equal a recount (`Violations`).
* **Kept current at**: a split shifts within the left part's leaf (a full leaf first splits in two) and adds 1 to the
  later leaves' first positions; a merge removes from one leaf or more, drops emptied leaves, and a leaf at or below a
  quarter then merges with a neighbour when they fit; `Add` opens a new leaf when the last is full; a list of one leaf
  has no directory (it comes at the 129th struct and goes when merges leave one leaf). A change of the leaves inserts
  or removes their bits in place and marks the tree stale; the next read rebuilds it (O(leaves), once per change of the
  leaves, which come every 64 splits or so at the earliest).
* **The walks**: the run's head is the last break in the position's leaf, else in the last leaf before it whose bit
  is set; the run's end the same way forward. `RunHead`'s sum, `SeekRight` and `SeekLeft` go position by position in
  the first and last leaf and jump whole leaves through the tree (the leaf where the sum reaches the target is found by
  one descent), bounded by the run's last leaf and, for a text, the next leaf with a live format. A lookup is O(128 +
  log leaves + leaves/64).
* **Where it differs from the design**: a Fenwick tree of the leaf sums (section 5 rejected one over positions): with
  leaf sums only, a walk over a run of n structs adds n/128 sums, which leaves typing into an observed text in one
  transaction quadratic (n/128 is 4 times the n/512 blocks before); the tree makes it O(log). A has-live-format bit
  per leaf (the walk right of a text must not jump a leaf with one). Leaves of 2, 4 and 8 in the tests, not only 2 and
  4. The suggested commits (the container first, the index second) were one: the per-leaf index is the container's
  bookkeeping, and an intermediate state would have kept block sums over leaves only to delete them.
* **Proof**: the recount after every transaction covers the directory; `Ycs.Tests` (also with `YCS_LEAF_CAPACITY` 2
  and 4), the corpus with leaves of 2, the properties with leaves of 4 (`walk-index` also with 2); merges of 3 to 81
  structs ending before, at and after a leaf boundary, starting at one and centred on one (`RunHeadIndexTests`); the
  placeholder text with leaves of 2; the review's differential fuzz with leaves of 2, 16 and 128 against the walks item
  by item; the soak against Yjs. Sabotages caught by `Ycs.Tests`, and the measurements: bench/Ycs.Compare/README.md,
  "The leaf order".
* **Cost**: against c9f673a `apply-state-v1` +7 %, `apply-state-v2` +9 %, `concurrent-20` +10 %, the price of the leaf
  object and the two-level lookup (a few nanoseconds per `FindIndex`, `Add` and `Refresh`, half of it with a single
  leaf; open question 6). The +21 % first measured on `apply-state-v1` was mostly a gen0 collection that promoted dead
  documents: `UpdateReader.Release` dropped a large decoded list without clearing it, and its array on the large object
  heap kept the loaded document alive until a gen2 collection; it clears every list now (bench/Ycs.Compare/README.md,
  "The leaf order").

### 1.4 Steps 4a and 4b: `ItemRef`

`Structs/ItemRef.cs`, `Structs/ItemRef.Operations.cs`. The library's code holds the structs of the store through
`internal readonly struct ItemRef : IEquatable<ItemRef>`, one field: the object of its time (step 7 made it (slab, handle):
1.7). Yjs' `Item` algorithms moved from
`Item.cs` onto it unchanged but for the reference (integrate, split, merge and its test, delete, mark deleted, gc, keep,
visibility, write, next and prev, run extension); `Item` keeps its fields and public members, and its overrides
delegate. On it since 4a: the struct lists (leaves of `ItemRef[]`), the store's lookups, splits, replacement and delete
set application, the conflict resolution, the transaction's merge list, the delete set's passes, the type collection's
walks, `IContentEx.Integrate`, the search markers and the push hint; since 4b: a type's `_start`, `_item` and `_map`,
the walks of every type, the text's list position, the undo manager, the events' changes and paths, the relative and
absolute positions.

* **Shape**: `default` is Yjs' null (`IsNull`); `==`, `Equals(ItemRef)` and `GetHashCode` (the object's identity hash)
  are written out; `IsItem`, `IsGC`, `AsItem` (what `as Item` gave), `IsSameKind` (the merge rules' `GetType()`
  comparison: on the struct it would name `ItemRef`). An item's members test the type (`as Item`), so on a GC struct
  they throw `NullReferenceException`, as `(str as Item).Member` did; the type test walks faster than a cast (below).
  The members every kind has (`Delete`, `Integrate`, `CanMergeWith`, `MergeWith`, `Write`) dispatch by kind: an item's
  algorithm, else the struct's own override. `ItemRef.New` is Yjs' `new Item(id, left, left && left.lastId, right, right
  && right.id, parent, parentSub, content)`. A decoded object enters the store only through `ItemRef.Adopt`; `Of`, `ToStruct` and
  `ToItem` are called by no method outside `ItemRef` (an IL test, `ItemRefAdoptionTests`, reads every method body and
  also catches a method group, `ldtoken` or `ldvirtftn` that names one).
* **What it costs**: no allocation (every workload allocates the same bytes, below); no box (a test reads every
  method body of the library and fails on a `box` or `constrained.` of an `ItemRef`; the one exception is the
  non-generic `IEnumerator.Current` of the lists' `IEnumerable<ItemRef>`, which nothing calls); no `ValueType.Equals`
  (equality written out, `HashSet<ItemRef>` and `Dictionary<_, ItemRef>` use `IEquatable<ItemRef>`); no interface
  dispatch on it. What remains is the type test on each item member, one compare where the field read
  needed none: the accessors are inlined (`AggressiveInlining`: `Integrate` and the walks read more of them than the JIT
  inlines by its budget, and a call per read cost the observed typing 10-15 %), and the hottest walks read a position's
  item once per step.
* **Traps found on the way**: an `ItemRef` compared with `null` binds to the lifted operator and is always false (seven
  places once the temporary conversions went; CS8073 is an error in `src` now); `(object)type._item ?? type` boxed and
  was never null (the garbage collection check of the integration plan; caught by its test and by the box test);
  `x.GetType()` on the struct names `ItemRef` (the merge rules; `IsSameKind`); `is { }` on the struct always matches.
* **Invariant** (`ItemRefSignatureTests`): no internal field, property, parameter or return names `Item` or
  `AbstractStruct` but in `ItemRef`, the struct classes, the public members, the decoded form (the update reader, the
  pending structs, `UpdateOperations`, the integration plan; until step 9), the members that pass a public filter
  (`Predicate<Item>`, `Func<Item, bool>`) to its call, and three hooks of the property tests.

**The public boundaries.** No public member names `Item`, `GC`, `Skip` or `AbstractStruct` (they are internal) and none
converts: callers read an item through `ItemView` (a store view by id, or the detached view of a decoded struct the
integration plan hands to `GcFilter`, made by `ItemRef.Decoded` in `ItemView.Detached` only), and an update's structs
through `DecodedStruct`. `YDoc.GcFilter` and `UndoManagerOptions.DeleteFilter` take an `ItemView`.

**The holders across a free point (step 5's work list, done: 1.5).** Marked in the code with "ItemRef lifetime
(docs/design-item-layout.md, 3.1)" until step 5:

| holder | what it is held across |
| --- | --- |
| `Transaction._mergeStructs` | the cleanup's merge pass, which reads an entry after merging the entries after it |
| `YArrayBase.EnumerateContent` (the public enumerator of arrays and XML fragments) | the caller's code between two values |
| `YArray.EnumerateList` (`Map`, `ForEach`, `ToArray`, the clone) | the callback of `Map` and `ForEach` |
| `YArrayBase.ForEach` (`YXmlFragment.ForEach`) | the callback |
| `YXmlTreeWalker._currentNode` | the caller's code between two nodes |
| `YText.WriteTo` | the caller's `IBufferWriter<char>` |
| `YArrayBase._searchMarkers` of a collected type | the type's collection, which does not clear them (H5) |

The walks inside a transaction that call user code (`YText.ToDelta`'s `computeYChange` with snapshots, the undo
manager's redo) run where no cleanup can happen, and the other holders of 2.4 (type starts, map entries, search markers
of a live type, the push hint, the clock order) are kept current by split and merge.

**Where the code was not moved mechanically, and why.**

* The decoded form stays objects (2.5, step 9): `IntegrationPlan` keys its dictionaries by `PlanItem`, which is a
  store item (by id) or a decoded one, so the plan never converts an `ItemRef` to an object; the pending structs'
  rest store holds `Skip` structs, tested by kind. `ItemRef.Adopt` is the one place a decoded struct enters the store:
  it resolves the item's references (`ResolveReferences` sets `Left` and `Right` to store `ItemRef`s) and integrates
  it, or adds a GC struct.
* Casts that only fail on a GC struct the code cannot reach (no item's neighbour, run or redone copy is one) are gone;
  the next member read throws `NullReferenceException` where the cast threw `InvalidCastException`:
  `(Item)structs[reached]` in `StructStore.SeekRight` and the marker walk, the undo manager's
  `(Item)GetItemCleanStart(...)`, `LeftOf`'s `(Item)left.Right`, and the casts in the item algorithms (`(Item)right` in
  a merge). No suite reaches one.
* `TypeMapEnumerateValues` iterates the map instead of the public `TypeMapEnumerate`, with the same filter; the text's
  and the XML fragment's inserts call `InsertGenericsAfterReturningLast`, which the protected `InsertGenericsAfter`
  calls.
* Performance, not semantics: the accessors are inlined, `AsItem` keeps its type test's result, the text position's
  steps and the marker walk read the position's item once.

**Proof** (the numbers: bench/Ycs.Compare/README.md, "ItemRef"):

* Every suite as before: `Ycs.Tests` Release 2,906 passed (also with `YCS_LEAF_CAPACITY` 2 and 4), Debug 2,902 and 4
  skipped (the stale-marker scripts against the committed Yjs fixtures among them); the corpus 3,366; the properties 630
  (22 skipped), `walk-index` on 30,000 scripts; the fuzzer 18; the budgets 2; `interop/run.sh`; the soak against Yjs on
  60,000 new scripts (seeds 7,200,001 to 7,260,000): no mismatch.
* Byte for byte against c263413 (the base built beside it): the encoded state, v1 and v2, of the 110 corpus cases with
  their updates applied in order, reversed and shuffled, and the events seen on the way (330 runs); and of 2,000 random
  scripts of the review's probe (five kinds, 3 to 6 clients), compared after every step on content, search markers,
  every marker lookup's item and index and the v1 and v2 state: identical.
* The review's differential fuzz: 6,000 scripts (seeds from 7,300,001; `breath` 1,500, `breathundo`, `breathmany`,
  `small`, `text` 1,000 each, `big` 500), each run item by item and through the index with leaves of 2, 16 and 128, the
  lists recounted after every operation: 0 differing.
* `ItemRefTests` (null, identity, hashing, the members on an item and on a GC struct), `ItemRefBoxingTests` (a sabotage,
  the `(object)type._item ?? type` above put back, fails it), `ItemRefSignatureTests`. `PublicApi.txt` unchanged; no
  unsafe code; no new warning in `src` (two fewer); the Native AOT sample publishes without a warning.

**Measured** (`bench/Ycs.Compare`, `ab.py`, c263413 against the branch, interleaved):

| workload | c263413 | ItemRef | change |
| --- | ---: | ---: | --- |
| `apply-updates/ycs` | 220.3 ms | 222.6 ms | +1 % |
| `trace-replay` | 198.6 ms | 202.1 ms | +2 % |
| `apply-state-v1/ycs`, `-v2/ycs` | 51.8, 56.9 ms | 52.2, 57.9 ms | +1 %, +2 % |
| `array-delete-random`, `array-insert-random` | 165.7, 138.7 ms | 169.8, 137.2 ms | +2 %, -1 % |
| `text-random`, `text-random-100k` | 25.9, 768.7 ms | 24.5, 772.7 ms | noise |
| `concurrent-20`, `map-set-distinct` | 68.0, 40.0 ms | 67.4, 41.6 ms | noise |
| `text-append-1txn-observed`, 25k / 100k | 29.8 / 124.3 ms | 31.9 / 130.9 ms | **+7 % / +5 %** |
| `text-append-1txn-observed`, 200k / 400k | 268.3 / 535.4 ms | 271.2 / 549.9 ms | +1 % / +3 % |

Allocations and held memory are equal to the byte in all of them. The one cost beyond noise is typing into an observed
text in one transaction at 25,000 and 100,000 characters: the marker walk's literal steps test the type of each
neighbour they read (`Item` is not sealed, so a cast or a test is a compare where the object field needed none). It was
8-14 % before the accessors were inlined and the members switched from a cast to a type test; with the walk on an `Item`
variable it was +3 % (an experiment, not kept: it would hold the object step 7 removes). Step 7's slab reads a slot by
index and has no test (1.7); see bench/Ycs.Compare/README.md, "ItemRef", for the experiments.

### 1.5 Step 5: ids across free points

Built on objects, before any slot exists. Nothing that can outlive a cleanup's merge pass or a type's collection holds a
struct without its id; no public API changed (`PublicApi.txt`).

| holder | holds now |
| --- | --- |
| `Transaction._mergeStructs` | `List<ID>`: the id a split or a collection added; the merge pass finds the struct that holds it by the clock |
| `YArrayBase.EnumerateContent`, `YArray.EnumerateList` (`Map`, `ForEach`), `YArrayBase.ForEach` (`YXmlFragment.ForEach`), `YText.WriteTo` | the item it stands on, its id and `StructStore.Departures` read before the caller's code runs; `StructStore.NextAfter` goes on from the item when the count is unchanged, else from the struct that holds the id now (one lookup by id) |
| `YXmlTreeWalker` | the current node, its id and the count, the same way |
| `YArrayBase._searchMarkers` of a collected type | cleared with the push hint when the type is collected (`ForgetList`) |

`Departures` counts the structs that left a list: merged into their left neighbour (`ItemRef.MergeItemWith`) or
replaced by a GC struct (`StructStore.ReplaceWithGC`). It is the enumerators' validity check (`StructStore.NextAfter`); an
`ItemView`'s hint is the slot's handle, which a live slot that holds the id answers (3.2).

* **Open question 3, accepted and pinned**: after a merge an enumerator continues from the struct that holds its id now
  (`EnumeratorLifetimeTests`). That is where Yjs' orphaned item continues unless the list changed right of it since:
  an item that arrives right of the merged struct after the merge is visited (Yjs' orphan skips it, `a,c,d,z` against
  `a,c,d,y,z`). When the id lies in a GC struct the type was collected, and the walk ends: for the list enumerators that
  is where Yjs ends too (everything left is deleted); a tree walker standing in the collected type ends where Yjs'
  orphan walks up through its old parent to the next node (`e1,c` against `e1,c,e3`).
* **Found on the way**: `YXmlFragment.ForEach` called itself (its lambda bound to the public overload): a stack overflow
  on every call; fixed (`fix:`).
* **Proof**: `Ycs.Properties` scripts whose handlers on the document (`afterTransactionCleanup`, `update`,
  `afterTransaction`) and on types make nested transactions (`react` with `on`, `GenOptions.Hooks`: `fixtures/hooks.jsonl`,
  150 scripts recorded from Yjs, and fresh seeds live, `HookScriptTests`), `EnumeratorLifetimeTests` (the values of Yjs
  13.6.32 where they agree), `CollectedTypeMarkersTests`. Sabotage, not committed: a hook that rewrites the `Id` of
  every merged-away and replaced object changes nothing in any suite; with `_mergeStructs` back to objects, 40 of the
  150 hooks fixtures and the live hook seeds fail (so do 163 others, where an observer's transaction or a later
  merge of the pass reads an orphan: 203 of 772 recorded scripts, 626 of 2,916 `Ycs.Tests`); the same with the rewrite
  only while another cleanup is queued, 7 hooks fixtures and 76 other recorded scripts. A hook that also clears an orphan's `Left`
  and `Right` leaves `EnumeratorLifetimeTests` passing; the markers not cleared fail `CollectedTypeMarkersTests`.
* **Measured** (`bench/Ycs.Compare`, `ab.py`, 9e182ef against the branch, 6 processes x 6 timed runs, interleaved, load 1.6
  to 2.7): `apply-updates/ycs` 222.1 / 218.5 ms, `trace-replay` 198.4 / 196.4 ms, `text-append-1txn-observed-25k` 32.3 /
  31.8 ms; allocations and held memory equal to the byte. Noise, so not in the bench README.

### 1.6 Step 6: the public API break

`Item`, `AbstractStruct`, `GC`, `Skip`, `IContent` and the `Content*` classes are internal. `ItemView`, `ItemKind` and
`DecodedStruct` are public (3.2, 3.3); an event's `Adds`/`Deletes`, `ChangesCollection.Added`/`Deleted`, `GcFilter` and
`DeleteFilter` use views; `DecodedUpdate.Structs` lists `DecodedStruct`s, and the store integrates `DecodedUpdate.Raw`.
`GcFilter` receives a store view for a struct in the store, a detached view for a planned one (`IntegrationPlan`).
`TypeMapEnumerate` is `private protected`; `InsertGenericsAfter` is gone (its one caller passed null). Migration:
`docs/api.md`.

### 1.7 Step 7: the slab

`Utils/Slab.cs`, `Utils/TypeTable.cs`, `Utils/ClientTable.cs`, `Structs/ItemHandle.cs`, `Structs/ItemRef*.cs`. Every struct
of a store (an item or a GC struct) is a slot of its document's `Slab`; no `Item` or `GC` object is allocated for one.
The content of the other kinds stays an object, beside the slot; a Deleted slot holds none and a Type slot holds the index of its type. What a reader needs:

* **A slot** is the 64-byte sequential struct of 2.1 with no reference field. Its kind (`Info`, bits 8 to 15) is the
  content's wire reference number: 0 for a GC struct, `Slot.FreeKind` (0xFF) for a slot on a free list. `Payload` holds
  the free-list link of a free slot and the type-table index of a Type slot; a Deleted and a GC slot hold 0 (step 8 adds
  the arena offsets).
* **Pages** (2.2): page 0 starts at 16 slots (`Slab.FirstPageSlotsForTests` changes it for the test runs; 1 makes every
  allocation copy) and doubles by copy up to 1,024; the next pages hold 1,024 and never move. A handle is
  `page << 10 | index`; a read is two array indexes (the slab publishes the pages' arrays by number). Beside each page
  two `object[]` pages, `Refs` (the content) and `Keys` (a map item's key), are allocated on first use, null for a
  free slot; beside the slab, `Redone` (`Dictionary<int, ID>`, flag `HasRedone`) holds the id each redone item names.
  `Keys` is a second page set, not a `KeyedRef` allocation per map item; step 8 may fold it into `Refs`
  (docs/plan-layout-8-9.md, D5).
* **`ItemRef`** is (slab, handle), two fields in Release (a test asserts them); `default` is Yjs' null. Two references
  are equal when they name the same slot of the same slab. A member reads the slot; a write goes to the slot. In Debug
  it also keeps the slot's generation (below). Code that reads several fields of a slot takes one `ref Slot` or
  `ref readonly Slot` after the method's last allocation (`Allocate` may move page 0) and reads it once per step.
* **A client and a parent are indexes.** `ClientTable` numbers the clients of a document and `TypeTable` its types, both
  never reused; a slot names them in four bytes each. A type gets its index when its item is made (`ItemRef.New`, `Adopt`); a collected
  type's entry is cleared and stays (a slot inserted into a collected type puts it back). Each type ever created costs
  one 8-byte entry, null after its collection and never reused: 1,000 nested maps created and collected leave 1,001
  entries. That cost is accepted; a free list of indexes can reuse them later.
* **Free and reuse.** A merge frees the right part's slot (`ItemRef.FreeSlot`: its kind becomes Free, its objects and
  redone id are dropped, its generation changes). It joins the free list only at `Slab.AllowReuse`, which
  `Transaction` calls at the end of the outermost batch, nested transactions of observers and `Update` handlers
  included. The rule is defensive (2.4). A page, but page 0, left with no live slot is released then. Debug poisons a
  slot when it joins the free list and the old array of a grown page 0.
* **A GC struct is the slot of the item it replaces.** `StructStore.ReplaceWithGC` calls `ItemRef.BecomeGC`, which
  rewrites the slot in place (client, clock and length stay; `Info` is Deleted alone; links, origins and parent are
  none; content, key and redone id are dropped), puts the same handle back in the client's list and counts a departure
  (1.5). Every `ItemRef` and `ItemHandle` to it stays valid and reads a GC struct. A GC struct merges into a GC struct
  to its left like any struct, which frees its slot.
* **Holders** keep an `ItemHandle`, not an `ItemRef` (2.4): the leaves of `ClientStructs`, a type's start, item and map
  values (`YType._startHeld`, `_itemHeld`, `_map`), the search markers and the push hint. A type keeps its document's
  slab. `YDoc._item` stays an `ItemRef` (a subdocument's item is in its parent's slab).
* **Generations, Debug only.** Each slot has a generation that a free changes. An `ItemRef` made from a handle or an
  `ItemHandle` keeps the generation of the slot when it was made, and a read through a reference that finds another one
  throws `InvalidOperationException` (`Slab.CheckGeneration`): a holder that outlived its struct fails at the read. In
  Release there is no generation and a read costs the index.
* **The decoded form** (`Item`, `GC`, `Skip`, `IContent`) is no store's: the update reader, the pending rest store and the
  integration plan hold it, and `ItemRef.Adopt` is the one place it enters a store (it resolves the references and
  writes a slot). The merge, split and write of the decoded structs are their own (`Item.cs`, `GC.cs`, `Skip.cs`,
  `LazyStructWriter.cs`, `UpdateOperations.cs`, `EncodingUtils.cs`): no `ItemRef` serves a struct outside a store.
  Step 9 moves the decoded form to staging tables.
* **A collected type** answers its item's facts from a copy, and its XML siblings by its id (2.4).
* **The store check.** `StructStore.CheckForTests` runs after every test transaction: links mirror, the orders are
  contiguous, the free lists are sound, no holder names a freed slot, a type item names its type, and a type's entry
  in the table is never reused. The test runs also set leaves of 2 positions, page 0 to `YCS_SLAB_FIRST` slots (1: every
allocation of it copies) and, with `YCS_POISON=1`, poison the handle of a collected type (2.4).

**Proof**: the check after every test transaction, the suites with page 0 at one slot, the properties of step 5 with real
reuse, the corpus byte for byte. **Measured**: bench/Ycs.Compare/README.md, "Step 7 against the object layout" (held
memory and time) and "The slab, performance pass".

## 2. The layout: built in step 7, to complete in steps 8 and 9

2.1 to 2.4 and 2.6 describe the slab as it is; where a part waits for step 8 or 9 the text says so.

### 2.1 A slot

Sequential layout, no reference field (the garbage collector does not scan the slab). Clocks are 64-bit: Yjs clocks go
to 2^53, and the 32-bit clocks with an escape table of the first draft cost three dictionary entries per struct for a
client a hostile update pushes past 2^32 in three structs (M8); see open question 1.

| field | bytes |
| --- | ---: |
| `Clock`, `OriginClock`, `ROriginClock` (the origins' clients below; -1: no origin) | 24 |
| `Length`, `Client` (index into the client table), `OriginClient`, `ROriginClient` | 16 |
| `Left`, `Right` (handles, -1 none) | 8 |
| `Parent` (index into the type table; -1: a GC struct) | 4 |
| `Info` (keep, countable, deleted, marker, has-redone; the kind in bits 8..15) | 4 |
| `Payload` (step 8: an arena offset or a type index; today a free slot's list link and a Type slot's table index) | 8 |
| **total** | **64** (the model measured 52 with 32-bit clocks) |

Beside the slab, by handle: `Refs` (`object[]` pages allocated on first use: the content object, until step 8 moves
`String` and `Any` out, and for good the cold kinds), `Keys` (a map item's key; a second page set, 1.7), `Redone`
(`Dictionary<int, ID>`, flag `HasRedone`). Origins stay ids: Yjs compares and
encodes them as ids, a split changes which struct holds an origin, and an origin may name a GC struct.

Content kinds: `String` (the char arena), `Any` (the value arena: the caller's objects, shared), `Deleted` and `GC`
(nothing), `Type` (the type table), `Skip` (staging only, 2.5), and the cold kinds `Format`, `Embed`, `Binary`, `Json`,
`Doc` (their object in `Refs`). A collected item keeps its `Countable` bit when it becomes `Deleted` in place, as in
Yjs.

### 2.2 Pages, and small documents

The first draft allocated a page of 1,024 slots (53 KB) at the first struct and arena pages of 64 Ki characters (128
KB, on the large object heap) at the first character: "hello" would hold 194 KB. The design takes geometric pages:

* **Slab**: page 0 starts at 16 slots and doubles by copy up to 1,024; pages 1 and on hold 1,024 slots and never move.
  A handle is `page << 10 | index`. While page 0 grows, no `ref Slot` may live across an allocation: slots are taken
  by `ref` only after the last allocation of a method (tests run with page 0 starting at one slot, so every
  allocation copies, and Debug builds poison the old array).
* **Arenas**: page k holds min(256 · 2^k, 32 Ki) characters or min(64 · 2^k, 8 Ki) values, so every page stays below
  85 KB; a run longer than a page gets a page of its own.

Measured (`bench/Ycs.Layout small`, `results/2026-11-15-small.md`, the model's 52-byte slots): today's document
against the designed one (today's document with its root type and no structs, 1.2 KB, plus what the model's store
holds beyond its empty store).

| document | today | first draft's pages | geometric pages | geometric / today |
| --- | ---: | ---: | ---: | ---: |
| text "hello" (1 struct) | 3.0 KB | 194 KB | 3.2 KB | 1.07 |
| 20 characters typed at random positions, 2 deleted (20 structs) | 8.4 KB | 194 KB | 4.4 KB | 0.52 |
| 200 typed, 20 deleted (198 structs) | 49 KB | 196 KB | 20 KB | 0.40 |
| 1,000 typed, 100 deleted | 221 KB | 202 KB | 75 KB | 0.34 |
| 20,000 typed, 2,000 deleted | 4.5 MB | 1.6 MB | 1.6 MB | 0.35 |
| map, 20 keys | 10.0 KB | 196 KB | 6.1 KB | 0.61 |
| map, 1,000 keys | 354 KB | 289 KB | 174 KB | 0.49 |

The first draft stops paying below about 1,000 structs. With geometric pages the layout pays from about 10 structs;
below that it costs at most 215 bytes more (one struct: 3,176 against 2,961 bytes), 407 with 64-byte slots (12 bytes
more for each of page 0's 16 slots: 1.14 of today; 20 structs 0.57). So there is no separate representation for small
documents.

### 2.3 The arenas

A split gives the right part `offset + diff` (no copy). Typing at a run's end appends in place when the run ends at
the arena's end; otherwise the merged run is copied to the end with geometric slack (twice its length, capped at a
page), so two runs growing alternately copy each other O(log n) times, not once per keystroke (M3). Garbage:
deleted characters, and copied-away runs. Compaction is document-wide: when the dead elements exceed the live ones, one
walk of the slab copies the live runs of every page below half full and frees those pages. A collected item's values
are nulled in the value arena at GC, so it does not keep the caller's objects alive until a compaction.

### 2.4 Handles: lifetime and slot reuse

* **Split**: the left part keeps its handle; the right part gets a new slot. **Merge**: the right part's slot is
  freed (kind `Free`, linked in its page's free list at the batch's end). **GC**: an item whose parent type is collected
  becomes a GC struct in its own slot (`StructStore.ReplaceWithGC`, `ItemRef.BecomeGC`): the slot is rewritten in place,
  the handle stays in the list, and every reference and holder to it stays valid and reads a GC struct. An item that only
  loses its content (`Deleted`) stays, with its handle.
* **Reuse**: a freed slot is reused only when the cleanup queue (`_transactionCleanups`) is empty: after the outermost
  batch, nested transactions from observers and `Update` handlers included (H4). The rule is defensive: a stricter
  sabotage that reuses right after each transaction's merge pass, not at the batch end, found no reader that fails
  (the full Debug suite ran clean, but for the unit test that pins the policy). A page whose slots are all free is
  released (M4: 1,000,000 merged items would otherwise leave 64 MB of free slots).
* **No generation counters in Release.** A generation per slot (4 bytes, 6% of a slot) tells that a slot changed; in
  Debug only, a slot has one and an `ItemRef` keeps it (zero cost in Release: a test asserts that `ItemRef` has two
  fields there, `_slab` and `_handle`). What a holder
  outside the store needs to know is whether the slot still holds *its* struct, and "the slot is live and its id is
  the held id" answers that exactly, because an id names one struct start. So every holder that can outlive a free
  point holds an id (and at most a handle as a hint, validated by that check): delete sets, stack items, snapshots,
  relative positions, `Transaction._mergeStructs` (it holds ids since step 5: the failure it prevents is H4's, where a
  nested transaction's merge pass reads a reused slot and merges at a position Yjs does not, so the update bytes
  differ), the public enumerators, and `ItemView` (section 3). Handles without ids are held only by what split and merge
  keep current: type starts, map entries, search markers, the push hint, the clock order. A Debug check after every
  test transaction walks them: none points at a `Free` slot. These holders keep an `ItemHandle` (7.5b): the `int`
  handle alone in Release (a test asserts the one field), and in Debug the handle with the slot's generation. The
  slab is the owner's: the document's for a type, the list's for the leaves of `ClientStructs`, the marker's own. A
  read rebuilds the `ItemRef` with that generation, so a holder that outlived its struct throws in Debug, and the
  store check also compares the generation. `YDoc._item` stays an `ItemRef`: a subdocument's item is in its parent's
  slab, which the subdocument does not hold.
* **Type table**: an index is never reused; a collected type's entry is cleared and its `YType` keeps its index, and its
  search markers and `_start` are cleared at collection (`TypeContent.cs`), so a user-held collected `YArray` writes no
  marker flag through a handle that now names another type's item (H5).
* **A collected type keeps its item's facts** (7.0e). The struct that held a collected type can merge away and its slot
  be reused while user code holds the type (or, for a subdocument, the `YDoc`); a handle read after that names another
  struct. So the facts that survive are copied at collection, before the struct is replaced, and read from the copy:

  | holder | read after collection | kept as |
  | --- | --- | --- |
  | `YType._item` | `Parent`, `IsRoot`; the item's id (relative positions, `WriteItem`'s parent id, `YEvent.Path`); whether it is deleted (`Transaction`, `CanExtendRun`, `Integrate`); its key (`YEvent.Path`); its keep (`KeepItemAndParents`, which continues at `ParentType`); the scope walk of `UndoManager` (`ParentType`) | `_collectedParent`, `_collectedId`, `_collectedParentSub`, `_collectedKeep`; deleted is always true |
  | `YDoc._item` (a subdocument) | the parent document (`Load`, `Destroy`) | `_collectedParentDoc` |

  Whatever sets `_item` again (`YType.Integrate`, `YDoc.SetItem` from `ContentDoc.Integrate`) clears the facts: a
  document inserted again belongs to the item integrated last. The rule: code that may meet a collected type asks
  `IsRoot`, `ParentType`, `ItemId`, `ItemParentSub`, `ItemDeleted` or `ItemKeep`, never `_item`. A test switch
  (`YType.PoisonCollectedHandleForTests`, `YCS_POISON=1` for `Ycs.Tests`) replaces the handle after collection
  with an unrelated live item, as a reused slot would; the suite passes with it on.
* **The position of a collected type** (7.6): the XML siblings (`NextSibling`, `PrevSibling`, `InsertAfter`) need where
  the type was, not a fact. A collected type holds an id (`_collectedId`), and `YType.CurrentItem` finds the struct that
  holds it now (a type item has length 1): the deleted item its own struct merged into, or, when its parent was
  collected too and the struct is a GC struct, none (the siblings are none, as Yjs' deleted right chain gives, and
  `InsertAfter` throws `ArgumentException`). `InsertAfter` splits that item to end at the id (`GetItemCleanEnd`). No slot
  is pinned: a pin would keep the old struct's links, which name neighbours whose slots are freed and reused in turn, so
  it would need the neighbours pinned too; an id names one struct start whatever is freed. The deviation from Yjs: its
  orphaned item keeps the links it had when it left the list, ours answers from the store now (a sibling inserted
  after the merge shows). Answering by id is also the only correct way for `InsertAfter`: in yjs 13.6.32
  `insertAfter(orphan, x)` does not merely work from stale siblings, it corrupts the local list (the local doc
  shows `right` while a peer decoding the update sees `x, right`), so local and remote diverge. A Debug read through a reference or a holder that outlived its struct throws (the generation check, 1.7), and
  a slot is poisoned when it goes on the free list.

### 2.5 Decoding, and the operations without a document

The decoder writes an update's structs into a staging table of the same slots (plus, per struct, the parent as a root
name or an id, and the `Skip` kind), with its own geometric arenas; a staged struct gets its slab slot when it is
integrated. `UpdateOperations` (`MergeUpdates`, `DiffUpdate`, `DecodeUpdate`) works on staging tables, so there is one
representation and one encoder (M6). Until step 9 the decoded form stays today's objects, copied into slots at
integration.

### 2.6 Clients

The client table is a `long[]` and a `Dictionary<long, int>`; a slot names its client by index. Per client the clock
order (step 3) is one flat leaf until it holds 128 structs, so a client with one struct costs what it costs today in
the order. Measured (`bench/Ycs.Layout small`): a document of 20,000 clients with one map entry each holds 574 bytes per
client today and 348 designed (52-byte slots; 360 with 64; M5).

## 3. References: `ItemRef` inside, `ItemView` outside

### 3.1 `ItemRef` (internal)

`internal readonly struct ItemRef : IEquatable<ItemRef>`, with the members of today's `Item` and `AbstractStruct` that
the library uses (`Id`, `Length`, `Left`, `Right`, `Content`, `Deleted`, `Countable`, origins, `Parent`, ...), so that
ported code keeps reading like Yjs. It is (slab, handle) (1.7), and the long-lived holders inside the store keep an
`ItemHandle` (2.4).

* **Lifetime**: valid until its struct is merged away or its type collected. Code may hold one across a split (the left
  part keeps its handle) but not across a cleanup's merge pass or a type's collection, except in the holders of 2.4
  that those update.
* **Equality**: the same slab and the same handle, written out (no `ValueType.Equals`, which reflects).
* **Threads**: as the document: one thread at a time.

### 3.2 `ItemView` (public)

`public readonly struct ItemView : IEquatable<ItemView>`: the document, the id, and an internal hint: the
slab handle of the struct it was made from, valid when its slot is live and holds the id (a detached view holds the
staging table, the struct's index and the table's version instead: it reads the staged fields while the version
matches, then the store's struct that holds its id, and throws `InvalidOperationException` when the store has none).

* **What it reads**: the struct that holds its id *now*, as if split at the id without splitting it. With S the struct
  that holds the id and o = id.clock - S.clock: `Length` = S.Length - o; `Content` = S's content from o; `LeftOrigin` =
  (client, clock - 1) when o > 0, else S's; `Left` = the view of (client, clock - 1) when o > 0, else S's left;
  `RightOrigin`, `Right`, `Deleted`, `Countable`, `Keep`, `Parent`, `ParentSub` = S's; `Redone` = S's moved by o (as
  Yjs' `splitItem` moves it); `LastId` = S's. `Kind` (`String`, `Any`, `Json`, `Binary`, `Embed`, `Format`, `Type`, `Doc`,
  `Deleted`, `GC`) tells what Yjs' filters test with `instanceof`; a GC struct reads `Parent` null, empty content,
  `Deleted` true. `Content` is what today's `GetContent()` gives for the kind.
* **Cost**: a read checks the hint; when it is stale, one lookup by id (40-60 ns, the model's find-by-id).
* **Lifetime**: valid as long as the document, across any split, merge, GC, slab growth or slot reuse: an id names
  one position in its client's clocks forever, and the store never drops a clock it has (a collected item becomes a GC
  struct of the same range). On `default(ItemView)` every member throws `InvalidOperationException`.
* **Equality**: the same document (by reference) and the same id; the hash is the id's.
* **Threads**: as the document; a view read inside a transaction sees the splits made so far.
* **Difference from Yjs**: within a transaction and its observers a view reads what Yjs' item reads. After the
  cleanup's merges it reads the current struct, where Yjs' orphaned item keeps its old fields: for three items a, b, c
  merged into one, `Changes.Added` read afterwards gives "abc", "bc", "c" where Yjs gives "abc", "b", "c" (H7).
  The public enumerators (`YArrayBase.EnumerateContent`, the XML tree walker) hold an id and a hint the same way: after
  a merge they continue from the struct that now holds the id, which is where Yjs' orphan continues unless the list
  changed right of it since.

### 3.3 The public API break (step 6)

From `tests/Ycs.Tests/PublicApi.txt`, every member that exposes a struct changes:

* `Item` becomes `ItemView` (above); `Marker` and `SplitItem` are dropped (internal state, and a mutation that bypasses
  the store and the merge rules).
* `ChangesCollection.Added/Deleted` become `ISet<ItemView>`; `YEvent.Adds/Deletes` take an `ItemView`;
  `YDoc.GcFilter`, `YDocOptions.GcFilter` and `UndoManagerOptions.DeleteFilter` take an `ItemView`;
  `TypeMapEnumerate` yields `ItemView`s; `YArrayBase.InsertGenericsAfter` becomes `private protected`.
* `AbstractStruct`, `GC`, `Skip`, `IContent` and the `Content*` classes become internal. `DecodedUpdate.Structs`
  becomes `IReadOnlyList<DecodedStruct>`: a record (`Id`, `Length`, `Kind` including `Skip` and `GC`, `LeftOrigin`,
  `RightOrigin`, `Parent` as a root name or an id, `ParentSub`, `Content`), detached from any store. It covers the one
  consumer use known (Plenum's `SharedText.cs`: `is Skip`, `is Item`, `Id`, the origins, `Parent as ID?`; L3).

Announced as `feat!:` (ycs is before 1.0); the commit body gives the migration (`item.Left as Item` becomes
`item.Left`, an `ItemView?`; `e.Changes.Added.Contains(x)` and filter lambdas keep their bodies; `is Skip` becomes
`Kind == ItemKind.Skip`; no replacement for `SplitItem` and `Marker`). `PublicApi.txt` (regenerated with
`YCS_ACCEPT_API=1`) and `docs/api.md` change in the same commit. No other step changes the public API.

## 4. The remaining steps

Each step is one concern, leaves every suite green (unit, properties, corpus, fuzz, budgets, coverage) and can be
reviewed alone. Effects: *measured* where a source is named, otherwise estimates.

| step | concern | effect | risk |
| --- | --- | --- | --- |
| 3 | the leaf order of a client's structs: built (1.3) | `apply-updates`, `trace-replay` below 53a35de (measured) | medium |
| 4a | `ItemRef` in the store core: built (1.4) | none beyond noise, but observed typing in one transaction +5-7 % (measured) | low |
| 4b | `ItemRef` in the types, undo, events: built (1.4) | (with 4a) | low |
| 5 | ids wherever a struct is held across a free point: built (1.5) | none beyond noise (measured) | low-medium |
| 6 | the public API break: built (1.6) | none inside; a view read costs a hint check | medium |
| 7 | the slab, content still as objects: built (1.7) | held memory -15 to -27 %, allocation lower, typing slower (measured) | high |
| 8 | inline content and the arenas | the rest of the saving; no allocation per edit (est.) | medium-high |
| 9 | the decoder and the update operations on staging tables | a load allocates about what it holds (est.) | medium |

**Step 3, the leaf order: built** (1.3). `apply-updates` and `trace-replay` are below 53a35de again; the slab
inherits the order.

**Step 4, `ItemRef`, in two halves: built** (1.4). Every internal use of `Item`/`AbstractStruct` (435 in `src/Ycs`: `StructStore`
87, `Item` 41, `YArrayBase` 39, `YText` 30, `UndoManager` 23, the rest under 16) goes through `ItemRef` wrapping
today's object, with implicit conversions both ways until the end of 4b.

* *Files*: 4a `Utils/StructStore*.cs`, `ClientStructs*.cs`, `Transaction.cs`, `DeleteSet.cs`, `EncodingUtils.cs`,
  `Lazy*.cs`, `Structs/*.cs`; 4b `Types/*.cs`, `UndoManager.cs`, `YEvent.cs`, `RelativePosition.cs`,
  `AbsolutePosition.cs`, `Snapshot.cs`, `PermanentUserData.cs`.
* *Invariant*: behaviour unchanged; after 4b no internal signature names `Item` or `AbstractStruct` outside `ItemRef`
  and the public members of 3.3.
* *Proof*: every suite, byte for byte; a test that walks the internal signatures for the rule above; the budgets
  (`tests/Ycs.Budgets`) catch an `ItemRef` boxed through an interface. The holder table of 2.4 is checked against the
  result.
* *Effect*: none (est.: a one-field struct inlines); measured: none beyond noise but typing into an observed text in one
  transaction, +5-7 % (the type test of each member read, 1.4). *Risk*: low per line, a large diff; review as a diff of
  renames.

**Step 5, ids across free points: built** (1.5). `Transaction._mergeStructs` holds ids (looked up at the merge pass); the public
enumerators hold an id and a hint; a collected type's search markers are cleared. On objects, before any slot exists.

* *Files*: `Transaction.cs`, `YArrayBase.cs`, `YXmlFragment.cs` (the tree walker), `ContentType.cs`, tests.
* *Invariant*: nothing that can outlive a cleanup's merge pass or a type's collection holds a struct without its id.
* *Proof*: new properties against Yjs: nested transactions from observers, `AfterTransactionCleanup` and `Update`
  handlers that split and merge (H4's shape); enumerators held across merges and GC (the rule of 3.2, pinned); a
  collected `YArray`'s `Insert(0, ...)` changes no other type's markers. Sabotage: a test hook that rewrites the `Id`
  of each merged-away object (what a reused slot would show) must change nothing; with `_mergeStructs` back to objects
  the H4 property must fail.
* *Effect*: est. under 1% on `apply-updates` (one lookup per merge entry). *Risk*: low-medium (enumerator semantics in
  a rare case, documented).

**Step 6, the public API break** (3.3), on today's storage.

* *Files*: new `Utils/ItemView.cs`, `Utils/DecodedStruct.cs`; `YEvent.cs`, `YDoc.cs`, `UndoManager.cs`,
  `AbstractType.cs`, `YArrayBase.cs`, `UpdateOperations.cs`, the visibility of `Structs/*`; `PublicApi.txt`;
  `docs/api.md`; the tests that use `Item`.
* *Proof*: `PublicApiTests`; a property that, in every observer call, compares each view's fields with the object's
  (identical inside the transaction); tests for the post-merge rule (a, b, c above), views of GC structs, default
  views, equality and hashing; Plenum's `Plenum.Blazor.Tests` against the new build.
* *Effect*: none inside. *Risk*: medium: the consumers' migration.

**Step 7, the slab: built** (1.7). `ItemRef` is (slab, handle); slots, geometric pages, the client and type tables, `Refs`
and `Keys` pages on first use, per-page free lists with reuse at the batch end and page release; content stays as objects in
`Refs`; the leaf order (1.3) holds handles; a flat order would make a delete 2.2-2.9× slower than today's code
(bench/Ycs.Layout README, "Editing").

* *Files*: new `Utils/Slab.cs`, `Utils/TypeTable.cs`, `Utils/ClientTable.cs`, `Structs/ItemHandle.cs`; `ItemRef*.cs`;
  `StructStore*.cs`, `ClientStructs*.cs`, `Transaction.cs`, `DeleteSet.cs`; `Structs/Item.cs`, `GC.cs` (decoded form
  only now), `Skip.cs`, `ContentType.cs`; `YType.cs` (`_start`, `_item`, `_map` as handles), `YArrayBase.cs`;
  `UpdateOperations.cs`, `EncodingUtils.cs`, `LazyStructWriter.cs` (the decoded form has its own merge, split and write, so
  that `ItemRef` serves the store's structs only).
* *Invariant*: 2.4; the store check after every test transaction (links mirror, orders contiguous and equal to the
  slots' clocks, no holder at a `Free` slot, free lists sound, type entries never reused).
* *Proof*: the check after every test transaction; page 0 starting at one slot in the test runs; the step 5 properties
  with real reuse; corpus, differential, soaks. Sabotages: reuse at transaction end instead of batch end (found no
  reader that fails: the rule is defensive); a `ref Slot` kept across an allocation (the one-slot run); markers not
  cleared (the H5 test); a collected type's handle poisoned (`YCS_POISON=1`).
* *Effect*: measured, bench/Ycs.Compare/README.md, "Step 7 against the object layout": held memory 15 to 27 % below the object layout on the struct-heavy workloads, allocation 15-60 % lower on editing, time +10 to +43 % on typing and observed appends and equal or faster on random-position array and map workloads (044bb73 against 5464252). *Risk*: was
  high (stale handles; the largest diff).

**Step 8, inline content.** `String` and `Any` in the arenas (2.3), `Deleted` and `Type` inline; cold kinds stay in
`Refs`.

* *Files*: new `Utils/Arena.cs`; `Structs/Content*.cs` (string, any, deleted, type become kinds), `Item` accessors,
  `YText.cs`, `YArrayBase.cs`, the GC paths.
* *Proof*: forced compaction after every transaction and in the middle of a batch in a test mode; runs longer than a
  page; two runs growing alternately; content and encoded bytes before and after a compaction; a weak reference shows
  a collected item's value is collectible before any compaction. Sabotage: a compaction that misses one payload.
* *Effect*: held memory the model's 0.35-0.45 of today with 52-byte slots, about 0.40-0.50 with 64-byte slots (est.:
  +12 bytes a struct); no allocation per edit, observed types included. *Risk*: medium-high: an arena bug corrupts
  content; the content checks are strong.

**Step 9, decoding into staging tables** (2.5), and `UpdateOperations` on them.

* *Files*: `UpdateDecoderV1/V2.cs`, `LazyStructReader.cs`, `UpdateReader.cs`, `UpdateOperations.cs`, the integration
  plan in `StructStore.IntegrationPlan.cs`; `DecodedStruct` read from a staging table.
* *Proof*: the corpus byte for byte, the fuzzer, the update-operation differentials against Yjs; the load budgets
  tighten. Sabotage: a `Skip` read as a GC struct in a merge.
* *Effect*: est. a load allocates about what the document holds (the trace: about 1 MiB instead of 3.2); `apply-state`
  1.5-2× is plausible, not measured. *Risk*: medium.

Order: 3 is built; 4a, 4b and 5 pay nothing but are safe and enumerate the holders; 6 once the slab is
decided (it is: open question 2), right before 7; 7 to 9 are the rewrite (7 alone gives about half the saving).

## 5. Not done, and why

* A Fenwick tree over positions and an `int[]` of lengths: leaf sums from the starts are as fast and cost nothing per
  struct (the tree is over the leaves' sums, 1.3).
* A full B+tree: two levels hold a client of a million structs in a directory of 8,000-16,000 leaves; revisit if measured.
* Generation counters in Release: the id check is exact (2.4); Debug has them (1.7).
* A separate small-document representation: geometric pages cost at most about 400 bytes a document (2.2).
* Inline scalars and a rope per text: every read would box, every offset would shift.
* A struct of arrays or a hot/cold split of the slot: measure after step 8, not before.
* Shrunken or pooled `Item` objects: 30% instead of 60%, and pooled objects make stale references worse.

## 6. Open questions

1. **32-bit clocks with an escape table** (12 bytes, 19% of a slot). Recommendation: not in steps 7-9;
   measure afterwards whether the gain is worth an escape that hostile updates can force for a whole client.
2. **When to commit to the slab** (steps 6 to 9: about 4,000 lines): decided, commit after step 5. The model's
   editing measurement (bench/Ycs.Layout README, "Editing"; Yjs' integrate loop and the delete-set loop over slots,
   checked against ycs): with the leaf order of 1.3, integration is 1.4-1.7× and deletion 1.6-4.1× faster than today
   and allocates less on the workloads with conflicts; with a flat order, deletion is 2.2-2.9× slower than today. So
   the slab keeps the leaf order. The integration gain is an upper bound: the model keeps no run-head index.
3. **Enumerators after a merge** (3.2: continue from the struct that holds the id now): decided, accepted and pinned
   in step 5 (1.5); the alternative, throwing on a changed store as .NET collections do, breaks code that edits while
   iterating.
4. **Leaf capacity**: decided, 128 (measured against 64 and 256 in step 3, bench/Ycs.Compare/README.md).
5. **Plenum's client** (`SharedText.cs` reads `Structs` as `Item`/`Skip`). Recommendation: change it in the Plenum
   commit that moves the ycs submodule past step 6, with `Plenum.Blazor.Tests` as the check.
6. **What the leaf order costs where nothing splits** (open, one candidate closed): `apply-state-v1` +7 %,
   `apply-state-v2` +9 %, `concurrent-20` +10 % against c9f673a; per call `FindIndex` 8 to 12 ns and `Add` 9 to 15 ns
   more, half of it even with a single leaf. Six variants (the cursor not rewritten when unchanged, a plain binary
   search in the leaf, no pivot among the leaves, the pivot from 8 leaves on, a 32-bit division, the indexer's cursor
   test inlined) gave nothing beyond 2 %, and a seventh, a flat form below one leaf (the hot operations reading the
   leaf's arrays without the leaf object), gave 0 to +1 % on the three, and at best 1 to 3 % with every client one flat
   leaf: not built. Its cases (clients of at most 128 structs) are not the workloads that lose, whose clients hold
   hundreds to thousands (bench/Ycs.Compare/README.md, "The leaf order"). The slab of step 7 replaced the object lists; what it
   did to these workloads: bench/Ycs.Compare/README.md, "Step 7 against the object layout".
