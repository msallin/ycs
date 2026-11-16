# Walkthrough C: undo, snapshots, positions, subdocuments, encoders, protocols

## Status

Where each finding stands on branch `integrate` (fix-core, fix-undo and fix-types merged). The
walkthrough below describes the code at 40d7fdb.

| Finding | Status |
| --- | --- |
| 1 undo of untracked content in merged captures | fixed in 3735809 |
| 2 a stack item that changes nothing | fixed in 3735809 |
| 3 `RedoItem` of 13.4 | fixed in 3735809 |
| 4 undo of a deleted nested `YText` | fixed in 0faeec2 |
| 5 relative position at the edge of a nested type | fixed in 576976d |
| 6 subdocument options and lifecycle | fixed in 7547e92 |
| 7 v2 struct limit | fixed in a0e0904 |
| 8 client ids of 2^32 and above | fixed in e833da7; generated ids have 32 bits since 0432537 |
| 9 `Destroy` with subdocuments | fixed in 7547e92 |
| 10 `YText.ToDelta` | fixed in ad0baa4 (embedded types) and a66588c (`computeYChange`, no transaction, outside a document) |
| 11 lone surrogate escape in v1 JSON | fixed in 1d11000 |
| 12 PermanentUserData | fixed in f49d651; the filter still gets the transaction only, on purpose (the delete set is internal) |
| 13 events of the UndoManager | fixed in 3735809 and 0ddbae0 |
| 14 snapshot-aware reads | `GetAttributes(snapshot)` fixed in 0ecec64; `ToArraySnapshot`, `GetSnapshot`, `GetAllSnapshot` read without a transaction or a split since fix-prop, as Yjs (a split could replace a lone high surrogate and the next character with U+FFFD) |
| 15 sync protocol errors | fixed in f48345c (optional error handler) |
| 16 decoding leniency | invalid UTF-8 fixed in 1d11000; the rest is kept on purpose (hardening) |
| 17 merging to the left stops after one struct | fixed in 0b1cbe6; the 20 scripts of a 20,000 x 60 undo fuzz run it broke are replayed since 05564bc (the run matches Yjs on every step) |
| `YMap.Get` of a missing key | fixed in 3c95cc7 |
| untrusted-input holes: snapshots | fixed in bced462 and a791a61 |
| untrusted-input holes: JSON positions | fixed in d8d34ec |
| untrusted-input holes: `users` map | fixed in f49d651 |
| least verified 1: undo with XML, embeds, more clients | covered by the differential undo fuzz (3ec10ef) |
| least verified 3: subdocument lifecycle | nested destroy tested in cca5965, undo and redo of a subdocument in bdc5f59; nothing differed |

Side-by-side reading of Ycs (`modernize`, 40d7fdb) against Yjs 13.6.32 (`interop/corpus/node_modules/yjs/src`),
lib0 0.2.120 and y-protocols 1.0.7. Nothing in the repository was changed; this file is the only addition.

"Confirmed by run" means: a Node script against the real Yjs and a C# console project against
`src/Ycs/Ycs.csproj` produced the quoted results. The probes are in `/tmp/ycs-walk-c`
(`js/probe.mjs`, `js/probe2.mjs`, `js/probe3.mjs`, `js/fuzzgen.mjs`, `cs/Program.cs`, `cs/Fuzz.cs`).

## Summary

- The UndoManager and `RedoItem` are a port of Yjs 13.4, not 13.6. Three algorithm differences change
  documents (findings 1-3), and undoing the deletion of a nested `YText` throws (finding 4).
- A differential undo fuzz (1,600 scripts of 50 steps: text, map, array, nested map and text, formatting,
  remote updates, untracked origins, capture merging; state bytes compared with Yjs after every step)
  diverges in 741 scripts on the port. A scratch copy of `src/Ycs` with fixes 1-4 applied
  (`/tmp/ycs-walk-c/patched`) passes all 1,600 byte-equal, so those four fixes are sufficient for
  everything that fuzz reaches.
- Outside undo, the findings that change documents or reject valid input are: relative positions at the
  end of a nested type (5), subdocument options (6), the v2 struct limit (7), client ids above 2^32 (8).

Verdict counts over the table below (103 rows): identical 45, equivalent 22, DIFFERS 25, not ported 7,
port-only 3, not compared 1.

## Findings

Severity classes: wrong document / wrong bytes / wrong event or order / exception / untrusted-input hole / cosmetic.

### 1. Undo deletes content it never tracked when captures are merged — wrong document

- Scenario A1 (confirmed by run): `trackedOrigins = {"t"}`, large `captureTimeout`; insert `a` (origin t),
  insert `X` (origin u, same client), insert `b` (origin t); `undo()`.
  Yjs: `"X"`. Ycs: `""`.
- Scenario A2 (confirmed by run): default origins; local insert `a`; apply a remote update inserting `R`;
  local insert `b` within the capture timeout; `undo()`. Yjs: `"R"`. Ycs: `""` — the remote user's text
  is deleted.
