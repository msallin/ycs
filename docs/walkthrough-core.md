# Walkthrough of the core against Yjs 13.6.32 (area A)

## Status

Where each finding stands on branch `integrate` (fix-core, fix-undo and fix-types merged). The
walkthrough below describes the code at 40d7fdb.

| Finding | Status |
| --- | --- |
| F1 `GC.MergeWith` merges anything | fixed in 7f3487e |
| F2 subdocument options | fixed in 7547e92 |
| F3 `EncodeStateAsUpdate` without pending data | fixed in 9fb7ade |
| F4 `redoItem` of an older Yjs | fixed in 3735809 |
| F5 `getMissing` | fixed in 389bbe4 |
| F6 v2 cap of 64 structs per byte | fixed in a0e0904 |
| F7 `Destroy` with subdocuments | fixed in 7547e92 |
| F8 structs of length 0 rejected | open |
| F9 client ids of 2^32 and above | fixed in e833da7; generated ids have 32 bits as Yjs since 0432537 |
| F10 `IterateDeletedStructs` without guards | fixed in a791a61; the snapshot split uses it since the merge 2ab12d4 |
| F11 `meta` that is not an object | fixed in 7547e92 |
| F12 `MaxPendingBytes` of 16 MiB | fixed in 928d947 (no limit by default) |
| F13 nesting deeper than 256 | kept on purpose: bounds the recursion on untrusted input |
| F14 deep observers' `CurrentTarget` and `Path` | fixed in 0701f70 |
| F15 unstable sort of deep events | fixed in 5727865 |
| F16 events of deleted targets | fixed in 0701f70 |
| F17 subdocument's client id | fixed in 7547e92 |
| F18 exception paths of the cleanup | partly: the handlers of one event run as `callAll` and the last exception propagates (0701f70); across the phases the first exception is rethrown, and (b) to (d) are open |
| F19 unstable sort in `MergeUpdates` | fixed in 5727865 |
| F20 `ContentJSON` loses `undefined` | fixed in 3d0c9c0 |
| F21 root type of a pending struct | kept on purpose: no public API lists the root types, so it cannot be observed; the struct reader would need the document |
| F22 `DeleteSet.Read` normalizes | kept on purpose: Yjs writes neither form, and the deletions applied are the same |
| F23 `Get<T>` accepts a subclass | fixed in 7d3f3cf |
| F24 inserting an integrated subdocument again | fixed in 7547e92 |
| F25 `Destroy` of a deleted subdocument | fixed in 7547e92 |
| F26 `ContentDeleted.GetContent()` | fixed in 7d3f3cf; the map snapshot reads and event keys read null for it since 2747d2a |

Side-by-side reading of `src/Ycs` (branch `modernize`, 40d7fdb) against
`interop/corpus/node_modules/yjs/src` (yjs 13.6.32, lib0 beside it). Scope: `structs/*`,
`utils/{StructStore,Transaction,DeleteSet,encoding,updates,ID,isParentOf,Doc}.js` and their C#
counterparts. Nothing in the repository was changed; probes ran against a copy of `src/Ycs`
(Release build) and against the real Yjs with Node 24. Probe sources: `/tmp/ycs-walk-a/node/{gen,gen2,check,zl}.mjs`
and `/tmp/ycs-walk-a/probe/Program.cs`.

"Confirmed by run" means: the scenario was produced with real Yjs, applied to the port, and both
results were compared. "By reading" means the difference follows from the two sources and was not run.

## Summary

| Verdict | Count |
| --- | --- |
| identical | 32 |
| equivalent (different code, same observable behaviour on valid input) | 41 |
| DIFFERS | 12 |
| not ported | 4 |
| port-only (no Yjs counterpart) | 13 |

26 findings: 5 wrong document, 8 rejection/exception on input Yjs accepts, 5 wrong event or order,
4 wrong bytes, 4 cosmetic. The two that matter most are F1 (a merge of updates turns live content
into a GC range; reachable through plain `ApplyUpdate` with out-of-order delivery) and F2 (a
subdocument gets a random guid on the wire).

## Function by function

### structs/Item.js

