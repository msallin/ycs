# Walkthrough of the core against Yjs 13.6.32

How the core of Ycs maps to the Yjs 13.6.32 source: `structs/*`, `utils/{StructStore,Transaction,DeleteSet,encoding,updates,ID,isParentOf,Doc}.js`
and their C# counterparts in `src/Ycs`. Use it to find where a Yjs function lives in Ycs, and what is different on purpose.

Two things to know before the tables:

- `ID` in the code is a global alias of the public struct `ItemId`, and `AbstractType` an alias of `YType`.
- A struct of a document's store (an item or a GC struct) is a **slot** of the document's `Slab`, not an object. An
  `ItemRef` is a (slab, handle) pair that names a slot and reads and writes its fields. `ItemRef.Left` and `ItemRef.Right`
  return the `ItemRef` of the slot that the slot's link fields name, and `default(ItemRef)` is Yjs' `null`. The classes
  `Item`, `GC` and `Skip` are the *decoded* form of a struct, held by the update reader, the pending store, the integration
  plan and `MergeUpdates`; `ItemRef.Adopt` is the one place where a decoded struct enters a store.
  [design-item-layout.md](design-item-layout.md) describes slots, handles, pages and the free lists.

Verdicts: **identical** (the same control flow), **equivalent** (different code, the same observable behavior on valid
input), **differs** (see [Differences from Yjs](#differences-from-yjs)), **not ported**, **port-only**.

## Function by function

### structs/Item.js

| JS | C# | Verdict |
| --- | --- | --- |
| `followRedone` | `StructStore.FollowRedone` | identical |
| `keepItem` | `ItemRef.KeepItemAndParents` | identical |
| `splitItem` | `ItemRef.SplitItem` (`StructStore.SplitItem` also bumps `Version`) | equivalent |
| `isDeletedByUndoStack` | `UndoManager.IsDeletedByStack` | identical |
| `redoItem` | `UndoManager.RedoItem` | identical |
| `Item` constructor, `info` bits (keep, countable, deleted, marker) | `ItemRef.New`, the flag bits of `Slot.Info` | equivalent |
| `Item.getMissing` | `Item.FindMissing` (decoded items), `ItemRef.ResolveReferences` | equivalent |
| `Item.integrate` (conflict loop, relinking, map value, length, delete if the parent is deleted) | `ItemRef.Integrate` | equivalent (also keeps `_mapCount`) |
| `next`, `prev` | `ItemRef.Right`, `ItemRef.Left` | identical |
| `Item.mergeWith` | `ItemRef.MergeWith` (`CanMergeWith` first), `YArrayBase.OnMerge` | equivalent |
| `Item.delete` | `ItemRef.Delete` | equivalent (also keeps `_mapCount`) |
| `Item.gc` | `ItemRef.Gc` | identical |
| `Item.write` | `ItemRef.Write` | equivalent |
| `readItemContent`, `contentRefs` | `EncodingUtils.ReadItemContent` | equivalent (an unknown content reference is a `YUpdateException`) |

A garbage-collected item is the same slot rewritten as a GC struct (`StructStore.ReplaceWithGC`, `ItemRef.BecomeGC`), so
every reference to it stays valid and reads a GC struct.

### structs/GC.js, Skip.js, AbstractStruct.js

| JS | C# | Verdict |
| --- | --- | --- |
| `AbstractStruct` | `AbstractStruct` | identical |
| `GC.integrate`, `GC.write`, `GC.getMissing`, `GC.delete` | the same names, on a decoded `GC` and on the `ItemRef` of a GC slot | identical |
| `GC.mergeWith` | `GC.MergeWith` | identical (merges only a GC) |
| `Skip.*` | `Skip.*` | equivalent (a Skip exists only while updates are merged or diffed; it has no `integrate`) |

### structs/Content*.js

| JS | C# | Verdict |
| --- | --- | --- |
| `ContentAny`, `ContentBinary`, `ContentEmbed`, `ContentFormat` | the same names | identical or equivalent |
| `ContentDeleted` | `ContentDeleted` | equivalent (a deleted slot holds no content object) |
| `ContentDoc` (constructor, copy, integrate, delete, write, read, `createDocFromOpts`) | `ContentDoc`, `YDocOptions` | equivalent |
| `ContentJSON` | `ContentJson` | equivalent (`undefined` is read as `null`) |
| `ContentString` (splice with the surrogate repair, mergeWith) | `ContentString` | equivalent (the same code units) |
| `ContentType` (delete, gc, read) | `ContentType`; a Type slot holds its type's index in the `TypeTable` | equivalent (`delete` adds every already deleted child to `_mergeStructs`, Yjs only those older than the transaction; the extra entries merge nothing) |

### utils/StructStore.js

| JS | C# | Verdict |
| --- | --- | --- |
| `getStateVector`, `getState` | `GetStateVector`, `GetState` | identical |
| `integrityCheck` | `IntegrityCheck` | equivalent (also fails on pending data) |
| `addStruct` | `AddStruct` | identical |
| `findIndexSS` | `FindIndexSS`, over `ClientStructs` (the start clocks beside the structs, in leaves) | equivalent |
| `find`, `getItem` | `Find` | identical |
| `findIndexCleanStart`, `getItemCleanStart`, `getItemCleanEnd` | the same names | identical |
| `replaceStruct` | `ReplaceWithGC` | equivalent |
| `iterateStructs` | `IterateStructs` | equivalent (the callback may stop the loop) |

### utils/Transaction.js

| JS | C# | Verdict |
| --- | --- | --- |
| `Transaction` constructor | `Transaction.Begin`, `End` (objects are reused) | equivalent |
| `writeUpdateMessageFromTransaction` | `WriteUpdateMessageFromTransaction` | identical |
| `nextID` | `GetNextId` | identical |
| `addChangedTypeToTransaction` | `AddChangedTypeToTransaction` | equivalent (shared read-only sets) |
| `tryToMergeWithLefts` | `DeleteSet.TryToMergeWithLefts` | equivalent (see [Merging](#merging)) |
| `tryGcDeleteSet`, `tryGc` | `DeleteSet.TryGcDeleteSet`, `TryGc` | identical |
| `tryMergeDeleteSet` | `DeleteSet.TryMergeDeleteSet` | equivalent |
| `cleanupTransactions` | `Transaction.CleanupTransactions`, `CleanupTransaction` | differs: throwing handlers |
| `transact` | `YDoc.Transact` | differs: throwing handlers |

### utils/DeleteSet.js

| JS | C# | Verdict |
| --- | --- | --- |
| `iterateDeletedStructs` | `DeleteSet.IterateDeletedStructs` | identical (skips a client the store lacks and stops at ranges beyond its state) |
| `findIndexDS`, `isDeleted` | `FindIndexSS`, `IsDeleted` | identical |
| `sortAndMergeDeleteSet`, `mergeDeleteSets` | `SortAndMergeDeleteSet`, `MergeDeleteSets` | identical |
| `addToDeleteSet` | `Add` | identical |
| `createDeleteSetFromStructStore` | `CreateDeleteSetFromStructStore`, `NextDeletedRange` | equivalent |
| `writeDeleteSet` | `Write` | identical |
| none | `WriteFromStore` | port-only (the same bytes as build and write) |
| `readDeleteSet` | `Read`, `ReadInto` | equivalent on what Yjs writes (see [Differences from Yjs](#differences-from-yjs)) |
| `readAndApplyDeleteSet`, the `pendingDs` handling in `readUpdateV2` | `StructStore.ApplyDeleteSet`, the pending deletes | equivalent |

### utils/encoding.js

| JS | C# | Verdict |
| --- | --- | --- |
| `writeStructs`, `writeClientsStructs` | `EncodingUtils.WriteStructs`, `WriteClientsStructs` | identical, equivalent |
| `readClientsStructRefs` | `UpdateReader`, `StructCursor`, `EncodingUtils.ReadStruct` | differs: input checks |
| `integrateStructs` | `StructStore.IntegrationPlan` (`Plan`, `Execute`, `EncodeRest`) | equivalent (the same visiting order, stack and rest handling, including Yjs' `restStructs.clients.set(client, [item])` overwrite) |
| `writeStructsFromTransaction` | inline in `WriteUpdateMessageFromTransaction` | identical |
| `readUpdateV2`, `readUpdate` | `YDoc.ApplyUpdate`, `StructStore.ApplyUpdate` | equivalent in flow: structs, the rest to pending, the update's deletes, the waiting deletes, retry |
| `applyUpdate`, `applyUpdateV2` | `YDoc.ApplyUpdate(..., format)`, `TryApplyUpdate` | identical |
| `writeStateAsUpdate` | `EncodingUtils.WriteStateAsUpdate` | equivalent |
| `encodeStateAsUpdate(V2)` | `YDoc.EncodeStateAsUpdate(..., format)` | equivalent (pending structs and deletions are part of the result, as in Yjs) |
| `readStateVector`, `decodeStateVector` | `ReadStateVector`, `DecodeStateVector` | equivalent (clock limit) |
| `writeStateVector`, `encodeStateVector(V2)` | `WriteStateVector`, `YDoc.EncodeStateVector` | identical |

### utils/updates.js

| JS | C# | Verdict |
| --- | --- | --- |
| `lazyStructReaderGenerator`, `LazyStructReader` | `StructCursor`, `LazyStructReader` | equivalent |
| `LazyStructWriter` and its functions | `LazyStructWriter` | equivalent |
| `mergeUpdates(V2)` | `YUpdate.Merge`, `UpdateOperations.MergeStructs` | identical, including the order of equal inputs (`StableSort` reproduces V8's sort) |
| none | `MergeUpdatesTrusted` (groups of 8) | port-only |
| `sliceStruct` | the slicing in `UpdateOperations` | identical |
| `encodeStateVectorFromUpdate(V2)` | `YUpdate.EncodeStateVector` | identical |
| `parseUpdateMeta(V2)` | `YUpdate.ParseMeta` | identical |
| `diffUpdate(V2)` | `YUpdate.Diff` | identical |
| `convertUpdateFormat*` | `YUpdate.Convert` | identical |
| `decodeUpdate(V2)`, `logUpdate(V2)` | `YUpdate.Decode`, `YUpdate.Describe` | equivalent |
| `obfuscateUpdate(V2)` | none | not ported |

### utils/ID.js, isParentOf.js

| JS | C# | Verdict |
| --- | --- | --- |
| `ID`, `createID`, `compareIDs` | `ItemId` (a struct; `ID` in the code) | equivalent |
| `writeID`, `readID` | the encoders and decoders | equivalent (client up to 2^53 - 1, clock up to 2^31 - 1) |
| `findRootTypeKey` | `YDoc.FindRootTypeKey`, cached in `YType.FindRootTypeKey` | equivalent |
| `isParentOf` | `UndoManager.IsParentOf` | equivalent |

### utils/Doc.js

| JS | C# | Verdict |
| --- | --- | --- |
| `generateNewClientId` | `YDoc.GenerateNewClientId` | identical (32 random bits) |
| constructor: gc, gcFilter, guid, meta, autoLoad, shouldLoad, collectionid | `YDocOptions`, the `YDoc` constructor | equivalent |
| `isLoaded`, `isSynced`, `whenLoaded`, `whenSynced`, the `load` and `sync` events, `isDestroyed` | none | not ported |
| `load` | `Load` | identical |
| `getSubdocs`, `getSubdocGuids` | internal `Subdocs`, `GetSubdocGuids` | equivalent |
| `transact` | `Transact` | equivalent |
| `get` (placeholder upgrade, "different constructor"), `getArray`, `getText`, `getMap`, `getXmlElement`, `getXmlFragment` | internal `Get<T>`, `GetArray`, `GetText`, `GetMap`, `GetXmlElement`, `GetXmlFragment`, `GetXmlText` | identical (asking for a root as another type throws) |
| `toJSON` | none | not ported |
| `destroy` | `Destroy` | equivalent (iterates a copy of the subdocuments) |
| events `beforeAllTransactions`, `beforeTransaction`, `beforeObserverCalls`, `afterTransaction`, `afterTransactionCleanup`, `update`, `updateV2`, `subdocs`, `afterAllTransactions`, `destroy` | `BeforeAllTransactions`, `BeforeTransaction`, `BeforeObserverCalls`, `AfterTransaction`, `AfterTransactionCleanup`, `Updated`, `UpdatedV2`, `SubdocsChanged`, `AfterAllTransactions`, `Destroyed` | identical order |

### Port-only

| Port-only member | What it is for |
| --- | --- |
| `StructStore.IntegrationPlan` | works out an update's integration against a model of the store before anything changes, so that a rejected update leaves the document as it was |
| `Limits.MaxClock` (2^31 - 1), `Limits.MaxClient` (2^53 - 1) | every clock, length and gap fits an `int`; a client id is a JavaScript safe integer |
| `UpdateLimits` (`MaxStructsPerUpdate`, `MaxIntegrationSteps`, `MaxPendingBytes`, `MaxPendingStructs`, `MaxDepth`) | bound what an update may cost; see [api.md](api.md#limits-and-untrusted-input) |
| a struct of length 0, or a client listed twice in an update, is rejected | Yjs integrates the first and keeps only the last section of the second; no Yjs writer produces either |
| `CheckEarlier`: an own-client reference must point to an earlier clock | always true for a valid update |
| `PendingStructs`, `PendingDeletes`, `HasPending` | the pending store as public state ([api.md](api.md#pending-structs-retried)) |
| `YType._mapCount`, `StructStore.Version`, `IsObserved` gating | O(1) `YMap.Count`, cache invalidation, no events when nobody observes |
| transaction reuse, `UpdateReader` pooling | allocation |

## Differences from Yjs

- **Throwing handlers.** In Yjs an `update` handler that throws leaves `_transactionCleanups` non-empty and the document
  stuck (yjs#799), and `beforeAllTransactions` and `beforeTransaction` run outside the `try` of `transact`. Ycs runs them
  inside, finishes the cleanup and rethrows ([api.md](api.md#exceptions-of-handlers)). When the transacted function
  throws, an observer exception from the cleanup is swallowed, where Yjs lets it replace the original. An exception from a
  `BeforeObserverCalls` handler does not skip the observers, as it does in Yjs.
- **Input checks.** The decoders reject what no valid Yjs writer produces and what would cost without bound: a struct of
  length 0, a client listed twice, a clock beyond 2^31 - 1, nesting beyond `UpdateLimits.MaxDepth` (3,000, the depth Yjs
  itself survives) and more structs than `MaxStructsPerUpdate`. Yjs has no such limits.
- **`DeleteSet.Read` normalizes**: empty ranges (v1 only) are dropped and a client listed twice is merged into one list,
  where Yjs keeps both and applies them in the listed order. Yjs writes neither.
- **A root type named by a pending struct** is created when the struct integrates, where Yjs calls `doc.get(name)` while
  reading. No public API lists the root types, so this cannot be observed.
- **`undefined`** is not a value: Yjs' `ContentJSON` reads the string `undefined` and writes it back; Ycs reads it as
  `null`.

## Reviewed without a difference

### The phases of the cleanup

`sortAndMergeDeleteSet`, `afterState`, `beforeObserverCalls`, observers in `changed` order, deep observers (all prepared
first, then called), `afterTransaction`, formatting cleanup; then in the `finally`: gc, merge of the delete set, merge from
each client's first new struct, `_mergeStructs`, the client id change for a non-local transaction that advanced the own
clock, `afterTransactionCleanup`, `update`, `updateV2`, subdocuments, and after the last transaction `afterAllTransactions`
with `_transactionCleanups` replaced first. Equal to Yjs.

### Merging

`DeleteSet.TryToMergeWithLefts` finds the run that merges leftwards from a struct (`ItemRef.CanMergeWith`, which changes
nothing) and merges it into its leftmost struct from left to right. Yjs merges pair by pair from the right, which copies
the run's content into every left neighbor. Whether two neighbors merge depends only on the pair (ids, origins, `right`,
deleted, content class, `redone`), and merging does not change that for other pairs, so the order does not matter. Yjs'
order is kept as `TryToMergeWithLeftsPairwise`, and the `merge-order` property compares the struct stores after every step
([testing.md](testing.md#reference-comparisons)). Merging frees the right part's slot.

### Runs extended in place

An insert that the end of the transaction would merge into its left neighbor (`ItemRef.CanExtendRun`: the same client and
the next clock, the neighbor as its origin, the same right origin and parent, not deleted, not kept or redone, and for a
text not ending in the first half of a surrogate pair) appends to it instead (`ItemRef.ExtendRun`) when the transaction is
local and the only one of its batch (one an observer starts runs before the other one's merge, which Yjs then does on its
own terms) and nobody observes the type. Observed means: a handler on it, a deep handler on it or an ancestor, an
`AfterTransaction`, `AfterTransactionCleanup`, `AfterAllTransactions` or `BeforeObserverCalls` handler on the document
(`YType.IsObserved`; an `UndoManager` is an `AfterTransaction` handler).

The transaction records where the item would have started and splits there (`Transaction.MaterializeRunExtensions`) before
anything that could tell the difference:

- the cleanup, when someone is called with the transaction or its events before the merge (a handler attached within the
  transaction counts), or when a garbage collection filter other than the default one will be called on its deletions;
- encoding the store, applying an update, undo;
- a snapshot read (`ToArray(snapshot)`, `ToDelta` with a snapshot), whose visibility is decided per item;
- an insert that moves a search marker off a deleted or uncountable item, which in Yjs lands on the last of the run's items.

Where none of these happens, the runs are what the merge makes of Yjs' items. Against the reference (every insert an item
of its own, merged pair by pair) the `run-extension` property compares the stores, markers included, after every step;
against Yjs the live soak's quiet clients and `StaleMarkerScriptTests` do ([testing.md](testing.md)).

### Transaction reuse

`Begin` sets origin, local and the before-state; `End` clears or replaces every collection, resets
`_needFormattingCleanup`, the "taken" flags, the subdocument sets, `Meta`, `_deepCalls`, `_mergeStructs`, and detaches the
events. A transaction returns to the pool only after its batch's `AfterAllTransactions`, so one started by an observer or
by such a handler never gets an object that is still in use.

### `integrateStructs`

The plan uses its own state map where Yjs reads the store while it integrates; after every planned step both hold
`clock + length`. Structs that are known already still have their references resolved (splitting items), in the same
position of the sequence.

## Not compared

- The codec below the update layer (`UpdateEncoder` and `UpdateDecoder` V1 and V2, the lib0 var-ints, `any`, strings, the
  RLE columns) is in [walkthrough-undo-snapshots-protocols.md](walkthrough-undo-snapshots-protocols.md).
- `obfuscateUpdate`, `Doc.toJSON`, the load and sync state of `Doc`: not ported, nothing to compare.
- yrs and y-py: not compared source against source. What this documentation says about them (u64 client ids, descending
  client order) is from their sources, not from a run.
