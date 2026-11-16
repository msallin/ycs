# A measured model of the compact item layout

A standalone model of the layout that [`docs/design-item-layout.md`](../../docs/design-item-layout.md) designs: items as
52-byte structs in a slab, linked by 32-bit handles, a client table, per client the structs in clock order (start
clocks and handles), the types in a table, the characters and values in append-only arenas. It does not replace the
library: it walks the struct store of a document ycs built (through `InternalsVisibleTo`) and builds its own store from
it, checks that it holds the same content, and measures what it holds, what three operations cost and what editing
(integration and deletion) costs against today's `YDoc`. Run on 2026-11-15; results in
[`results/2026-11-15.md`](results/2026-11-15.md) and, for small documents,
[`results/2026-11-15-small.md`](results/2026-11-15-small.md); editing on 2026-11-16,
[`results/2026-11-16-edit.md`](results/2026-11-16-edit.md).

```bash
bench/Ycs.Layout/run.sh                  # about 6 minutes; fetches the automerge-paper trace (see below)
FIXTURES=/dir/with/trace.json.gz bench/Ycs.Layout/run.sh
```

The project is in `Ycs.sln`, so CI builds it; CI does not run it. No dependency beyond ycs.

## Method

* **The same documents as the comparison with YDotNet.** The project compiles `bench/Ycs.Compare/Inputs.cs`,
  `Prepared.cs` and `YcsCases.cs`; each workload (`Workloads.cs`) repeats the setup and body of the `YcsCases` method
  of the same name and keeps the documents, and its v1 state is checked byte for byte against the state that
  `YcsCases` method produces (`array-delete-random` only with `LAYOUT_FULL_CHECK=1`: its body takes a minute; its
  content is checked as for the others). The trace is `bench/Ycs.Benchmarks/get-trace.sh`'s, SHA-256 checked.
* **Checks, before a number counts.** For every document: the prototype's links are consistent (each `Right` is
  mirrored by a `Left`, each client's order is contiguous by clock), and its content (text, array values, map entries,
  read by walking the prototype's lists and maps) equals the `YDoc`'s. Every operation's result is compared between the
  two sides (positions found, where a walk ends and the index it computes, the content after the splits). A weak
  reference proves the `YDoc` was collected before the prototype was measured alone. `run.sh` printed "all checks
  passed" for every part.
