# Walkthrough of undo, snapshots, positions, subdocuments, encoders and protocols

How `UndoManager`, snapshots, relative positions, `PermanentUserData`, subdocuments, the update encoders and decoders, the
lib0 primitives and the sync protocol map to Yjs 13.6.32, lib0 0.2 and y-protocols 1.0.7, and where Ycs differs on purpose.
The core is in [walkthrough-core.md](walkthrough-core.md), the shared types in [walkthrough-types.md](walkthrough-types.md).

Verdicts: **identical**, **equivalent** (different code, the same observable behavior on valid input), **differs** (see
[Differences from Yjs](#differences-from-yjs)), **not ported**, **port-only**.

## Function table

| JS | C# | Verdict |
| --- | --- | --- |
| **utils/UndoManager.js** | | |
| `StackItem` (`insertions`, `deletions`) | `StackItem` (internal `Insertions`, `Deletions` delete sets; public `Meta`) | identical |
| `clearUndoManagerStackItem` | `UndoManager.ClearStackItem` | identical |
| `popStackItem` | `UndoManager.PopStackItem` | identical (a stack item that changes nothing pops the next one) |
| constructor, option defaults | the three constructors, `UndoManagerOptions` | equivalent |
| `afterTransactionHandler` | `UndoManager.OnAfterTransaction` | identical |
| tracked origins (`has(origin)`, `has(origin.constructor)`) | `AddTrackedOrigin`, `RemoveTrackedOrigin`, `UndoManagerOptions.TrackedOrigins` | equivalent (the exact type, as in Yjs) |
| scope test | `UndoManager.ScopeChanged`, `InScope` | identical |
| `captureTimeout`, the merge condition | `CaptureTimeout`, `TimeProvider` | equivalent |
| `captureTransaction` | `UndoManagerOptions.CaptureTransaction` | identical |
| `ignoreRemoteMapChanges` | `IgnoreRemoteMapChanges` (option and property) | identical |
| `deleteFilter` | `UndoManagerOptions.DeleteFilter` (takes an `ItemView`) | identical |
| `addToScope` | `AddToScope` (a type, several types, or the document) | equivalent (a wrong entry throws; no other-document warning) |
| `clear` | `Clear(undoStack, redoStack)` | equivalent |
| `stopCapturing` | `StopCapturing` | identical |
| `undo`, `redo` | `Undo`, `Redo` | identical |
| `canUndo`, `canRedo` | `CanUndo`, `CanRedo` | identical |
| `destroy` | `Dispose` (also when the document is destroyed) | equivalent |
| events `stack-item-added`, `stack-item-updated`, `stack-item-popped`, `stack-cleared` | `StackItemAdded`, `StackItemUpdated`, `StackItemPopped`, `StackCleared` | equivalent |
| `undoStack`, `redoStack`, `currStackItem` | `UndoStack`, `RedoStack`, `CurrentStackItem` (read-only) | equivalent |
| **structs/Item.js as the undo manager uses it** | | |
| `redoItem` | `UndoManager.RedoItem` | identical |
| `isDeletedByUndoStack` | `UndoManager.IsDeletedByStack` | identical |
| `followRedone` | `StructStore.FollowRedone` | identical |
| `keepItem` | `ItemRef.KeepItemAndParents` | identical |
| `isParentOf` | `UndoManager.IsParentOf` | identical |
| `ContentType.copy`, `YType._copy` | `ContentType.Copy`, `YType.InternalCopy` (every shared type overrides it) | identical |
| **utils/Snapshot.js** | | |
| `Snapshot`, `createSnapshot`, `emptySnapshot` | `Snapshot`, `Snapshot.Empty` | identical |
| `snapshot(doc)` | `YDoc.CreateSnapshot` | identical |
| `equalSnapshots` | `Snapshot.Equals` | equivalent |
| `encodeSnapshotV2`, `encodeSnapshot` | `Snapshot.Encode(format)` | identical (the bytes are equal) |
| `decodeSnapshotV2`, `decodeSnapshot` | `Snapshot.Decode(bytes, format)` | identical |
| `isVisible` | `ItemRef.IsVisible` | identical |
| `splitSnapshotAffectedStructs` | `Transaction.SplitSnapshotAffectedStructs` | equivalent |
| `createDocFromSnapshot` | `YDoc.CreateDocFromSnapshot` | equivalent |
| `snapshotContainsUpdateV2`, `snapshotContainsUpdate` | `Snapshot.ContainsUpdate(update, format)` | equivalent (reads all structs, checks the last per client) |
| `typeListToArraySnapshot` | `YArray.ToArray(snapshot)`, `YXmlFragment.ToArray(snapshot)` | identical |
| `typeMapGetSnapshot`, `typeMapGetAllSnapshot` | `YMap.TryGetValue(key, snapshot, ...)`, `YMap.ToDictionary(snapshot)` | identical |
| `YXmlElement.getAttributes(snapshot)` | `YXmlElement.GetAttributes(snapshot)` | identical |
| `YText.toDelta(snapshot, prevSnapshot, computeYChange)` | `YText.ToDelta(snapshot, previous, computeYChange)` | identical |
| **utils/RelativePosition.js** | | |
| `RelativePosition`, `AbsolutePosition`, `createAbsolutePosition` | `RelativePosition`, `AbsolutePosition` | identical |
| `createRelativePositionFromTypeIndex` | `RelativePosition.FromIndex` | identical |
| `relativePositionToJSON`, `createRelativePositionFromJSON` | `WriteJson`, `FromJson` | equivalent (the JSON of a root type's name `""` is kept, Yjs drops it) |
| `writeRelativePosition`, `encodeRelativePosition` | `Encode` | identical |
| `readRelativePosition`, `decodeRelativePosition` | `Decode` | equivalent (a truncated or unknown form is a `YUpdateException`) |
| `getItemWithOffset` | `GetItemWithOffset` | identical |
| `createAbsolutePositionFromRelativePosition` | `RelativePosition.TryResolve` | identical (follows redone items and, by default, undone deletions) |
| `compareRelativePositions` | `RelativePosition.Equals` | identical |
| **utils/PermanentUserData.js** | | |
| constructor, `initUser`, `setUserMapping` | `PermanentUserData`, `SetUserMapping` | equivalent (see [Differences](#differences-from-yjs)) |
| `getUserByClientId`, `getUserByDeletedId` | `GetUserByClientId`, `GetUserByDeletedId` | identical |
| **utils/Doc.js, structs/ContentDoc.js, subdocuments** | | |
| options `guid`, `gc`, `gcFilter`, `meta`, `autoLoad`, `shouldLoad`, `collectionid` | `YDocOptions` | identical |
| `load` | `Load` | identical |
| `getSubdocs`, `getSubdocGuids` | internal `Subdocs`, `GetSubdocGuids` | equivalent |
| `get`, `getArray`, `getMap`, `getText`, `getXml*` | `Get<T>` (internal), `GetArray`, `GetMap`, `GetText`, `GetXml*` | identical |
| `destroy` | `Destroy` | equivalent (the subdocuments are destroyed from a copy of the set) |
| `isLoaded`, `isSynced`, `whenLoaded`, `whenSynced`, the `load` and `sync` events, `isDestroyed` | none | not ported |
| `ContentDoc.write`, `readContentDoc`, `createDocFromOpts` | `ContentDoc.Write`, `ContentDoc.Read`, `YDocOptions` | identical (`gc` only when false, `autoLoad` only when true, `meta` only when set, never `guid`; a `guid` in the options overrides the item's) |
| the subdocs block of `cleanupTransactions` | the end of `Transaction.CleanupTransaction` | identical (an added subdocument takes the parent's client id) |
| **utils/UpdateEncoder.js, UpdateDecoder.js** | | |
| `DSEncoderV1`, `UpdateEncoderV1`, `DSEncoderV2`, `UpdateEncoderV2` | `UpdateEncoderV1`, `UpdateEncoderV2` | identical (V2: the columns, their order and `toUint8Array`) |
| `UpdateEncoderV1.writeJSON` | `YJson.Stringify` | equivalent (the bytes are equal for every value tried) |
| `writeKey` | `WriteKey` | identical (Yjs' key map is never filled) |
| `DSDecoderV1`, `DSDecoderV2`, `UpdateDecoderV1`, `UpdateDecoderV2` | `UpdateDecoderV1`, `UpdateDecoderV2` (`ReadColumns`) | equivalent (bounds on every length and count) |
| `UpdateDecoderV1.readJSON` | `YJson.Parse` | equivalent (a lone surrogate escape is read as Yjs reads it) |
| `readKey` | `ReadKey` | identical |
| `ContentAny` read and write | `ContentAny`, `AnyEncoding` | identical |
| `ContentJSON` read and write | `ContentJson` | equivalent (`undefined` is read as `null` and written as `null`) |
| **lib0** | | |
| `writeVarUint`, `writeVarInt` (negative zero) | `Lib0Writer` | identical |
| `writeVarString` (the native path; a lone surrogate becomes U+FFFD) | `Lib0Writer.WriteVarString` | identical (the polyfill path, which throws `URIError`, is not reproduced) |
| `writeUint8Array`, `writeVarUint8Array`, float32, float64 and bigint, big-endian | `Lib0Writer` | identical |
| `writeAny` | `AnyEncoding.WriteAny` | equivalent (a closed set of value kinds; no `undefined`) |
| `readVarUint` | `Lib0Reader.ReadVarUint` | equivalent (up to 2^53 - 1; a longer var-uint is a `YUpdateException`) |
| `readVarInt` | `ReadVarInt` | equivalent |
| `readVarString` | `ReadVarString` | equivalent (invalid UTF-8 is a `YUpdateException`; lib0's `TextDecoder` is fatal too) |
| `readAny` | `AnyEncoding.ReadAny` | equivalent (`undefined` is read as `null`) |
| `RleEncoder`, `RleDecoder`, `UintOptRleEncoder`, `UintOptRleDecoder`, `IntDiffOptRleEncoder`, `IntDiffOptRleDecoder` | the same names | identical |
| `StringEncoder`, `StringDecoder` | the same names | equivalent (a surrogate pair split over two strings is joined, as in Yjs' joined string) |
| `IntDiffEncoder`, `RleIntDiffEncoder`, `IncUintOptRleEncoder` and their decoders | none | not used by the update format; not ported |
| **y-protocols/sync.js** | | |
| `writeSyncStep1`, `writeSyncStep2`, `writeUpdate` | `SyncProtocol.WriteSyncStep1`, `WriteSyncStep2`, `WriteUpdate` | identical |
| `readSyncStep1`, `readSyncStep2`, `readUpdate`, `readSyncMessage` | `SyncProtocol.ReadSyncMessage` | equivalent (see [Differences](#differences-from-yjs)) |
| none | the `format` parameter of the sync functions | port-only |
| **y-protocols/awareness.js** | none in the library; `samples/Ycs.Server/Awareness` | port in the sample |
| **utils/logging.js** | none | no behavior; the console warnings do not exist |

## Differences from Yjs

- **Undo events.** The stack item events carry a copy of the transaction's `ChangedParentTypes`, so a handler may keep it.
- **PermanentUserData.** Yjs defers its two writes (a user's new deletions, and the data of a user whose entry another peer
  replaced) with `setTimeout`. Ycs runs them once the batch of transactions that caused them has ended, each in a transaction
  of its own. The filter of `SetUserMapping` gets the transaction only: the delete set is internal. Entries of the users map
  that have another shape than Yjs writes (another peer's data) are ignored.
- **Sync protocol.** y-protocols' `readSyncStep2` and `readUpdate` pass an error of the apply to an `errorHandler`, log it
  and carry on. `SyncProtocol.ReadSyncMessage` throws `YUpdateException` and leaves the document as it was
  ([networking.md](networking.md)).
- **Decoding is stricter than lib0** where an input has no valid meaning: a `readVarInt` at the end of the input and a v2
  string longer than its column throw (lib0 returns 0 and cuts), and an unknown relative-position kind throws (Yjs reads an
  empty position). Yjs reads the v2 feature flag as a var-uint, Ycs as one byte.
- **`undefined`** is not a value; see [the table below](#value-round-trip).
- **Dictionaries** are written in insertion order. Yjs writes the integer-like keys of a JavaScript object first, so
  `{"b":1,"1":2}` from Ycs is `b, 1` on the wire and `1, b` once a Yjs peer re-encodes it: the same value, other bytes.
- **Numbers.** A `long` beyond 31 bits is written as a float, as JavaScript does; beyond 2^53 - 1 it throws (use
  `BigInteger`, a `bigint`: 64 bits, Yjs wraps wider ones, Ycs throws on write). Tag 125 with a value beyond 31 bits (lib0
  does not write it) is read as a `long` and re-encoded as a float.

## Value round trip

A JavaScript value written by Yjs in a `Y.Map` (`ContentAny`), read by Ycs as a `YValue`, and re-encoded by Ycs in v1 and
v2 (and v2 read, v1 written). The re-encoded bytes are equal except where marked.

| JS value | tag | `YValue` kind | bytes after re-encoding |
| --- | --- | --- | --- |
| `1` | 125 | Number | equal |
| `1.5`, `0.5`, `Math.fround(0.1)` | 124 float32 | Number | equal |
| `0.1`, `5e-324`, `1e21`, `2**31 + 1`, `2**40 + 1` | 123 float64 | Number | equal |
| `2**31`, `-(2**31)`, `2**40`, `2**53` | 124 float32 | Number | equal |
| `-(2**31) + 1` | 125 | Number | equal |
| `-0` | 125 (`0x40`) | Number, -0.0 | equal |
| `NaN` | 123 | Number | equal (the canonical NaN is written) |
| `Infinity`, `-Infinity` | 124 | Number | equal |
| `undefined` | 127 | Null | **differs**: written as 126 (null) |
| `null`, `true`, `false` | 126, 120, 121 | Null, Boolean | equal |
| `12n`, `-(2n**63n)` | 122 | BigInteger | equal |
| `'str'`, `''`, a surrogate pair | 119 | String | equal |
| `'a\uD83Db'` (a lone surrogate) | 119, U+FFFD written by Yjs | String | equal |
| `Uint8Array` | `ContentBinary`, or 116 inside a value | Bytes | equal |
| `Float32Array` (inside an array) | 118, keys `"0"`, `"1"` | Object | equal |
| `{ b: 1, 1: 2, a: 3 }` | 118, order `1, b, a` | Object, the same order | equal |
| `[1, [2, { x: undefined, y: null }]]` | 117 | Array | **differs** only in the `undefined` |
| `new Date(0)` | 118, no keys | Object, empty | equal |
| `{}`, `[]` | 118, 117 | Object, Array, empty | equal |

JSON content (a v1 embed or a format value; v2 writes them as `any`):

| JS value | v1 text | re-encoded |
| --- | --- | --- |
| `1.0`, `1e21`, `1.5e-7` | `1`, `1e+21`, `1.5e-7` | equal |
| nested objects, escapes | | equal |
| `new Date(0)` | `"1970-01-01T00:00:00.000Z"` (v2: `{}`) | equal per format, as in Yjs |
| `-0`, `NaN` | `0`, `null` (v2: `-0`, `NaN`) | equal per format |
| `Uint8Array` | `{"0":1,"1":2}` | equal |
| `{ a: undefined, b: [undefined] }` | `{"b":[null]}` | v1 equal; v2 differs (`undefined` becomes `null`), and v2 read then v1 written gives `"a":null` where Yjs omits `a` |
| a string with a lone surrogate | `"\ud83d"` | equal |

## Not ported

- `UndoManager`: the warning for a scope entry of another document.
- `Doc`: `getSubdocs`, `isLoaded`, `isSynced`, `whenLoaded`, `whenSynced`, the `load` and `sync` events, `isDestroyed`.
- `undefined` as a value.
- Awareness is not in the library. The sync server has a port, byte-compatible with y-protocols' awareness
  (`samples/Ycs.Server/Awareness`), that you can copy. The message types of y-websocket (1 awareness, 3 query awareness)
  belong to the provider, not to y-protocols' sync.

## Least verified

- Undo and redo with XML types, subdocuments, embeds, more than two clients, `DeleteFilter` and a document-wide scope: the
  differential undo fuzz (`UndoDifferentialTests`) covers text with formatting, map, array and nested map and text, with one
  remote client.
- `PermanentUserData` against Yjs with the deferred writes: read, not run.
- The v1 JSON number formatter over the full double range: a dozen values tried.