- Cause: Yjs 13.6 `StackItem` holds `insertions`, a `DeleteSet` of exactly the ranges the captured
  transactions created, merged with `mergeDeleteSets`. Ycs `StackItem` holds `BeforeState` of the first and
  `AfterState` of the last merged transaction and undoes every struct between them
  (`UndoManager.cs:506-562`), and uses the same range for "created and deleted in this capture"
  (`:580-583`).
- Fix: give `StackItem` an `Insertions` delete set built in `OnAfterTransaction` from
  `afterState - beforeState`, merge it on capture, iterate it in `PopStackItem`
  (`iterateDeletedStructs(transaction, stackItem.insertions, ...)` with the follow-redone step of Yjs) and
  test `Insertions.IsDeleted(id)` instead of the clock range.

### 2. A stack item that changes nothing ends the undo — wrong document, wrong return value

- Scenario C (confirmed by run): `captureTimeout 0`; insert `a`; insert `b`; delete `b` with an untracked
  origin; `undo()`. Yjs: text `""`, undo stack 0, redo stack 1. Ycs: text `"a"`, undo stack 1, nothing redoable,
  and `Undo()` returns the stack item although it did nothing.
- Also reached without untracked origins: insert and delete merged into one capture, then undo
  (fuzz mode 3), and a remote delete of what the stack item inserted (fuzz mode 1).
- Cause: Yjs sets `currStackItem = performedChange ? stackItem : null` and the loop pops the next item;
  Ycs sets `result = stackItem` unconditionally (`UndoManager.cs:608`).
- Fix: `result = performedChange ? stackItem : null`.

### 3. `RedoItem` is the 13.4 algorithm — wrong document

- Scenario B (confirmed by run): map, `trackedOrigins = {"t"}`; `set a=1` (t); `set a=2` (t);
  `set a=3` (untracked, same client); `undo()`. Yjs: `a = 3`, returns null, both stacks empty (the value
  set outside the undo manager is kept). Ycs: `a = 1`, returns the item (the untracked value is overwritten).