* **Memory held:** the managed heap after a forced, compacting full collection (`GC.GetTotalMemory(true)`), one
  process per workload. *Today (edited)*: before the setup to after the body, with the inputs created before (so,
  unlike `bench/Ycs.Compare`'s figure, it does not count the inputs the setup creates). *Prototype*: what dropping the
  prototype's store frees, after the `YDoc` was dropped (so it counts the map keys and the values, which both share).
  *Today (loaded)*: a fresh `YDoc` that applied the document's v1 state (`UpdateLimits.Unlimited`).
* **Bytes per item by kind:** `bench/Ycs.Benchmarks sizes`' documents (100,000 items that do not merge), today and
  rebuilt in the prototype; the values and strings the caller passed are shared and counted on neither side.
* **Time:** the best of 5 rounds, `Stopwatch` around the loop only; tiered compilation is off (`TieredCompilation`
  false: Tier-0 code keeps a local alive to the end of its method, which would keep a dropped document in the heap).
* **Machine:** AMD EPYC-Genoa, 8 logical cores, 32 GB, Ubuntu 26.04 (kernel 7.0.0-43), .NET 11.0.100, shared with
  other agents (load average 1.1 to 1.7 during the run).

## Results (2026-11-15)

Memory held, MiB:

| workload | structs | today (edited) | today (loaded from its state) | prototype | prototype / today |
| --- | ---: | ---: | ---: | ---: | ---: |
| `text-random-100k` | 99,998 | 17.8 | 22.4 | 7.0 | 0.39 |
| `trace-replay` | 10,971 | 2.2 | 2.6 | 1.0 | 0.45 |
| `concurrent-20` (20 documents) | 128,560 | 26.5 | 28.0 | 11.7 | 0.44 |
| `map-set-distinct` | 100,000 | 28.8 | 33.9 | 12.8 | 0.44 |
| `array-insert-random` | 49,999 | 13.5 | 13.5 | 5.0 | 0.37 |
| `array-delete-random` | 50,118 | 14.4 | 12.9 | 5.0 | 0.35 |

Bytes per item by kind (per operation of `sizes`):

| kind | today | prototype | of which in the prototype |
| --- | ---: | ---: | --- |
| character, one item each | 186.5 | 73.1 | slot 52 + reference array 8 + clock order 8 (+ its doubling slack 2.5) + character 2 |
| array element (an integer) | 258.5 | 79.7 | 68 + 2.5 slack + value reference in the arena 8 |
| map entry (distinct key, an integer) | 278.3 | 110.1 | 68 + 2.5 + 8 + the type's dictionary entry and bucket (as today: about 32) |
| array element and a deleted one (collected) | 429.0 | 150.2 | two slots 136 + 5 slack + 8 (the deleted one has no content object) |

A slot measured 52.0 bytes (an array of 1,000,000).

Operations:

| operation | document | today | prototype | today / prototype |
| --- | --- | ---: | ---: | ---: |
| find by (client, clock), 1,000,000 random ids (10,971 structs, 1 client) | `trace-replay` | 99.4 ns | 59.3 ns | 1.7× |
| find by (client, clock) (6,428 structs, 21 clients) | `concurrent-20`, client 0's document | 60.8 ns | 38.7 ns | 1.6× |
| find by (client, clock) (99,998 structs, 1 client) | `text-random-100k` | 71.6 ns | 38.0 ns | 1.9× |
| split at a random id inside a struct, with the lookup (7,798 splits of a 10,971-struct client) | `trace-replay` | 1.52 µs | 615 ns | 2.5× |
| the same (20,000 splits of a 50,118-struct client) | `array-delete-random` | 5.68 µs | 2.27 µs | 2.5× |
| marker walk-left, Yjs' loop (2,000 walks from random items, 24,767 steps each) | `array-delete-random` | 511 µs | 63.5 µs | 8.0× |
| marker walk-left, run-head index (the same walks, the same ends and indexes) | `array-delete-random` | 511 µs | 97 ns | 5,280× |

An earlier run of the operations under the same load gave 724 µs / 64.0 µs / 81.8 ns for the walks and 107.5 / 60.2 ns
for the trace lookups: the ratios are stable, the absolute times move by up to 40% with the machine's load.

What the numbers say:

* **The estimate was low.** The prototype holds 35-45% of today's memory for every workload, not two thirds: per item
  the bookkeeping goes from 184 to 71 bytes for a character and from 250 to 78 for an array element. The parts the
  layout does not shrink (a map's dictionary, the keys, the values) are what remains of a map entry's 110 bytes.
* **Loading a state holds more than editing** today (`text-random-100k` 22.4 against 17.8 MiB): a decoded item's text
  is a slice of a string the decoder made. The prototype has no such difference: the characters are copied into the
  arena (not modelled: decoding straight into it, see below).
* **Find by id** is 1.6-1.9× faster on a dense array of start clocks, independent of the rest of the layout.
* **Split** was 2.5× faster against the code before the leaf order (step 3), with no allocation, but O(n): inserting
  into a flat order shifts 8 bytes per later struct. Against today's leaf order the flat prototype is slower (333 ns
  against 636 ns on `trace-replay`, "Editing" below); the slab keeps the leaf order.
* **The walk-left** is 8× faster as Yjs' literal loop over the slab (2.6 ns a step against 21-29 ns: the slots are
  contiguous, the items are not), and constant time with the run-head index, with the same end and index on all 2,000
  walks.

## Small documents and many clients (2026-11-15)

`small` (in `run.sh`; results in [`results/2026-11-15-small.md`](results/2026-11-15-small.md), load 2.2-2.5): many
documents of one shape alive at once, the managed heap divided by their number. The prototype replaces only the struct
storage, so a designed document is today's document of the same root type without structs (the "empty" row) plus what
its store holds beyond the store of that empty document. Two page policies (`Paging` in `LayoutStore.cs`): *fixed*,
the design's first draft (1,024 slots and arena pages of 64 Ki characters or 16 Ki values from the first struct), and
*geometric* (the slab from 16 slots doubling to pages of 1,024; arena pages doubling from 256 characters and 64 values
to 32 Ki and 8 Ki, below the large object heap). The workload tables above keep the slab trimmed to its count.