| JS | C# | Verdict |
| --- | --- | --- |
| `followRedone` | `StructStore.FollowRedone` | identical |
| `keepItem` | `Item.KeepItemAndParents` | identical |
| `splitItem` | `Item.SplitItem` (+ `StructStore.SplitItem` bumps `Version`) | equivalent |
| `isDeletedByUndoStack` | - | not ported (see F4) |
| `redoItem` (6 parameters) | `Transaction.RedoItem(item, redoItems)` | **DIFFERS** - F4 |
| `Item` constructor, `info` bits (keep/countable/deleted/marker) | constructor, `InfoEnum` | equivalent (JS toggles with XOR, C# sets/clears; same values) |
| `Item.getMissing` | `Item.FindMissing` + `Item.ResolveReferences` | **DIFFERS** - F5 |
| `Item.integrate` (conflict loop, relinking, map value, length, delete-if-parent-deleted) | `Item.Integrate` | equivalent (adds `_mapCount` bookkeeping) |
| `next`, `prev`, `lastId` | `Next`, `Prev`, `LastId` | identical |
| `Item.mergeWith` | `Item.MergeWith` + `YArrayBase.MoveMarkersOnMerge` | equivalent |
| `Item.delete` | `Item.Delete` | equivalent (`_mapCount`) |
| `Item.gc` | `Item.Gc` | identical |
| `Item.write` | `Item.Write` | equivalent |
| `readItemContent` / `contentRefs` | `EncodingUtils.ReadItemContent` | equivalent (unknown ref: `YUpdateException` instead of a `TypeError`) |

### structs/GC.js, Skip.js, AbstractStruct.js

| JS | C# | Verdict |
| --- | --- | --- |
| `AbstractStruct` | `AbstractStruct` | identical |
| `GC.integrate`, `GC.write`, `GC.getMissing`, `GC.delete` | same names | identical |
| `GC.mergeWith` | `GC.MergeWith` | **DIFFERS** - F1 |
| `Skip.*` | `Skip.*` | equivalent (`integrate` throws `NotSupportedException`) |

### structs/Content*.js

| JS | C# | Verdict |
| --- | --- | --- |
| `ContentAny` (splice/mergeWith/write/read) | `ContentAny` | equivalent |
| `ContentBinary` | `ContentBinary` | identical |
| `ContentDeleted` | `ContentDeleted` | equivalent (`GetContent` throws instead of `[]`, F26) |
| `ContentDoc` (constructor, copy, integrate, delete, write, read, `createDocFromOpts`) | `ContentDoc`, `YDocOptions.Write/Read` | **DIFFERS** - F2, F11, F24 |
| `ContentEmbed` | `ContentEmbed` | equivalent |
| `ContentFormat` | `ContentFormat` | equivalent |
| `ContentJSON` | `ContentJson` | **DIFFERS** - F20 |
| `ContentString` (splice with the surrogate repair, mergeWith) | `ContentString` | equivalent (string or `StringBuilder`; same code units) |
| `ContentType` (delete, gc, read) | `ContentType` | equivalent (`delete` adds every already-deleted child to `_mergeStructs`, Yjs only those older than the transaction; the extra entries merge nothing new) |

### utils/StructStore.js

| JS | C# | Verdict |
| --- | --- | --- |
| `getStateVector`, `getState` | `GetStateVector`, `GetState` | identical |
| `integrityCheck` | `IntegrityCheck` | equivalent (also fails on pending data) |
| `addStruct` | `AddStruct` | identical |
| `findIndexSS` | `FindIndexSS` | equivalent (integer pivot; range check up front) |
| `find` / `getItem` | `Find` | identical |
| `findIndexCleanStart`, `getItemCleanStart`, `getItemCleanEnd`, `replaceStruct` | same names | identical |
| `iterateStructs` | `IterateStructs` | equivalent (callback may stop the loop) |

### utils/Transaction.js

| JS | C# | Verdict |
| --- | --- | --- |
| `Transaction` constructor | `Transaction.Begin` / `End` (objects reused) | equivalent - reuse reviewed, see "Transaction reuse" below |
| `writeUpdateMessageFromTransaction` | `WriteUpdateMessageFromTransaction` | identical |
| `nextID` | `GetNextId` | identical |
| `addChangedTypeToTransaction` | `AddChangedTypeToTransaction` | equivalent (shared read-only sets) |
| `tryToMergeWithLefts` (chain) | `DeleteSet.TryToMergeWithLeft` (one step) | equivalent - see "Merging" below |
| `tryGcDeleteSet` | `DeleteSet.TryGcDeleteSet` | identical |
| `tryMergeDeleteSet` | `DeleteSet.TryMergeDeleteSet` | equivalent |
| `tryGc` | `DeleteSet.TryGc` | identical |
| `cleanupTransactions` | `Transaction.CleanupTransactions` / `CleanupTransaction` | **DIFFERS** - F14, F15, F16, F17, F18 (phase order itself equals Yjs) |
| `transact` | `YDoc.Transact` | equivalent |

### utils/DeleteSet.js

| JS | C# | Verdict |
| --- | --- | --- |
| `iterateDeletedStructs` | `DeleteSet.IterateDeletedStructs` | **DIFFERS** - F10 |
| `findIndexDS`, `isDeleted` | `FindIndexSS`, `IsDeleted` | identical |
| `sortAndMergeDeleteSet` | `SortAndMergeDeleteSet` | identical |
| `mergeDeleteSets` | `MergeDeleteSets` | identical |
| `addToDeleteSet` | `Add` | identical |
| `createDeleteSetFromStructStore` | `CreateDeleteSetFromStructStore` / `NextDeletedRange` | equivalent |
| `writeDeleteSet` | `Write` | identical |
| - | `WriteFromStore` | port-only (same bytes as build + write) |
| `readDeleteSet` | `Read` / `ReadInto` | equivalent on Yjs output (F22: drops empty ranges, merges a client listed twice) |
| `readAndApplyDeleteSet` (+ `pendingDs` handling in `readUpdateV2`) | `StructStore.ApplyDeleteSet` + `PendingDeletes` | equivalent |

### utils/encoding.js

| JS | C# | Verdict |
| --- | --- | --- |
| `writeStructs` | `EncodingUtils.WriteStructs` | identical |
| `writeClientsStructs` | `WriteClientsStructs` | equivalent |
| `readClientsStructRefs` | `ReadClientStructRefs` / `StructCursor` / `ReadStruct` | **DIFFERS** - F6, F8, F9 (rejections), F21 (root type created later) |
| `integrateStructs` | `IntegrationPlan.Plan` + `Execute` + `EncodeRest` | equivalent (same visiting order, same stack and rest handling, including Yjs' `restStructs.clients.set(client, [item])` overwrite) |
| `writeStructsFromTransaction` | inline in `WriteUpdateMessageFromTransaction` | identical |
| `readUpdateV2` / `readUpdate` | `YDoc.ApplyUpdate` + `StructStore.ApplyUpdate` | equivalent in flow (retry decided on the planned state, same order: structs, rest to pending, update's deletes, waiting deletes, retry); the pending merge inherits F1; F12 |
| `applyUpdate`, `applyUpdateV2` | `ApplyUpdate…` | identical |
| `writeStateAsUpdate` | `WriteStateAsUpdate` | equivalent |
| `encodeStateAsUpdate(V2)` | `EncodingUtils.EncodeStateAsUpdate` | **DIFFERS** - F3 |
| `readStateVector`, `decodeStateVector` | `ReadStateVector`, `DecodeStateVector` | equivalent (clock limit) |
| `writeStateVector`, `encodeStateVector(V2)` | `WriteStateVector`, `EncodeStateVector` | identical |

### utils/updates.js

| JS | C# | Verdict |
| --- | --- | --- |
| `lazyStructReaderGenerator`, `LazyStructReader` | `StructCursor`, `LazyStructReader` | equivalent on Yjs output (same rejections as `readClientsStructRefs`) |
| `LazyStructWriter`, `writeStructToLazyStructWriter`, `flushLazyStructWriter`, `finishLazyStructWriting` | `LazyStructWriter` | equivalent |
| `mergeUpdates(V2)` | `UpdateOperations.MergeUpdates(V2)` / `MergeStructs` | **DIFFERS** - F1, F19 |
| - | `MergeUpdatesTrusted` (groups of 8) | port-only |
| `sliceStruct` | `SliceStruct` | identical |
| `encodeStateVectorFromUpdate(V2)` | same name | identical |
| `parseUpdateMeta(V2)` | same name | identical |
| `diffUpdate(V2)` | `DiffUpdate(V2)` | identical |
| `convertUpdateFormat*` | `ConvertUpdateFormatV1ToV2` / `V2ToV1` | identical |
| `decodeUpdate(V2)`, `logUpdate(V2)` | `DecodeUpdate(V2)`, `DescribeUpdate`, `LogUpdate` | equivalent |
| `obfuscateUpdate(V2)` | - | not ported |

### utils/ID.js, isParentOf.js

| JS | C# | Verdict |
| --- | --- | --- |
| `ID`, `createID`, `compareIDs` | `ID` (struct), `ID.Equals(ID?, ID?)` | equivalent |
| `writeID`, `readID` | `ID.Write`, `ID.Read` | equivalent (client < 2^32, F9) |
| `findRootTypeKey` | `YDoc.FindRootTypeKey`, cached in `AbstractType.FindRootTypeKey` | equivalent |
| `isParentOf` | `UndoManager.IsParentOf` | equivalent |

### utils/Doc.js

| JS | C# | Verdict |
| --- | --- | --- |
| `generateNewClientId` | `YDoc.GenerateNewClientId` | equivalent (31 bits instead of 32) |
| constructor: gc, gcFilter, guid, meta, autoLoad, shouldLoad | `YDocOptions`, constructor | equivalent |
| `collectionid`, `isLoaded`, `isSynced`, `whenLoaded`, `whenSynced`, events `load`/`sync`, `isDestroyed` | - | not ported |
| `load` | `Load` | identical |
| `getSubdocs`, `getSubdocGuids` | `Subdocs` (internal), `GetSubdocGuids` | equivalent |
| `transact` | `Transact` | equivalent |
| `get` (placeholder upgrade, "different constructor") | `Get<T>` | **DIFFERS** - F23 (cosmetic) |
| `getArray/getText/getMap/getXmlElement/getXmlFragment` | same | identical |
| `toJSON` | - | not ported |
| `destroy` | `Destroy` | **DIFFERS** - F7, F25 |
| events `beforeAllTransactions`, `beforeTransaction`, `beforeObserverCalls`, `afterTransaction`, `afterTransactionCleanup`, `update`, `updateV2`, `subdocs`, `afterAllTransactions`, `destroy` | .NET events of the same names | identical order (see F18 for the exception paths) |

### Port-only

| Port-only member | Assessment |
| --- | --- |
| `IntegrationPlan` (plan before integrating) | equivalent to integrating directly; the planned state equals the store's state after each step. No valid update found that it changes |
| `Limits.MaxClock` (2^31 - 1) | no real producer exceeds it (yrs clocks are u32, but a document needs > 2 billion operations of one client) |
| `Limits.MaxClient` (2^32 - 1), var-uints limited to 32 bits | F9 |
| `Limits.MaxDepth` (256) | F13 |
| `Limits.MaxStructsPerByteV2` | F6 |
| zero-length structs rejected | F8 |
| a client listed twice rejected | Yjs keeps only the last section (`clientRefs.set`), so rejecting is safer; Yjs' writers and `mergeUpdates` on descending input never emit it |
| own-client reference must be earlier (`CheckEarlier`) | every origin/parent exists when an item is created, so always true for valid updates |
| `MaxPendingBytes`, `PendingStructs` list, `PendingDeletes`, `HasPending` | F12, F3 |
| `AbstractType._mapCount`, `StructStore.Version`, `IsObserved` gating | equivalent; `_mapCount` is recomputed when a placeholder root is upgraded |
| transaction reuse, `UpdateReader` pooling | reviewed, no leak found (below) |

## Findings

### Wrong document

**F1 - `GC.MergeWith` merges anything; merging updates turns the content after a GC range into GC.**
Severity: wrong document (silent, permanent data loss). Confirmed by run.

- Yjs: `GC.mergeWith(right)` returns false unless `right` is a GC. Port (`Structs/GC.cs`):
  `Debug.Assert(right is GC); Length += right.Length; return true;` - in a Release build the
  assert is gone. `TryToMergeWithLeft` checks the types first, so the store is safe, but
  `UpdateOperations.MergeStructs` calls `currWrite.Struct.MergeWith(curr)` unguarded, as Yjs does.
- Scenario (client 7): `m.set('a', new Y.Array)`, `arr.insert(0,[1,2,3])`, `m.delete('a')`, then
  `text.insert(0,'hello')`. `u1 = AQIHACEBAW0BYQEAAwEHAQAE` (Item@0, GC@1+3),
  `u2 = AQEHBAQBAXQFaGVsbG8BBwEABA==` (Item@4 "hello").
  `Y.mergeUpdates([u1,u2])` = `AQMHACEBAW0BYQEAAwQBAXQFaGVsbG8BBwEABA==`, text "hello".
  `UpdateOperations.MergeUpdates` = `AQIHACEBAW0BYQEACAEHAQAE` (Item@0, **GC@1+8**), text "".
- The same happens inside `ApplyUpdate`, because pending updates are merged with the same code.
  Three valid Yjs updates delivered out of order: `B = AQEHBAQBAXQFaGVsbG8BBwEABA==`,
  `A = AQEHAQADAQcBAAQ=` (GC@1+3), `C = AQEHACcBAW0BYQAA` (Item@0). Yjs after B, A, C: "hello".
  Port after B, A, C (and A, B, C): "", state `Item@0, GC@1+8`, `HasPending == false`; the state
  vector claims clocks 4..8, so no peer will ever resend them.
- Fix: `if (right is not GC) return false;` in `GC.MergeWith` (and keep the assert out). Add the
  two scenarios as tests; run the tests in Release as well as Debug.

**F2 - A subdocument's options are written with `gc`, a random `guid` and `autoLoad`; Yjs peers then see another guid.**
Severity: wrong document (peers disagree on which subdocument an item refers to) and wrong bytes. Confirmed by run.

- Yjs `ContentDoc.write`: the guid, then `opts` holding only `gc: false`, `autoLoad: true`, `meta`
  when they differ from the defaults (normally `{}`). `createDocFromOpts = (guid, opts) => new Doc({ guid, ...opts, … })`:
  a `guid` inside `opts` **overrides** the item's guid.
- Port: `ContentDoc` creates `Opts = new YDocOptions()` (whose `Guid` defaults to a new random
  guid) and `YDocOptions.Write` always writes `{ gc, guid: Opts.Guid, autoLoad }`. The comment in
  `YDocOptions.Read` ("Yjs ignores 'guid' in the options as well") is wrong.
- Scenario: Yjs writes `d.getMap('m').set('s', new Y.Doc({guid:'my-sub-guid'}))` =
  `AQELACkBAW0BcwtteS1zdWItZ3VpZHYAAA==`. The port applies it and `EncodeStateAsUpdate()` gives
  `AQELACkBAW0BcwtteS1zdWItZ3VpZHYDAmdjeARndWlkdyRjZjMy…` ; Yjs applying that sees
  `subdoc.guid == 'cf325a8c-6a35-4ad8-9288-2eb2f8898f7a'`. Same for a subdocument created in the port
  (`Guid = "ycs-sub-guid"` arrives in Yjs as a random guid). A port peer reading the same bytes keeps
  the real guid, so port and Yjs peers also disagree with each other.
- Fix: write exactly what Yjs writes (`gc` only when false, `autoLoad` only when true, `meta` only
  when not null, never `guid`); when reading, let a `guid` in the options override, as Yjs does.

**F3 - `EncodeStateAsUpdate` leaves out pending structs and pending deletions (the known difference).**
Severity: wrong document after a persistence round trip; wrong bytes otherwise. Confirmed by run.

- Yjs merges `store.pendingDs` and `diffUpdateV2(pendingStructs.update, sv)` into the result.
- Scenario (client 20): `u1 = AQEUAAQBAXQDb25lAA==` ("one"), `u2 = AQEUA4QUAgQgdHdvAA==` (" two"),
  `u3 = AAEUAQAB` (delete clock 0). A server applies u2, u3 (both pending), persists
  `encodeStateAsUpdate`, restarts from it, then receives u1. Yjs: persisted
  `AQEUA4QUAgQgdHdvARQBAAE=`, final text "ne two". Port: persisted `AAA=`, final text "one" (without
  the restart: "ne two").
- Consequences: (1) a server that stores snapshots and drops applied updates loses what was
  pending; for structs a later sync step 2 from a client that still has them repairs it (the
  server's state vector does not cover them), for a pending **deletion** only a full resync does
  (delete sets travel only in updates and full states); (2) a client syncing from such a server
  does not get the pending part until the missing update arrives; (3) `EncodeStateAsUpdate` is not
  byte-equal with Yjs while something is pending. `HasPending` lets the caller notice, not recover.
- Fix: append them as Yjs does (the pending remainders are already v2 updates; `DiffUpdateV2` and
  `MergeUpdates` exist; convert to v1 for the v1 encoder), after F1 is fixed.

**F4 - `redoItem` is the algorithm of an older Yjs.**
Severity: wrong document after undo/redo. Confirmed by run (one case); the rest by reading.

- Yjs 13.6.32 `redoItem(transaction, item, redoitems, itemsToDelete, ignoreRemoteMapChanges, um)`:
  for a map entry with a newer value to its right it walks right only over items that are redone,
  in `itemsToDelete`, or deleted by the undo/redo stacks, and gives up (returns null) otherwise;
  without a right item it uses `parentType._map.get(parentSub)`; it drops a left item of another
  parent (#757); the parent is resolved first and only followed through `redone` when deleted.
- Port (`Transaction.RedoItem(item, redoItems)`): walks to the end of the chain and gives up only
  when it meets an item of another **client**; no `itemsToDelete`, no stacks, no
  `ignoreRemoteMapChanges`, no cross-parent check. `UndoManager` (outside this area) is equally old:
  it sets `result = stackItem` even when nothing changed, where Yjs pops the next stack item.
- Scenario (client 3): `transact(() => m.set('a',1), 'x')`; `um = new UndoManager(m)`;
  `m.delete('a')`; `transact(() => m.set('a',2), 'x')` (untracked origin, same client); `um.undo()`.
  Yjs: `m.get('a') === 2` (the redo conflicts with the newer value). Port: `1`.
- Fix: port `redoItem`, `isDeletedByUndoStack` and the `popStackItem` loop of 13.6.32.

**F5 - `getMissing`: an item next to a GC struct, or between two parents, is resolved as in an old Yjs.**
Severity: wrong document on structurally odd (hand-made) updates only; no valid Yjs history was found
that reaches it (children of a collected type all become GC together). Confirmed by run.

- Yjs: `if (left is GC || right is GC) parent = null; else if (!parent) { left is Item ? left's parent : right is Item ? right's parent }`.
  Port (`Item.ResolveReferences`): sets `Parent = null` for the GC case and then falls into
  `if (Parent == null)`, which takes the parent of the other, non-GC neighbour; and it asks the
  **right** neighbour first.
- Scenario a: base `AQMBACEBAW0BYQEAAQgBAXICdwFMdwFSAQEBAAI=`, then the crafted item
  `AQECAMgBAQEDAXcBWgA=` (client 2, origin (1,1) = GC, right origin (1,3) = an item of root array
  `r`). Yjs integrates a GC struct (state `AgECAAABAw…`). The port integrates a live item with
  parent `r` that is in the store but not linked into the list (state `AgECAEgBAwF3AVoD…`,
  `r.Length` counts it): the two states differ and stay different.
- Scenario b: base `AQIBAAgBAXABdwFhCAEBcQF3AWIA` (p=[a], q=[b]), crafted `AQECAMgBAAEBAXcBWgA=`
  (origin in p, right origin in q). Yjs: parent p, `p.length == 2`, `q.length == 1`. Port: parent
  q but linked after `a` in p's list: `p.Length == 1` with two items, `q.Length == 2` with one.
  `docs/api.md` says such updates "are integrated as Yjs integrates them".
- Fix: `if (Left is GC || Right is GC) Parent = null; else if (Parent == null) { left first, then right } else if (Parent is ID) …`;
  adjust `IntegrationPlan.ParentDepth` to the same order.

### Rejection or exception on input Yjs accepts

**F6 - The v2 cap of 64 structs per byte rejects ordinary documents.**
Severity: rejection of valid input (`ApplyUpdateV2`, `MergeUpdatesV2`, `DiffUpdateV2`, `ConvertUpdateFormatV2ToV1`, …). Confirmed by run.

- A deleted item that cannot merge with its neighbour costs **zero** bytes in v2: info, origin
  client, origin clock difference and length are all run-length encoded. Items inserted at the front
  of a list are such items (right origin = the previous item, no left origin).
- Scenario: 20,000 x `yarray.insert(0, [x])`, then `yarray.delete(0, 20000)`:
  `Y.encodeStateAsUpdateV2` is 42 bytes for 20,000 structs
  (`AAAESZ6cAQAFAAOcnAEDAQBBAwFhAQEBAARBnpwBAaCcAQABCQEAn5wB`). Port: "The update holds more
  structs than its size allows (6784)". A list used as a stack (insert at 0, delete the oldest, three
  live entries) gives 52 bytes for 20,000 structs and is rejected as well. So any list that is
  filled from the front and trimmed fails once it has seen about 6,500 entries.
- Fix: drop the per-byte ratio. The amplification is a property of the format; bound the absolute
  number of structs per update with an option (default in the millions) or not at all.

**F7 - `YDoc.Destroy()` throws when the document has subdocuments.**
Severity: exception. Confirmed by run (`InvalidOperationException: Collection was modified`).

- Yjs: `array.from(this.subdocs).forEach(subdoc => subdoc.destroy())`. Port: `foreach (var sd in Subdocs) sd.Destroy();` -
  the subdocument's `Destroy` runs a transaction on the parent whose cleanup adds to and removes
  from `Subdocs`. Fails with one subdocument, created locally or read from an update.
- Fix: iterate a copy.

**F8 - Structs of length 0 are rejected; Yjs integrates them.**
Severity: rejection of input Yjs accepts. Confirmed by run. No current producer found: the public
API of 13.6.32 creates none (`insert(0,'')`, `applyDelta([{insert:''}])`, `insert(0,[])`, `new Y.Text('')` were tried).

- Yjs integrates when `offset === 0 || offset < length`; the first clause exists for exactly this.
  `AQIDAAQBAXQABAEBdANhYmMA` (an empty string item and "abc", both at clock 0): Yjs text "abc",
  state 3. Port: `YUpdateException`. Documents written by older clients or other implementations
  that hold such an item cannot be loaded at all.
- Fix: decide deliberately. Either accept them as Yjs does (the store then holds two structs with
  the same clock, which `FindIndexSS` tolerates in Yjs), or drop them while reading.

**F9 - Client ids of 2^32 and above are rejected.**
Severity: rejection of valid input. Confirmed by run.

- Yjs reads var-uints up to 2^53 and `doc.clientID` is assignable; yrs/y-crdt and y-py type the
  client id as u64. `Lib0Reader.ReadVarUint` returns `uint` and throws above it, the v2 client column
  likewise. `AQGFgICAgCAABAEBdANiaWcA` (client 2^40 + 5): Yjs "big"; port "A variable-length
  integer is out of range" (v2: "A value in a v2 column is out of range"). 2^32 - 1 works.
- Fix: read clients (struct sections, ids, delete sets, state vectors) as 53-bit values; `ID.Client`
  is a `long` already. Only `YDoc.ClientId` needs to stay small.

**F10 - `IterateDeletedStructs` has no guards; a snapshot newer than the document throws.**
Severity: exception. Confirmed by run (`KeyNotFoundException`).

- Yjs skips a client the store does not have and stops at ranges beyond the client's state
  (`structs != null`, `del.clock < clockState`). Port: `Store.Clients[kvp.Key]` and an unguarded
  `IterateStructs`. Reached through `SplitSnapshotAffectedStructs` (`YText.ToDelta(snapshot)`,
  `ToArray(snapshot)` …) and the undo manager.
- Scenario: document `AQEFAAQBAXQLaGVsbG8gd29ybGQA`, snapshot `AgYBAAEFAQAFAgYDBQs=` taken later
  (it deletes clocks of a client 6 the document has not seen). Yjs `toDelta(snap)`:
  `[{"insert":" world"}]`. Port: throws.
- Fix: the two guards.

**F11 - A subdocument whose `meta` is not an object is rejected.**
Severity: rejection of valid input. Confirmed by run.

- Yjs' `meta` is `any`. `new Y.Doc({ guid, meta: 'just a string' })` as a subdocument
  (`AQEMACkBAW0BcwhtZXRhLXN0cnYBBG1ldGF3DWp1c3QgYSBzdHJpbmcA`) throws "A subdocument's 'meta' is
  not an object". Fix: keep `meta` as `object`.

**F12 - `MaxPendingBytes` (16 MiB) rejects large out-of-order deliveries.** By reading.

- A valid stream whose earlier part arrives last (a store replayed newest first, a large diff
  before its base) throws once more than 16 MiB wait. The growth of a duplicate update is counted
  in full even after compaction, so a retransmission near the limit is rejected too. Port-only
  protection; documented and configurable, so a risk rather than a defect.

**F13 - Nesting deeper than 256 is rejected** (shared types, 'any' and JSON values). By reading.
Yjs has no limit (it recurses). Legal but unusual input; documented.

### Wrong event or order

**F14 - Deep observers see `CurrentTarget` (and so `Path`) of the wrong type.**
Severity: wrong event. Confirmed by run.

- Yjs sets `event.currentTarget = type` only for types that have deep observers
  (`type._dEH.l.length > 0`), so an event ends with the last *observed* ancestor. The port sets it
  for every type in `ChangedParentTypes`.
- Scenario: root map with `observeDeep`, nested map `c`; one transaction does `root.set('x',1)`
  then `c.set('y',2)`. Yjs: the event of `c` has `currentTarget === root`, `path == ['c']`. Port:
  `CurrentTarget == c`, `Path == []`.
- Fix: skip types without a `DeepEventHandler` in the preparation loop.

**F15 - The events of a deep observer are sorted with an unstable sort.**
Severity: wrong event order. Confirmed by run.

- Yjs' `events.sort` is stable. `List<T>.Sort` is not once a list has more than 16 elements.
  40 nested maps changed in one transaction (k39 … k0): Yjs delivers `k39,k38,…,k0`; the port
  `k39,k17,k16,…,k1,k0`. Fix: a stable sort (insertion sort; the lists are short).

**F16 - Events whose target was deleted are not removed from a deep observer's list.** By reading.
Yjs filters them (`event.target._item === null || !event.target._item.deleted`); the port only
leaves their `CurrentTarget` alone. Reached when an observer deletes a type that also changed.

**F17 - A subdocument does not take its parent's client id.** Confirmed by run.
Yjs: `subdoc.clientID = doc.clientID` (and `collectionid`) for every added subdocument. The port
keeps the subdocument's own random id. Not a convergence problem; the subdocument's structs and
state vectors use another client id than Yjs would, and `collectionid` does not exist.

**F18 - Exception paths of the cleanup differ.** By reading. Cosmetic.
(a) Yjs lets the *last* exception of `callAll` escape, the port rethrows the *first*. (b) An
exception from a `beforeObserverCalls` handler skips all observers in Yjs; the port still calls
them. (c) An exception from an `update` handler leaves a Yjs document with a non-empty
`_transactionCleanups` (stuck); the port recovers. (d) When the transacted function throws, the
port swallows an observer exception from the cleanup, Yjs lets it replace the original.

### Wrong bytes

**F19 - `MergeUpdates` orders its inputs with an unstable sort.**
Severity: wrong bytes (the result is valid and yields the same document). Confirmed by run.
With more than 16 updates and ties (the same client and clock at the head of several updates) the
port picks another decoder than Yjs. 24 full states of a growing text, shuffled: Yjs
`AQYeAAQBAXQBYYQeAAdiY2RlZmdo…` (6 structs), port `AQceAAQBAXQBYYQeAANiY2SEHgME…` (7 structs).
Fix: a stable sort in `MergeStructs`.

**F20 - `ContentJSON` loses `undefined`.** Confirmed by run.
Yjs reads the string `undefined` as `undefined` and writes it back. The port reads it as null and
writes `null`: `AQECAAIBAWECCXVuZGVmaW5lZAd7ImsiOjF9AA==` re-encodes as
`AQECAAIBAWECBG51bGwHeyJrIjoxfQA=`. Legacy content (Yjs before 13); keep a marker for undefined.

**F21 - A root type named by a pending struct is created only when the struct integrates.** By reading.
Yjs calls `doc.get(name)` while reading, so `doc.share` has the key at once. Visible only in
enumerations of the root types.

**F22 - `DeleteSet.Read` normalizes.** By reading. Empty ranges (v1 only) are dropped and a client
listed twice is merged into one list, where Yjs keeps both and applies them in the listed order.
Yjs writes neither.

### Cosmetic

**F23 - `Get<T>` accepts a subclass.** Confirmed by run. `getXmlElement('x')` then
`getXmlFragment('x')` throws in Yjs ("different constructor"); the port returns the element
(`type is not T`). Same for `YXmlText`/`YText`, `YXmlHook`/`YMap`.

**F24 - Inserting an integrated subdocument a second time throws** (`Exception`); Yjs logs an error
and goes on. Confirmed by run.

**F25 - `Destroy` of a subdocument:** for a deleted item the port sets `ContentDoc.Doc = null`
(Yjs always puts a fresh `Doc` there), and the replacement document gets `ShouldLoad = AutoLoad`
where Yjs forces `shouldLoad: false`, so a later `Load()` of an auto-load subdocument raises no
event. By reading.

**F26 - `ContentDeleted.GetContent()` throws** `NotImplementedException`; Yjs returns `[]`. Not
reached by the port's own code. By reading.

## Reviewed without a finding

**Phase order of the cleanup.** `sortAndMergeDeleteSet`, `afterState`, `beforeObserverCalls`,
observers in `changed` order, deep observers (all prepared first, then called), `afterTransaction`,
formatting cleanup; then in the `finally`: gc, merge of the delete set, merge from each client's
first new struct, `_mergeStructs`, client id change for a non-local transaction that advanced the
own clock, `afterTransactionCleanup`, `update`, `updateV2`, subdocuments, and after the last
transaction `afterAllTransactions` with `_transactionCleanups` replaced first. Equal to Yjs.

**Merging.** The port merges one pair per call and walks `_mergeStructs` forwards; Yjs chains
(`tryToMergeWithLefts`) and walks backwards. Whether two neighbours merge depends only on the pair
(ids, origins, `right`, deleted, content class, `redone`), and merging does not change that for
other pairs, so the order does not matter; every boundary a transaction can have made mergeable
(new structs, deleted ranges, split halves, children collected with their type) is tried at least
once in both. Not proven by a run of its own; the corpus compares the resulting encodings.

**Transaction reuse.** `Begin` sets origin, local and the before-state; `End` clears or replaces
every collection, resets `_needFormattingCleanup`, the "taken" flags, the subdocument sets, `Meta`,
`_deepCalls`, `_mergeStructs`, and detaches the events. A transaction is returned to the pool only
after its batch's `AfterAllTransactions`, so one started by an observer or by such a handler never
gets an object that is still in use. No state was found that survives into the next transaction.

**`integrateStructs`.** The plan uses its own state map where Yjs reads the store while it
integrates; after every planned step both hold `clock + length`. Structs that are known already
still have their references resolved (splitting items), in the same position of the sequence.

## Not compared, and why

- The codec below the update layer (`UpdateEncoder/DecoderV1/V2`, `lib0` var-ints, 'any', strings,
  the RLE columns): read only where the core depends on it (value ranges). Assumed to be another area.
- `UndoManager` beyond `redoItem`, `YEvent` (changes, keys, delta), `YText`'s formatting cleanup,
  the types, `Snapshot`, `RelativePosition`, `PermanentUserData`.
- yrs/y-crdt and y-py output: not available offline; the statements about them (u64 client ids,
  descending client order) are from knowledge of their sources, not from a run.
- `obfuscateUpdate`, `Doc.toJSON`, the load/sync state of `Doc`: not ported, nothing to compare.
- Debug builds: all probes ran in Release (F1 would trip `Debug.Assert` in Debug).
- Whether Yjs versions before 13.6 could write zero-length items (F8): not checked against old sources.