- Differences in `Transaction.RedoItem` (`Transaction.cs:556-672`) against `redoItem` (`Item.js`):
  - map item: Ycs walks right while the next item is from the own client and refuses on the first item
    of another client. Yjs walks right only over items that are redone, in the stack item's insertions, or
    deleted by the undo or redo stack (`isDeletedByUndoStack`), and refuses otherwise. So Ycs overwrites
    own untracked values and refuses remote values that Yjs would replace.
  - `ignoreRemoteMapChanges` does not exist.
  - map item with no right neighbour: Yjs takes `parentType._map.get(parentSub)` as left; Ycs takes the item.
  - the "cross-parent left" correction (#757) is missing.
  - Ycs runs the left/right tracing only when the parent was redone; equivalent for list items.
- Fix: port `redoItem` as it is in 13.6 (parameters `itemsToDelete`, `ignoreRemoteMapChanges`, `um`).

### 4. Undoing the deletion of a nested `YText` throws `NotImplementedException` — exception

- Scenario (confirmed by run): array in scope; insert a `YText`, write into it, delete it from the array,
  `undo()`. Yjs: `["pq"]`. Ycs: `NotImplementedException` from `YType.InternalCopy`
  (`YType.cs:75`), thrown inside the transaction; the stack item is lost.
- Cause: `ContentType.Copy()` calls `Type.InternalCopy()`; `YText` does not override it
  (`YMap`, `YArray`, `YXmlElement`, `YXmlFragment`, `YXmlText`, `YXmlHook` do). Yjs: `YText._copy()` returns `new YText()`.
- Fix: `internal override YType InternalCopy() => new YText();` in `YText`.
- In the fuzz the first divergence is an exception in 499 of the 741 diverging scripts; every one inspected was this one.
  Without nested texts, single-client scripts with every change tracked and no merging do not diverge (0 of 250).

### 5. Relative position at the end (or left-associated start) of a nested type points into the parent — wrong bytes, wrong position

- Scenario E (confirmed by run): array `[x, y, <YText "abc">]`, positions on the nested text.

  | call | Yjs bytes | Ycs bytes | Yjs resolves to | Ycs resolves to |
  |---|---|---|---|---|
  | index 3, assoc 0 | `02 01 02 00` | `00 01 02 00` | nested text, 3 | parent array, 2 |
  | index 0, assoc -1 | `02 01 02 41` | `00 01 02 41` | nested text, 0 | parent array, 3 |
  | empty nested text, index 0 | `02 01 06 00` | `00 01 06 00` | | |

  Positions inside the content (E3, E4) and on root types (E6) are byte-equal. Ycs decodes and resolves
  Yjs' bytes correctly; only the positions it creates are wrong.
- Cause: `RelativePosition.FromTypeIndex` passes `type._item?.Id` as the *item* in the two fallback returns
  (`RelativePosition.cs:92` and `:118`); Yjs passes `null`, so the type id is written (kind 2).
- Fix: pass `null` for the item in both places.

### 6. Subdocument options: Ycs writes `gc`, a random `guid` and `autoLoad` — wrong document for Yjs peers, wrong bytes

- Bytes (confirmed by run) for `map.set('s', new Doc({ guid: 'g1' }))`, client 1, v1, the content after the guid string:
  - Yjs: `76 00` — an empty object. Yjs writes `gc` only when false, `autoLoad` only when true, `meta` only
    when not null, in that order, and never `guid`. Whole update: `010101002901016d0173026731760000`.
  - Ycs: `76 03 | 02 "gc" 78 | 04 "guid" 77 24 "<36 characters>" | 08 "autoLoad" 79`.
- The `guid` inside the options is not the document's guid: `ContentDoc.Opts` is a fresh `YDocOptions`,
  whose `Guid` defaults to a new random GUID, and `YDocOptions.Write` writes that. So the bytes differ on every
  encoding instance, and Yjs' `createDocFromOpts(guid, opts)` is `new Doc({ guid, ...opts })`: the options win.
  Confirmed by run: Yjs reading the Ycs update gets `subdoc.guid = b42680f0-...` instead of `g1`, and
  re-encodes the item with that guid as content. Ycs peers and Yjs peers then disagree about the identity
  of the subdocument.
- Fix: write only what Yjs writes (`gc: false`, `autoLoad: true`, `meta`), in that order.
- Related, by reading and run:
  - `YDocOptions.Read` rejects a `meta` that is not an object with `YUpdateException`; in Yjs `meta` is any
    value (`meta: 'str'`, `meta: [1]` confirmed rejected), so the whole update fails. Fix: keep `meta` as `object`.
  - `new YDoc()` has `ShouldLoad = AutoLoad` (false); Yjs' default is `shouldLoad = true`. A locally created
    subdocument is therefore missing from `loaded`: Yjs `{added 1, removed 0, loaded 1}`, Ycs `{1, 0, 0}` (confirmed).
  - After `subdoc.Destroy()` Yjs recreates the document with `shouldLoad: false`; Ycs with
    `ShouldLoad = AutoLoad`. For an `autoLoad` subdocument a later `Load()` fires `loaded` in Yjs and
    nothing in Ycs (confirmed, F6).
  - The cleanup does not set `subdoc.clientID = doc.clientID` (`Transaction.js:380`; confirmed: ids differ).
    `collectionid` does not exist.
  - `ContentDoc` constructor throws for a document that is already a subdocument; Yjs logs and continues (confirmed).
  - A destroyed, deleted subdocument has `content.Doc = null` (Yjs keeps a fresh document); `GetContent()` then returns `[null]`.

### 7. A valid v2 update is rejected by the struct limit — exception

- Scenario (confirmed by run): insert 20,000 characters one at a time at index 0, delete them all,
  `encodeStateAsUpdateV2`: 42 bytes, 20,000 structs (prepended items do not merge; every column run-length
  encodes). Yjs applies it. Ycs: `YUpdateException: The update holds more structs than its size allows (6784)`.
- A Ycs peer cannot read the v2 state of another Ycs peer in that shape either; only pending structs are
  read as trusted.
- Cause: `UpdateDecoderV2.MaxStructs = remaining * 64 + 4096` (`Limits.MaxStructsPerByteV2`); the comment
  "updates Yjs writes are far below it" does not hold (476 structs per byte here). `YArray.Unshift` followed by
  deletion gives the same shape.
- Fix: bound the memory, not the ratio: an absolute cap on structs per update (configurable, like
  `MaxPendingBytes`), or count only what is allocated.

### 8. Client ids of 2^32 and above are rejected — exception

- Scenario (confirmed by run): `doc.clientID = 2**40 + 5`, insert, encode. Ycs v1:
  `YUpdateException: A variable-length integer is out of range`; v2: `A value in a v2 column is out of range`.
- Cause: `Lib0Reader.ReadVarUint` returns `uint`; lib0's `readVarUint` reads up to 2^53 - 1.
  `UintOptRleDecoder` and `UpdateEncoderV2.WriteClient((uint)client)` have the same width. Yjs itself
  generates 32-bit ids, but ids are set by applications and other implementations use wider ones.
- Fix: read var-uints as `ulong` up to 2^53 - 1 for client ids (clocks may keep their limit), and make the
  client column 64-bit.

### 9. Destroying a document that has subdocuments throws — exception

- Confirmed by run: `doc.Destroy()` with two subdocuments: `InvalidOperationException: Collection was modified`
  after the first subdocument. Yjs: both destroyed, two `subdocs` events.
- Cause: `YDoc.Destroy` enumerates `Subdocs` while each `sd.Destroy()` runs a parent transaction whose
  cleanup adds to and removes from `Subdocs`. Yjs iterates `array.from(this.subdocs)`.
- Fix: iterate a copy.

### 10. `YText.ToDelta` — wrong result, wrong event

- A shared type embedded in a text is dropped (confirmed by run): Yjs
  `[{img}, "a", <YMap>, "b"]`, Ycs `[{img}, "ab"]`. The `switch` has no `ContentType` case
  (Yjs: `case ContentType: case ContentEmbed:` with `getContent()[0]`).
- With `computeYChange`, consecutive removed items of one user are one op in Yjs and separate ops in Ycs
  (confirmed: Yjs `["ba" removed, "c"]`, Ycs `["b" removed, "a" removed, "c"]`): the comparison reads
  `cur.State`, which nothing sets, instead of `cur.Type`; `User` is an `int`, client ids are `long`.
- `ToDelta()` without snapshots always opens a transaction with origin `"splitSnapshotAffectedStructs"`
  (confirmed: `AfterTransaction` fires). Yjs opens one only with a snapshot, with origin `'cleanup'`.
- `ToDelta()` on a text that is not in a document throws `NullReferenceException` (confirmed); Yjs returns `[]`.

### 11. v1 JSON with a lone surrogate escape is rejected — exception

- Confirmed by run: Yjs `insertEmbed(0, { v: 'a\uD83Db' })`, v1 update. Ycs:
  `YUpdateException (InvalidOperationException)`. `JSON.stringify` writes `"\ud83d"`, `JSON.parse` accepts it,
  `Utf8JsonReader.GetString` does not. The v2 form of the same value is read (the lone surrogate is U+FFFD
  already, as in Yjs).
- Fix: unescape strings in `YJson.Parse` by hand (or fall back to it when `GetString` throws).

### 12. PermanentUserData — wrong result, untrusted-input hole, order

- Client ids are `int`: ids of 2^31 and above written by Yjs peers (half of all Yjs client ids) are ignored
  by `AddClientId`, and `GetUserByClientId(int)` cannot ask for them. By reading.
- `SetUserMapping` casts what other peers wrote: `(YMap)users.Get(...)`, `(YArray)user.Get("ids")`.
  Confirmed by run: a peer that sets `users.alice = "oops"` makes `ApplyUpdate` throw `InvalidCastException`
  (`YMap.Get` on a missing key throws `KeyNotFoundException`), and every later local deletion throws again
  from the `AfterTransaction` handler.
- Yjs defers both handlers with `setTimeout`; Ycs writes inside the observer and inside `AfterTransaction`,
  so the writes are in the same batch. Same content, different transaction and update order. The filter has
  one parameter (Yjs: transaction and delete set).

### 13. Events of the UndoManager — wrong event

- A change merged into the last stack item fires `stack-item-updated` in Yjs and `StackItemAdded` in Ycs
  (confirmed: Yjs `added, updated, added, popped, cleared, added`; Ycs `added, added, ...`).
- By reading: `StackCleared` fires after the clearing transaction's callback instead of inside it (same
  relative order to the other events). When `Undo()` runs inside an outer transaction, the popped event gets
  the live dictionary, which is emptied when the transaction ends.