| document | structs | today | fixed pages | geometric pages | geometric / today |
| --- | ---: | ---: | ---: | ---: | ---: |
| a root text, empty | 0 | 1,232 B | 1,232 B | 1,232 B | 1.00 |
| text "hello" (one insert) | 1 | 2,961 B | 194,216 B | 3,176 B | 1.07 |
| 20 characters typed at random positions, 2 deleted | 20 | 8,392 B | 194,440 B | 4,360 B | 0.52 |
| 200 typed, 20 deleted | 198 | 49,288 B | 196,232 B | 19,592 B | 0.40 |
| 1,000 typed, 100 deleted | 999 | 220,848 B | 202,377 B | 74,937 B | 0.34 |
| 5,000 typed, 500 deleted | 5,000 | 1,127,017 B | 505,482 B | 390,410 B | 0.35 |
| 20,000 typed, 2,000 deleted | 19,999 | 4,484,938 B | 1,623,693 B | 1,557,821 B | 0.35 |
| map, 20 keys (an integer each) | 20 | 10,030 B | 196,232 B | 6,144 B | 0.61 |
| map, 1,000 keys | 1,000 | 354,336 B | 289,289 B | 174,208 B | 0.49 |

A document of 20,000 clients with one map entry each (the updates of 20,000 documents applied to one): 574 bytes per
client today, 351 (fixed) and 348 (geometric) designed.

* **Fixed pages stop paying below about 1,000 structs** ("hello" holds 66 times today's bytes); geometric pages pay
  from about 10 structs and cost at most 215 bytes more below that.
* **Many clients** are cheaper than today: a client's order is two arrays of 4, as today's list.

## Editing (2026-11-16)

`edit` (in `run.sh`; results in [`results/2026-11-16-edit.md`](results/2026-11-16-edit.md), ycs at c263413, load
average 7.7 over the last minute, 36-44 over 5 and 15). `Integrator.cs` is Yjs' `Item.integrate` and
`readAndApplyDeleteSet` on handles, as ycs implements them (`Item.ResolveReferences`, `Item.Integrate`,
`StructStore.ResolveConflict` with its lazily filled sets, `StructStore.ApplyDeleteSet`): the same decisions in the
same order. `Editing.cs` drives both sides.

* **Inputs**, recorded updates, decoded by ycs' `UpdateReader` before each round: `trace-replay`, the v1 update of each
  of the trace's transactions (`YcsCases.Record`), into an empty document; `concurrent-20`, the 20 clients' edits as
  one update each, into a document holding the base text; `random-sync`, five clients typing and deleting (half of the
  inserts near the start) with a random pair syncing both ways every tenth step, their local updates in the order they
  were made (seeded), into an empty document; `map-set-same`, 100,000 sets of one key; `array-delete-random`, its
  50,000 deletes, into the document holding the 100,000 values. `random-sync` exists for the checks: in
  `concurrent-20` the conflict loop never takes the branches where a scanned item's origin is not before the new item
  or is among the conflicting items; in `random-sync` it takes every branch.
* **Integrate**: every struct of the updates, in order, in one transaction (every struct at its client's state,
  checked: offset 0). *Today*: `Item.ResolveReferences` and `Item.Integrate` per struct, what `IntegrationPlan.Execute`
  runs. Included: the origins' lookups and splits, the conflict loop, the links, the parent's start, map entry, length
  and map count, the client's leaf order with its run-head index, the transaction's delete set, changed types and
  merge list. Excluded: decoding (the items and their contents: 200-248 bytes per struct), the plan, the update's
  delete set, the transaction's cleanup (merges, garbage collection, events, update encoding). *Prototype*: the same
  steps on the slab from a staged form converted from the same decode (not timed), the characters and values copied
  into the arenas (timed). Excluded besides: the slab's growth (reserved before the loop; the design allocates pages of
  1,024 slots, 52 bytes per slot here, 64 designed), the run-head index, the merge list.
* **Delete**: the updates' delete sets, in order, in one transaction, after the structs were integrated in a
  transaction of their own (whose cleanup merged them). *Today*: `StructStore.ApplyDeleteSet`'s loop (private, so
  copied into `Editing.cs`) on ycs' own `FindIndexSS` with its hint, `Item.SplitItem`, `ClientStructs.InsertSplit` and
  `Item.Delete`, without its pending branches and the two field writes of `StructStore.SplitItem`. *Prototype*: the same
  loop on the slab. Same exclusions on both sides as above.
* **Two prototype orders**: a client's clock order in leaves of 128 (`LeafOrder.cs`: the leaf order of design section
  1.3 without its run-head index and without leaf merges), and as one flat array (the model's `ClientOrder`, where a
  split shifts every later entry).