### 14. Snapshot-aware reads — wrong result (small), port-only behaviour

- `YXmlElement.GetAttributes(snapshot)` drops attributes whose value is null (confirmed: Yjs
  `{a: 'v', n: null}`, Ycs `{a: 'v'}`), and does not split structs first.
- `ToArraySnapshot`, `GetSnapshot`, `GetAllSnapshot` open a transaction and split the structs the snapshot
  cuts; Yjs' `typeListToArraySnapshot`, `typeMapGetSnapshot`, `typeMapGetAllSnapshot` only read. The port's
  results are the better ones for merged structs; the cost is a transaction (events) on a read.

### 15. Sync protocol — exception

- `readSyncStep2` / `readUpdate` in y-protocols catch the error of `applyUpdate`, pass it to
  `errorHandler` and log it; the message loop continues. Ycs lets `YUpdateException` propagate, with no handler
  parameter (documented in `docs/networking.md`).

### 16. Decoding is more lenient or stricter than lib0 in places — cosmetic to low

- Invalid UTF-8: lib0's `TextDecoder` is `fatal`, the update is rejected; Ycs replaces with U+FFFD and
  stores it (confirmed: `ff 74 72` becomes `"�tr"`, re-encoded as `ef bf bd 74 72`).
- Yjs reads the v2 feature flag as a var-uint, Ycs as one byte.
- `readVarInt` at the end of input returns 0 in lib0, throws in Ycs; a string in the v2 string column that
  is longer than the column is cut in lib0, throws in Ycs; an unknown relative-position kind gives an
  empty position in Yjs, throws in Ycs. All hardening.

### 17. Outside this area, found by the undo fuzz: merging to the left stops after one struct — wrong bytes

- Scenario (confirmed by run, fuzz mode 2 seed 165 of the first run): a nested map whose key has three
  deleted, kept items and one live item is deleted in an untracked transaction. After garbage collection
  Yjs has one `GC` of length 4, Ycs has `GC 1, GC 1, GC 2`; `encodeStateAsUpdate` differs.
- Cause: Yjs 13.6 `tryToMergeWithLefts` keeps merging to the left, also beyond the deleted range;
  `DeleteSet.TryToMergeWithLeft` merges one pair (`DeleteSet.cs:196-240`, also used from
  `Transaction.CleanupTransaction`).
- Also outside: `YMap.Get` of a missing key throws `KeyNotFoundException` (Yjs: `undefined`);
  `DeleteSet.IterateDeletedStructs` has neither the unknown-client nor the `del.clock < clockState` guard of Yjs.

## Remaining untrusted-input holes (non-library exceptions)

All confirmed by run unless noted.

| input | call | exception |
|---|---|---|
| decoded snapshot, state vector names an unknown client | `CreateDocFromSnapshot`, `RestoreDocument` | `KeyNotFoundException` |
| decoded snapshot, clock beyond the store | same | `InvalidOperationException` |
| decoded snapshot, delete set names an unknown client | `YText.ToDelta(snapshot)`, `ToArraySnapshot`, `GetSnapshot`, `GetAllSnapshot` | `KeyNotFoundException` |
| decoded snapshot, delete set starts beyond the store | same | `InvalidOperationException` |
| JSON position, `item`/`type` not an object | `CreateRelativePositionFromJSON` | `InvalidCastException` |
| JSON position, `client`/`clock` missing | same | `KeyNotFoundException` |
| JSON position, `assoc` out of range or not a number | same | `OverflowException` / `FormatException` (by reading) |
| JSON position, negative clock | `CreateAbsolutePositionFromRelativePosition` | `InvalidOperationException`; in Debug an assertion in `ID` ends the process |
| JSON position with neither item, type nor name | `ToArray`, `CreateAbsolutePositionFromRelativePosition` | `System.Exception` without a message (Yjs: `unexpectedCase`) |
| `users` map written by a peer | `PermanentUserData.SetUserMapping` observers | `InvalidCastException`, `KeyNotFoundException` out of `ApplyUpdate` |

Safe (a `YUpdateException`): `RelativePosition.Read` (truncated, empty, unknown kind), `Snapshot.DecodeSnapshot*`,
`SnapshotContainsUpdate*`, `SyncProtocol.ReadSyncMessage` (unknown type, truncated, bad state vector, bad update),
`YDocOptions.Read`.

## Value round trip

JS value written by Yjs in a `Y.Map` (ContentAny), read by Ycs, re-encoded by Ycs (v1 and v2, and v2 read then v1 written). Confirmed by run.

| JS value | tag | C# value | bytes after re-encoding |
|---|---|---|---|
| `1` | 125 | `int 1` | equal |
| `1.5`, `0.5`, `Math.fround(0.1)` | 124 float32 | `double` | equal |
| `0.1`, `5e-324`, `1e21`, `2**31 + 1`, `2**40 + 1` | 123 float64 | `double` | equal |
| `2**31`, `-(2**31)`, `2**40`, `2**53` | 124 float32 | `double` | equal |
| `-(2**31) + 1` | 125 | `int` | equal |
| `-0` | 125 (`0x40`) | `double -0.0` | equal |
| `NaN` | 123 | `double.NaN` | equal (canonical NaN written) |
| `Infinity`, `-Infinity` | 124 | `double` | equal |
| `undefined` | 127 | `null` | **differs**: written as 126 (null) |
| `null`, `true`, `false` | 126, 120, 121 | `null`, `bool` | equal |
| `12n`, `-(2n**63n)` | 122 | `BigInteger` | equal (Yjs wraps wider bigints to 64 bits; Ycs throws on write) |
| `'str'`, `''`, surrogate pair | 119 | `string` | equal |
| `'a\uD83Db'` (lone surrogate) | 119, U+FFFD written by Yjs | `"a�b"` | equal |
| `Uint8Array` | ContentBinary / 116 | `byte[]` | equal |
| `Float32Array` (inside an array) | 118, keys `"0"`, `"1"` | `Dictionary` | equal |
| `{ b: 1, 1: 2, a: 3 }` | 118, order `1, b, a` | `Dictionary`, same order | equal |
| `[1, [2, { x: undefined, y: null }]]` | 117 | `List<object>` | **differs** only in the `undefined` |
| `new Date(0)` | 118, no keys | empty `Dictionary` | equal |
| `{}`, `[]` | 118, 117 | empty | equal |

JSON content (v1 embeds and format values; v2 writes them as `any`):