* **Checks**, every round, both orders: the root's list (each struct's id, length and deleted flag, in list order),
  each map entry's chain, the type's length and map count, and every client's clock order equal today's; after the
  deletes the content also equals the recording document's (`random-sync`'s: a document that applied the updates
  through `ApplyUpdate`). Sabotages caught: the client comparison of the first case reversed (`concurrent-20`
  fails); each of the three outcomes of the second case forced (`random-sync` fails, the others pass).
* **Time** per struct (integrate) or per range (delete), the best of 5 rounds; **allocation** per struct or range,
  `GC.GetAllocatedBytesForCurrentThread` around the same loop.

| operation | workload | count | today | prototype, leaves of 128 | prototype, one flat order | today / leaves | today, B each | leaves, B each |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| integrate | `trace-replay` | 182,315 structs | 138.9 ns | 96.1 ns | 119.2 ns | 1.4× | 19 | 21 |
| integrate | `concurrent-20` | 5,506 structs | 368.4 ns | 232.9 ns | 236.5 ns | 1.6× | 79 | 28 |
| integrate | `random-sync` | 13,447 structs | 358.2 ns | 207.9 ns | 209.7 ns | 1.7× | 101 | 41 |
| integrate | `map-set-same` | 100,000 structs | 192.3 ns | 132.9 ns | 127.3 ns | 1.4× | 61 | 70 |
| delete | `trace-replay` | 77,463 ranges | 440.8 ns | 127.3 ns | 1.26 µs | 3.5× | 287 | 70 |
| delete | `concurrent-20` | 1,163 ranges | 156.8 ns | 98.8 ns | 97.9 ns | 1.6× | 32 | 35 |
| delete | `random-sync` | 7,534 ranges | 284.9 ns | 136.2 ns | 186.2 ns | 2.1× | 112 | 55 |
| delete | `array-delete-random` | 50,000 ranges | 942.7 ns | 230.6 ns | 2.11 µs | 4.1× | 759 | 61 |

Two more runs in the same minutes moved the ratios by up to 0.4 (one, under a load spike, gave today 484 ns on the
trace's integration).

* **Integration is 1.4-1.7× faster on slots**, with and without conflicts. Today pays the run-head index on every
  `Add` and relink, which the prototype does not model, so the gap is an upper bound; open question 6 puts the leaf
  order's own overhead at 8-15 ns per `FindIndex` or `Add`.
* **Deletion is 1.6-4.1× faster** with the leaf order; most where it splits: today a split of an array item copies the
  right part into a new list (`ContentAny.Splice`, 759 bytes a range on `array-delete-random`) and allocates an item,
  the prototype advances an arena offset in a new slot.
* **The clock order decides deletion, not the slab**: with one flat order the trace's and `array-delete-random`'s
  deletes take 2.9× and 2.2× today's time (the shift of every later entry). The same holds for the split row of the
  operations table, measured before step 3: run again on c263413, today's split takes 333 ns against the flat
  prototype's 636 ns on `trace-replay` (656 ns against 2.30 µs on `array-delete-random`). The slab needs the leaf
  order.
* **Allocation while integrating** is of the same order on both sides (19-101 against 21-70 bytes a struct: today the
  split items, the conflict sets and the leaves; the prototype the arena pages, the leaves and the delete set's list),
  but today's items and contents (200-248 bytes a struct) are made by the decoder, outside the loop; the slab's slots
  (52 bytes here, 64 designed) are made in pages, also outside it.

## What was not modelled

* Editing beyond integration and deletion: merging and the rest of the transaction cleanup, slot reuse, garbage
  collection, undo, events and encoding run only in today's code; their costs in the design are estimates
  (`docs/design-item-layout.md`, sections 3 and 5). Of integration: a struct at an offset (an update overlapping the
  store), pending structs, a nested type's content (integrated as a type entry; deleting one is not supported), a split
  surrogate pair. The delete set as ycs reads it (pending ranges).
* Decoding straight into the slab and the arenas (the allocation of a load), and the arenas' garbage: a deleted
  item's characters stay in the arena until a compaction, which an edited document would carry.
* In the workload tables the slab is trimmed to its count (pages of 1,024 slots have at most one page of slack; the
  small documents are measured with the page policies); the clock orders keep their doubling slack, as a `List` does.
* The design's sparse reference pages (8 bytes per item less for text and arrays), the 32-bit clock escape table, the
  hot/cold split of the origins, the run-head index under edits (the leaf order is modelled for editing, without it
  and without leaf merges).
* The marker walk's right part (Yjs' walk right from the marker): the same index serves it inside a chain; only the
  walk-left was measured, as asked.