| JS value | v1 text | C# | re-encoded |
|---|---|---|---|
| `1.0`, `1e21`, `1.5e-7` | `1`, `1e+21`, `1.5e-7` | `int`, `double` | equal |
| nested objects, escapes (` `, `"`, `\\`, `\n`) | | | equal |
| `new Date(0)` | `"1970-01-01T00:00:00.000Z"` (v2: `{}`) | `string` (v2: empty dictionary) | equal per format, as in Yjs |
| `-0`, `NaN` | `0`, `null` (v2: `-0`, `NaN`) | `int 0`, `null` | equal per format |
| `Uint8Array` | `{"0":1,"1":2}` | `Dictionary` | equal |
| `{ a: undefined, b: [undefined] }` | `{"b":[null]}` | | v1 equal; v2 differs (`undefined` to `null`), and v2 read then v1 written gives `"a":null` where Yjs omits `a` |
| string with a lone surrogate | `"\ud83d"` | | **v1 update rejected** (finding 11) |

Written by Ycs, by reading:

- A dictionary is written in insertion order; Yjs writes integer-like keys first. `{"b":1,"1":2}` from Ycs
  is `b, 1` on the wire (confirmed) and `1, b` once a Yjs peer re-encodes it. Same value, different bytes.
- `long` beyond 31 bits is written as a float, as JavaScript does; beyond 2^53 - 1 it throws. Tag 125 with a
  value beyond 31 bits (not written by lib0) is read as `long` and re-encoded as a float.
- There is no `undefined`: a C# peer cannot write one, and turns every one it relays into `null`.

## Function table

| JS | C# | verdict |
|---|---|---|
| **utils/UndoManager.js** | | |
| `StackItem` | `StackItem` | DIFFERS (1) |
| `clearUndoManagerStackItem` | local `clearItem` in `Clear` | identical |
| `popStackItem` | `PopStackItem` | DIFFERS (1, 2) |
| constructor, option defaults | constructors | equivalent |
| `afterTransactionHandler` | `OnAfterTransaction` | DIFFERS (1, 13) |
| tracked-origin test (`has(origin)`, `has(origin.constructor)`) | `Contains(Origin)`, `Contains(Origin.GetType())` | equivalent (exact type, as Yjs; a falsy origin such as `""` or `0` is not matched by constructor in Yjs) |
| scope test (`changedParentTypes.has(type)` or doc) | `ScopeChanged`, `InScope` | identical |
| `captureTimeout`, merge condition (`lastChange > 0`) | `DateTime.MinValue` | equivalent |
| `captureTransaction` | | not ported |
| `ignoreRemoteMapChanges` | | not ported |
| `deleteFilter` | `_deleteFilter` | identical |
| `addToScope` | `AddToScope` | equivalent (throws on a wrong entry, no other-document warning) |
| `addTrackedOrigin`, `removeTrackedOrigin` | same names | identical |
| `clear` | `Clear` | equivalent |
| `stopCapturing` | `StopCapturing` | identical |
| `undo`, `redo` | `Undo`, `Redo` | identical |
| `canUndo`, `canRedo` | `CanUndo`, `CanRedo` | identical |
| `destroy` | `Destroy` | equivalent |
| `stack-item-added` | `StackItemAdded` | equivalent |
| `stack-item-updated` | | not ported (13) |
| `stack-item-popped` | `StackItemPopped` | equivalent |
| `stack-cleared` | `StackCleared` | equivalent |
| `undoStack`, `redoStack`, `currStackItem` | `Count` only | not ported |
| **structs/Item.js as used by undo** | | |
| `redoItem` | `Transaction.RedoItem` | DIFFERS (3) |
| `followRedone` | `StructStore.FollowRedone` | identical |
| `keepItem` | `Item.KeepItemAndParents` | identical |
| `isParentOf` | `UndoManager.IsParentOf` | identical |
| `ContentType.copy` / `YText._copy` | `ContentType.Copy` / missing `YText.InternalCopy` | DIFFERS (4) |
| **utils/Snapshot.js** | | |
| `Snapshot` | `Snapshot` | identical |
| `equalSnapshots` | `Equals`, `EqualSnapshots` | equivalent |
| `encodeSnapshotV2`, `encodeSnapshot` | `EncodeSnapshotV2`, `EncodeSnapshot` | identical (bytes equal) |
| `decodeSnapshotV2`, `decodeSnapshot` | `DecodeSnapshotV2`, `DecodeSnapshot` | identical |
| `createSnapshot`, `emptySnapshot` | `CreateSnapshot`, `Empty` | identical |
| `snapshot(doc)` | `YDoc.CreateSnapshot` | identical |
| `isVisible` | `Item.IsVisible` | identical |
| `splitSnapshotAffectedStructs` | `Transaction.SplitSnapshotAffectedStructs` | equivalent (holes above) |
| `createDocFromSnapshot` | `YDoc.CreateDocFromSnapshot` | equivalent (holes above) |
| `snapshotContainsUpdateV2`, `snapshotContainsUpdate` | `SnapshotContainsUpdateV2`, `SnapshotContainsUpdate` | equivalent (reads all structs, checks the last per client) |
| | `Snapshot.RestoreDocument` | port-only |
| `typeListToArraySnapshot` | `YArrayBase.ToArraySnapshot` | identical |
| `typeListForEachSnapshot` | `ForEachSnapshot` | identical |
| `typeMapGetSnapshot` | `TypeMapGetSnapshot`, `GetSnapshot` | identical |
| `typeMapGetAllSnapshot` | `GetAllSnapshot` | identical |
| `YXmlElement.getAttributes(snapshot)` | `GetAttributes(snapshot)` | DIFFERS (14) |
| `YText.toDelta(snapshot, prevSnapshot, computeYChange)` | `YText.ToDelta` | DIFFERS (10) |
| **utils/RelativePosition.js** | | |
| `RelativePosition`, `AbsolutePosition`, `createAbsolutePosition` | same | identical |
| `createRelativePosition` | constructor | identical |
| `createRelativePositionFromTypeIndex` | `FromTypeIndex` | DIFFERS (5) |
| `relativePositionToJSON` | `RelativePositionToJSON` | equivalent (`tname ""` kept, Yjs drops it) |
| `createRelativePositionFromJSON` | `CreateRelativePositionFromJSON` | DIFFERS (holes) |
| `writeRelativePosition`, `encodeRelativePosition` | `Write`, `ToArray` | identical |
| `readRelativePosition`, `decodeRelativePosition` | `Read` | equivalent |
| `getItemWithOffset` | `GetItemWithOffset` | identical |
| `createAbsolutePositionFromRelativePosition` (follow redone and `followUndoneDeletions`) | `TryCreateFromRelativePosition` | identical (confirmed after undo, redo, undo: indexes 8 and 11, state bytes equal) |
| `compareRelativePositions` | `CompareRelativePositions`, `Equals` | identical |
| **utils/PermanentUserData.js** | | |
| constructor, `initUser` | constructor, `InitUser` | DIFFERS (12: ids of 2^31 and above) |
| `setUserMapping` | `SetUserMapping` | DIFFERS (12) |
| `getUserByClientId` | `GetUserByClientId` | DIFFERS (12) |
| `getUserByDeletedId` | `GetUserByDeletedId` | identical |
| **utils/Doc.js, structs/ContentDoc.js, subdocuments** | | |
| options `guid`, `gc`, `gcFilter`, `meta`, `autoLoad` | `YDocOptions` | identical |
| option `shouldLoad` | `ShouldLoad = AutoLoad` | DIFFERS (6) |
| option `collectionid` | | not ported |
| `generateNewClientId` | `GenerateNewClientId` | equivalent (31 bits) |
| `load` | `Load` | identical |
| `getSubdocs` | internal `Subdocs` | not ported |
| `getSubdocGuids` | `GetSubdocGuids` | identical |
| `get`, `getArray`, `getMap`, `getText`, `getXml*` | `Get<T>` and wrappers | identical |
| `destroy` | `Destroy` | DIFFERS (6, 9) |
| `isLoaded`, `isSynced`, `whenLoaded`, `whenSynced`, `load` and `sync` events, `isDestroyed` | | not ported |
| `ContentDoc` constructor | constructor | DIFFERS (6: throws) |
| `ContentDoc.copy`, `integrate`, `delete`, `gc`, `getContent` | same | identical |
| `ContentDoc.write` | `ContentDoc.Write`, `YDocOptions.Write` | DIFFERS (6) |
| `readContentDoc`, `createDocFromOpts` | `ContentDoc.Read`, `YDocOptions.Read` | DIFFERS (6: `meta`) |
| subdocs block of `cleanupTransactions` | end of `CleanupTransaction` | DIFFERS (6: client id) |
| **utils/UpdateEncoder.js, UpdateDecoder.js** | | |
| `DSEncoderV1`, `UpdateEncoderV1` | same | identical |
| `UpdateEncoderV1.writeJSON` | `YJson.Stringify` | equivalent (bytes equal for every value tried) |
| `DSEncoderV2`, `UpdateEncoderV2` (columns, order, `toUint8Array`) | same | identical |
| `writeKey` (the key map is never filled in Yjs) | `WriteKey` | identical |
| `DSDecoderV1`, `DSDecoderV2` | same | identical |
| `UpdateDecoderV1` | same | DIFFERS (8) |
| `UpdateDecoderV1.readJSON` | `YJson.Parse` | DIFFERS (11) |
| `UpdateDecoderV2` constructor and columns | `ReadColumns` | DIFFERS (7, 8) |
| `readKey` | `ReadKey` | identical |
| `ContentAny` read and write | `ContentAny` | identical |
| `ContentJSON` read and write | `ContentJson` | equivalent (`undefined` read as null, written as `null`) |
| **lib0** | | |
| `writeVarUint`, `writeVarInt` (negative zero) | `Lib0Writer` | identical |
| `writeVarString` (native path; lone surrogate to U+FFFD) | `WriteVarString` | identical; the polyfill path (throws `URIError`) is not reproduced |
| `writeUint8Array`, `writeVarUint8Array`, float32/float64/bigint big-endian | same | identical |
| `writeAny` | `AnyEncoding.WriteAny` | equivalent (closed set; no `undefined`) |
| `readVarUint` | `ReadVarUint` | DIFFERS (8) |
| `readVarInt` | `ReadVarInt` | equivalent |
| `readVarString` | `ReadVarString` | DIFFERS (16) |
| `readAny` | `AnyEncoding.ReadAny` | DIFFERS (`undefined` to null) |
| `RleEncoder` / `RleDecoder` | same | identical |
| `UintOptRleEncoder` / `UintOptRleDecoder` | same | identical up to 2^32 (8) |
| `IntDiffOptRleEncoder` / `IntDiffOptRleDecoder` | same | identical |
| `StringEncoder` / `StringDecoder` | same | equivalent (a pair split over two strings is joined, as Yjs' joined string) |
| `IntDiffEncoder`, `RleIntDiffEncoder`, `IncUintOptRleEncoder` and decoders | same | not used by the update format; not compared |
| **y-protocols/sync.js** | | |
| `writeSyncStep1`, `writeSyncStep2`, `writeUpdate` | same | identical |
| `readSyncStep1` | `ReadSyncStep1` | identical |
| `readSyncStep2`, `readUpdate` | same | DIFFERS (15) |
| `readSyncMessage` | `ReadSyncMessage` | equivalent |
| | `v2` parameter of the sync functions | port-only |
| **y-protocols/awareness.js** | | not ported (below) |
| **utils/logging.js** | | no behaviour; the warnings do not exist |

## Not ported

- UndoManager: `captureTransaction`, `ignoreRemoteMapChanges`, `stack-item-updated`, public `undoStack` /
  `redoStack` / `currStackItem`, the other-document warning.
- Doc: `collectionid`, `shouldLoad` option, `getSubdocs`, `isLoaded`, `isSynced`, `whenLoaded`, `whenSynced`,
  the `load` and `sync` events, `isDestroyed`; the subdocument does not take the parent's client id.
- `undefined` as a value.
- Awareness, entirely. A faithful port needs:
  - state: `clientID` (the document's), `states: Map<clientID, object>`, `meta: Map<clientID, { clock, lastUpdated }>`;
    the constructor calls `setLocalState({})`.
  - `getLocalState`, `setLocalState(state)` (clock is 0 for the first, then + 1; `null` removes the state;
    `added` if there was none, otherwise `updated`, and in `change` only when not deep-equal),
    `setLocalStateField`, `getStates`.
  - a timer every `outdatedTimeout / 10` (3 s): renew the local state when it is older than
    `outdatedTimeout / 2` (15 s); remove remote states older than `outdatedTimeout` (30 s) with origin `'timeout'`.
  - `removeAwarenessStates(awareness, clients, origin)`: delete the states, for the own client raise the clock, fire both events.
  - `encodeAwarenessUpdate(awareness, clients, states)`: var-uint count, then per client var-uint id,
    var-uint clock, var-string `JSON.stringify(state)` (`null` for a removed state). The state is JSON text,
    so it needs `YJson` (number formatting, key order, lone surrogates as in finding 11), and client ids need the
    width of finding 8.
  - `modifyAwarenessUpdate(update, modify)`.
  - `applyAwarenessUpdate(awareness, update, origin)`: accept when `currClock < clock`, or when the clocks are
    equal, the state is null and a state exists; a null state for the own client while it has a state raises
    the clock instead of deleting; `added` when there was no meta and the state is not null, `removed` when there
    was meta and the state is null, otherwise `updated` (and in `change` only when not deep-equal).
  - events `change` (`added`, filtered `updated`, `removed`; only when one is non-empty) and `update`
    (all three; from `setLocalState` always), each with the origin; `destroy` sets the local state to null,
    stops the timer, and runs when the document is destroyed.
  - the message type of y-websocket (1 = awareness, 3 = query awareness) is the provider's, not y-protocols'.

## Least verified

1. Undo and redo with XML types, subdocuments, embeds, more than two clients, `deleteFilter`, and a
   document-wide scope: the fuzz covers text (with formatting), map, array and nested map and text, with one remote client.
   `Clear` and the `keep` flags after it are read, not run.
2. PermanentUserData: read only, apart from the hostile overwrite; no run against Yjs with the deferred writes.
3. Subdocument lifecycle beyond the events tried (load, destroy and re-creation of a deleted subdocument,
   redo of a deleted subdocument, nested subdocuments), and the v1 JSON number formatter over the full
   double range (a dozen values tried).
