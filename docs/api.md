# API: Ycs, Yjs and YDotNet

This page compares the public API of Ycs with [Yjs](https://github.com/yjs/yjs) (13.6) and with
[YDotNet](https://github.com/y-crdt/ydotnet), the .NET binding to the Rust implementation
[yrs](https://github.com/y-crdt/y-crdt), and records which changes this fork made and which it did
not. The rule was: keep the Yjs concepts and names recognizable, take what is clearly better and
cheap, and do not rename for taste.

## How the three differ

| Concern | Yjs | YDotNet | Ycs |
| --- | --- | --- | --- |
| Transactions | implicit; `doc.transact(fn, origin)` groups changes | explicit: every call takes a `Transaction` from `doc.WriteTransaction()`, which must be committed or disposed | implicit, as in Yjs: `doc.Transact(tr => ..., origin)` groups changes |
| Indexes | `number` (UTF-16 code units for text) | `uint` | `int` (UTF-16 code units, as in Yjs) |
| Values | any JSON-like value, `Uint8Array`, `bigint`, shared types | tagged `Input` / `Output` classes (`Input.Long(5)`, `output.Long`) | plain CLR values from a closed set: `null`, `bool`, `int`/`long`, `double`, `BigInteger`, `string`, `byte[]`, lists, string-keyed dictionaries, shared types |
| Update format | v1 (`applyUpdate`) by default, v2 (`applyUpdateV2`) | `ApplyV1`/`ApplyV2`, `StateDiffV1`/`StateDiffV2` | v1 by default (`ApplyUpdate`), `…V1`/`…V2` explicitly |
| Subscriptions | `observe(f)` / `unobserve(f)`, `doc.on('update', f)` | `IDisposable Observe(Action<T>)` | .NET events, plus `Observe`/`ObserveDeep` returning `IDisposable` |
| Positions that survive edits | `RelativePosition` | `StickyIndex` | `RelativePosition` / `AbsolutePosition` (Yjs names) |
| Undo | `UndoManager(scope, { trackedOrigins, captureTimeout })` | `UndoManager(doc, branch, options)` | `UndoManager(scope, new UndoManagerOptions { TrackedOrigins, CaptureTimeout })` |
| Threading | single-threaded | the native document is locked per transaction | the caller's job, as in Yjs (see below) |

YDotNet's explicit transactions and tagged values come from the Rust borrow model and the native
boundary; they would make Ycs harder to use without making it safer, so Ycs keeps the Yjs shape.

## Changes made in this fork

| Before | Now | Why |
| --- | --- | --- |
| `ApplyUpdateV2(byte[])` was the only way in upstream; PR #12 added `ApplyUpdateV1` | `ApplyUpdate(byte[])` and `ApplyUpdate(ReadOnlySpan<byte>)` apply **v1**; `ApplyUpdateV1`/`ApplyUpdateV2` take `byte[]` or `ReadOnlySpan<byte>` | v1 is what `Y.applyUpdate` and every y-protocols provider use; spans let callers pass a slice of a network buffer |
| `ApplyUpdateV1(Stream)`, `ApplyUpdateV2(Stream)` | removed | the decoders work on a byte buffer; a stream was copied anyway |
| `EncodeStateAsUpdateV2(byte[] stateVector = null)` | `EncodeStateAsUpdate(ReadOnlySpan<byte> stateVector = default)` (v1), `EncodeStateAsUpdateV1`/`V2(...)`, and overloads `EncodeStateAsUpdate…(IBufferWriter<byte> destination, ReadOnlySpan<byte> stateVector = default)` | Yjs' `encodeStateAsUpdate`; writing into an `IBufferWriter<byte>` (a pipe, a pooled buffer) saves the result array: v1 is written straight into the destination, v2 (whose columns come before the rest) is assembled in pooled buffers and copied into it once; once warm, neither allocates. A malformed state vector throws `YUpdateException` before anything is written. A `byte[]` or `null` still works |
| `EncodeStateVectorV2()` | `EncodeStateVector()` (plus `…V1`, `…V2`, and `EncodeStateVector(IBufferWriter<byte>)`, which allocates nothing) | Yjs' `encodeStateVector`; the bytes are the same in both formats |
| event `UpdateV2`; PR #12 added `UpdateV1` | event `Update` (v1) and `UpdateV2` | Yjs' `doc.on('update')` and `doc.on('updateV2')` |
| `ClientId { get; internal set; }`, random only, `int` | `YDocOptions.ClientId` (`long?`, 0 to 2^53 - 1); `YDoc.ClientId` is a `long`. Client ids up to 2^53 - 1 are read and written everywhere (structs, ids, delete sets, state vectors, the v2 client column) | reproducible tests and benchmarks without reflection (YDotNet has `DocOptions.Id`); Yjs reads var-uints up to 2^53 - 1 and lets applications set `doc.clientID`, yrs uses u64 ids. Generated ids are 32 random bits, as Yjs' `random.uint32()` |
| `void Observe(handler)`, `void ObserveDeep(handler)` (PR #13) | return `IDisposable`; disposing unsubscribes | the YDotNet idiom; makes lambdas easy to unsubscribe. `Unobserve` and `+=`/`-=` still work |
| map/array values: anything, failing at encode time; `Newtonsoft.Json` tokens and reflection for JSON | the closed set of values above, checked when a value is inserted; numbers encoded with JavaScript's rules | Native AOT, byte-identical output, errors at the call that caused them |
| `SyncProtocol.ReadSyncStep2`, `ReadUpdate`, `ReadSyncMessage` let every exception of the apply propagate | an optional `errorHandler` (`Action<Exception>`, last parameter): with one, what reading and applying the update threw goes to it and the call returns; without one it propagates | y-protocols' `errorHandler`; it also always catches and logs, which a library without a logger cannot do quietly |
| `PermanentUserData`: client ids `int` (ids of 2^31 and above, half of Yjs', ignored); an entry of another shape in the users map threw `InvalidCastException` / `KeyNotFoundException` out of `ApplyUpdate` and every later local deletion; the deletions were written inside the deleting transaction's cleanup, before its `Update`; `Doc`, `YUsers` fields | `long` ids (`SetUserMapping(doc, long, …)`, `GetUserByClientId(long)`); such entries are ignored, and `SetUserMapping` replaces one for its own user; the two writes Yjs defers with `setTimeout` run after the batch that caused them, each in its own transaction; `Doc`, `YUsers` properties | Yjs' order of updates, and the users map is written by other peers |
| `UndoManager` tracked origin `Type` matched subclasses | matches that exact type | Yjs compares `origin.constructor` |
| `UndoManager` of Yjs 13.4: a stack item undid every struct between the first and the last state of its transactions (a remote or untracked change made meanwhile too); a stack item that changed nothing ended `Undo()` and was returned; `RedoItem` overwrote the map values of untracked changes and refused those of other clients; a merged change fired `StackItemAdded`; `StackCleared` after the clearing transaction; constructors with positional `captureTimeout`, `deleteFilter`, `trackedOrigins`; `StackItem.BeforeState`/`AfterState`; `Count` | Yjs 13.6's: a stack item holds what its transactions inserted and deleted; `Undo()`/`Redo()` go on to the next item until one changes the document; `redoItem` with `ignoreRemoteMapChanges`; `StackItemUpdated`; `StackCleared` inside that transaction; `UndoManagerOptions` (`CaptureTimeout`, `CaptureTransaction`, `DeleteFilter`, `TrackedOrigins`, `IgnoreRemoteMapChanges`, `Doc`); `UndoStack`, `RedoStack` (oldest first) and `CurrentStackItem` | undo deleted a remote user's text typed within the capture timeout, and documents differed from Yjs'; checked by a differential fuzz against Yjs (`tests/Ycs.Corpus/UndoDifferentialTests.cs`). Migrate: `new UndoManager(scope, 0, filter, origins)` becomes `new UndoManager(scope, new UndoManagerOptions { CaptureTimeout = 0, DeleteFilter = filter, TrackedOrigins = origins })`; `um.Count` becomes `um.UndoStack.Count` |
| `ApplyUpdate…(update, origin, bool local = false)` | `ApplyUpdate…(update, origin)`; the transaction an update is applied in is not local, also the caller's | Yjs' `readUpdateV2` forces `transaction.local = false`; a local transaction kept stale search markers (an insert after a nested apply landed 5 characters early) |
| malformed updates threw `EndOfStreamException`, `InvalidDataException`, `JsonReaderException`, `InvalidCastException`, `NullReferenceException`, `DivideByZeroException`, bare `Exception`, …, some after half the update was applied | `YUpdateException` (a `FormatException`), and the document is unchanged; `TryApplyUpdate` / `TryApplyUpdateV2` return false instead | see [Untrusted input](#untrusted-input) |
| `YDocOptions.Meta`, `YDoc.Meta`: `IDictionary<string, string>` (always null when read from an update) | `object` (any value of the 'any' encoding) | Yjs' `meta` is any value; the old cast always failed, and an update with a `meta` that was not an object was rejected |
| a subdocument's options written as `{ gc, guid, autoLoad }` with a random guid of their own; `ShouldLoad` defaulted to `AutoLoad`; `Destroy` threw with subdocuments and left a deleted subdocument's content without a document; a subdocument kept its own client id | as Yjs 13.6: the options are `{}` plus `gc: false`, `autoLoad: true`, `meta` when they differ from the defaults; reading lets every key override (a `guid` too, as `createDocFromOpts`); `YDocOptions.ShouldLoad` (default true) and `YDoc.ShouldLoad`; a destroyed subdocument is replaced by a fresh document that does not load; an added subdocument takes the parent's client id and `CollectionId` (new, Yjs' `collectionid`) | a Yjs peer reading Ycs' options took the random guid as the subdocument's identity |
| no limit on what an update may cost; pending structs and deletions unbounded | `UpdateLimits` (immutable; `Default`, `Unlimited`): `MaxStructsPerUpdate` (131,072, v1 and v2), `MaxPendingStructs` (131,072), `MaxPendingBytes` (16 MiB); `YDocOptions.Limits` for the document (a subdocument takes its parent's), an optional `limits` parameter of `ApplyUpdate…`/`TryApplyUpdate…` for one update, and of every `UpdateOperations` function and `Snapshot.SnapshotContainsUpdate…` | a v2 update of 30 bytes could make the decoder allocate gigabytes and the document keep half of it, and a peer could send updates that never integrate and fill memory. Migrate: `YDocOptions.MaxPendingBytes = n` becomes `YDocOptions.Limits = new UpdateLimits { MaxPendingBytes = n }`; `UpdateOperations.MaxStructsPerUpdate` (process-wide) becomes the `limits` argument; a valid state larger than the defaults (see [Untrusted input](#untrusted-input)) is applied with `UpdateLimits.Unlimited` or raised limits |
| the conflict loop filled two sets per integrated item (n items after n concurrent ones: n^2 set entries); no bound on its steps | the sets are filled only where an item's origin is another scanned item that is not the one just before; `UpdateLimits.MaxIntegrationSteps` (2^24) per update, beyond which the update is applied in part and `YUpdateException.PartlyApplied` is set | two updates of 28 bytes took 97 s and 111 GiB; see [Untrusted input](#untrusted-input) |
| pending structs and deletions: not visible, and left out of `EncodeStateAsUpdate` | `YDoc.HasPending`; `EncodeStateAsUpdate…` includes them, as Yjs 13.6 does (the pending deletions whole, the pending structs beyond the target state vector, merged into the state) | Yjs exposes `doc.store.pendingStructs`/`pendingDs`; a server that persisted its state and restarted lost what was pending |
| — (Yjs has none either) | `YText.RemoveNonText()`: deletes every embed, nested type, other non-string content and formatting in one transaction; returns whether it changed anything | a plain-text editor (a textarea) bound to a `YText` that other peers may fill with rich content: afterwards `Length` is the string's length and indexes agree. It deletes the items themselves, so nothing is added to the document |
| every changed type built its event (`YTextEvent`, `YMapEvent`, …) and added itself and its ancestors to `Transaction.ChangedParentTypes`, observed or not | only when the type has an `EventHandler`, it or an ancestor a `DeepEventHandler`, or the document an `AfterTransaction`, `AfterTransactionCleanup` or `AfterAllTransactions` handler (an `UndoManager` is one); `Transaction.Changed` is filled as before. When an observer attaches a deep observer or such a handler during the cleanup, the types skipped before build their events then (without calling their own observers), so the late observer sees every event, as in Yjs. `YTextEvent.KeysChanged` and `YXmlEvent.AttributesChanged` are built when read (also after the transaction ended: they hold the event's own set of changed keys, not the transaction) | an unobserved document (a server that applies and relays updates) builds no events; Yjs always does. A handler of `Update` or of another type's events that reads `ChangedParentTypes` sees only the observed types |
| `Transaction.Changed`: a new `HashSet<string>` per changed type; `YMap.Count` walked the map (O(n)) | the types whose children changed and none of their keys share one read-only set (`{ null }`), so treat the sets as read-only; `YMap.Count` is kept as entries are added and deleted (O(1)) | a keystroke changed one list type and allocated a set for it; counting a map is common in bindings |
| `YArrayBase.FindMarker`, `YArrayBase._searchMarkers` and their nested marker types were `protected` | `private protected` | implementation details of the search markers; a subclass outside the assembly cannot use them meaningfully |
| `YText.ToString()` built the string each time, through a `StringBuilder` (twice the text's size) | kept until the text changes (an unchanged text returns the same string; the text holds on to it meanwhile), built in one copy; new `YText.CopyTo(Span<char>)` (returns the characters written; `Length` is always enough room) and `YText.WriteTo(IBufferWriter<char>)` write the text without a string | an editor binding or a server that reads the text after every update |
| the formatting cleanup of an earlier Yjs: run in `YText`'s observer call, before the observers, in a nested transaction; `CleanupFormatting` deleted every format whose value differed from the attributes at the next content; format keys compared with `Equals` (`1` and `1.0` differed, two equal maps did not) | Yjs 13.6's: once per transaction that changed a formatted text remotely, after its `AfterTransaction` handlers, in a transaction of its own (`cleanupYTextAfterTransaction`, `cleanupFormattingGap`, `cleanupContextlessFormattingGap`); values compared as Yjs' `equalAttrs` (JavaScript `===`, maps and lists flat). Also as Yjs: `Format` beyond the end appends one newline per missing character (one fewer before), and an `ApplyDelta` insert after a formatted one goes after its negating format | with concurrent overlapping bold, Ycs deleted three formats and ended `[abcdef bold]` where Yjs deletes none and ends `[ab bold][cd bold][ef]`; the documents of a Ycs and a Yjs peer diverged |
| `ID.Write(Stream)`, `RelativePosition.Write(Stream)`/`Read(Stream)`, `Snapshot.DecodeSnapshot(Stream)` | internal; use `RelativePosition.ToArray()`/`Read(byte[])`, `Snapshot.DecodeSnapshot(byte[])` | these exposed the internal encoding layer |
| a new `Transaction` for every transaction, readable forever, and so were an event's lazily computed `Changes`, `Keys`, `Delta`, `Adds`, `Deletes` | the document reuses its transaction objects: a `Transaction` (and its `BeforeState`, `AfterState`, `Changed`, `ChangedParentTypes`, `Meta`, the list `AfterAllTransactions` passes) is valid until the batch's `AfterAllTransactions` handlers return; reading an ended one, or computing an event's changes after its transaction ended, throws `InvalidOperationException`. Every event handed to a handler (a type's, a deep one's, an `UndoManager` stack event's) is in its transaction's `ChangedParentTypes`, and the transaction lets go of them all when it ends, so a kept event never reads the transaction that reuses the object. Compute what you need in the handler and keep that | a transaction allocated about 2 KB of bookkeeping per keystroke; Yjs also throws when an event's changes are computed after its handler. `UndoManager` stack items and its events keep their own data |
| `Transact(Action<Transaction>, origin, local)` only | also `Transact<TState>(TState state, Action<TState, Transaction> action, object origin = null, bool local = true)` | with a `static` lambda nothing is captured, so no closure is allocated per call |
| `AfterTransaction` before the deep observers; an observer that threw skipped the observers of the other changed types; two transactions an observer started one after the other were merged into one; `SubdocsChanged` after every transaction | as Yjs 13.6: observers, deep observers, then `AfterTransaction`, each called even if one before threw (the first exception is rethrown after the cleanup), and every handler of one type's `EventHandler` or `DeepEventHandler` too (Yjs' `callAll`; of these the last exception propagates); observers in the order the types changed, also after a changed type was deleted; a deep observer's events in a stable order by path length, with `CurrentTarget` and `Path` relative to the observed type, and without events whose target was deleted meanwhile; a transaction started in an observer ends when its `Transact` returns; `SubdocsChanged` only when a subdocument was added, removed or loaded | Yjs' `cleanupTransactions` and `transact` |
| `YMap.Get` of a missing key threw `KeyNotFoundException` | returns null (Yjs: `undefined`); `YMap.TryGet(key, out value)` tells a missing key from a null value | Yjs' `get` |
| `YText.InsertEmbed` of a shared type threw `NotSupportedException`; a type a Yjs peer embedded was missing from `ToDelta` and the event delta | a shared type is an embed as in Yjs: `InsertEmbed`, `ApplyDelta`, `ToDelta`, `YTextEvent.Delta` | the indexes of a binding were off by one after such an embed |
| `YTextEvent.Changes` was the list change set (inserts as characters, the inserted and deleted items) | Yjs' text form: `Keys` and `Delta` (the same list as `YTextEvent.Delta`), no items | Yjs' `YTextEvent#changes` |
| `YTextChangeAttributes` had `State` and an `int` `User`; `ToDelta(snapshot, prevSnapshot, computeYChange)` compared `State` | no `State`; `User` is a `long?` (null when `computeYChange` does not set it), and `Type` is compared | text added after text removed by the same user was reported as removed; client ids above 2^31 never matched |
| `YText.ToDelta()` opened a transaction and threw outside a document | reads only (a transaction, with origin `"cleanup"`, only with snapshots); `[]` for a text outside a document, so `Clone` and `YXmlText.ToString` work there too | Yjs' `toDelta` |
| `YArray.Push`/`Add` inserted at `Length` | appends after the last item, deleted ones included, as Yjs' `push` | the bytes differed, and the place among concurrent appends could |
| `Insert` beyond the length inserted at the front; on a type outside a document `Insert`/`Delete` out of range threw; `Slice(start, end)` with `start > end` threw | `Insert` beyond the length throws `ArgumentOutOfRangeException` (Yjs: "Length exceeded!"); outside a document they splice as JavaScript; `Slice` never throws | Yjs' `typeListInsertGenerics`, `splice`, `typeListSlice` |
| `YXmlElement.ToString` wrote values with .NET formatting and the current culture (`True`, `1,5`); `GetAttributes(snapshot)` dropped null values | values as JavaScript's `String(value)` (`true`, `1.5`, `null`, `[object Object]`); null values kept | Yjs' `toString`, `typeMapGetAllSnapshot` |
| `Unobserve`/`UnobserveDeep` of a handler registered twice removed one registration | every registration of it, as Yjs' `removeEventHandlerListener` (`-=` on the events still removes one; an unknown handler is ignored, where Yjs logs an error) | Yjs' `unobserve` |

## Proposed, not done

These would be improvements but change many call sites or the wire behaviour, so they are left
for a discussion with the maintainers:

- `SyncProtocol` still reads and writes `Stream`s. A span/`IBufferWriter<byte>` version, or an
  `IAsyncEnumerable` over a `PipeReader`, would fit modern servers better.
- Several public types expose mutable public fields (`ID.Client`, `Delta.Insert`,
  `RelativePosition.Assoc`, `StackItem.Meta`, …). Properties would be safer; it is a breaking change.
- `RelativePosition` is a class of 80 bytes (two `ID?` fields of 24 bytes each). A `readonly struct`
  would cost nothing per position, but `Assoc` is a settable field, `CompareRelativePositions` and
  `AbsolutePosition.TryCreateFromRelativePosition` accept `null`, and callers keep positions in
  collections by reference, so it is a breaking change.
- `Item`, `AbstractStruct`, `GC`, `Skip` and the content classes are public but are
  implementation details of the CRDT; YDotNet exposes none of that.
- `YMap.Get` and `TryGet` return `object`. Typed accessors (`TryGet<T>(key, out T value)`) would
  remove casts without introducing YDotNet's `Output` wrapper.
- Nullable annotations cover the encoding layer only; the public types are not annotated yet.

## Untrusted input

Ycs is meant to apply updates from peers it does not trust (a server relaying for browsers, a
Blazor WebAssembly client). Every API that decodes bytes — `ApplyUpdate…`, `TryApplyUpdate…`,
`EncodeStateAsUpdate…(stateVector)`, everything in `UpdateOperations`, `Snapshot.DecodeSnapshot…`,
`CreateDocFromSnapshot` and `Snapshot.RestoreDocument` (a snapshot that holds more than the document),
`SnapshotContainsUpdate…`, `RelativePosition.Read`, `RelativePosition.CreateRelativePositionFromJSON`, `SyncProtocol.Read…` — throws only
`YUpdateException` for malformed input, with a message that says what was wrong (never the
input's content). The snapshot reads (`ToArraySnapshot`, `GetSnapshot`, `GetAllSnapshot`,
`ToDelta(snapshot)`) ignore deletions of clients or clocks the document does not have, as Yjs does.

**Atomicity.** `ApplyUpdate` decodes the whole update (structs and delete set) before it touches
the document, then plans the integration against the document without changing it (which structs
integrate in which order, which wait for others), and only then carries it out. A failure in
either step leaves the document exactly as it was: same text, state vector and encoding, no events
(an empty transaction may be observed when the failure is found in the planning step); the one
exception is the conflict-resolution budget below. What is checked before anything changes:

* every value is in range: client ids up to 2^53 - 1 (as lib0 reads them), and **clocks**: every struct, delete range and
  state-vector entry ends at clock 2^31 - 1 or before (about two billion operations per client id),
  so no clock arithmetic overflows;
* no struct of length 0, no client listed twice, no reference to a later struct of the item's own
  client, no unknown content or type;
* nesting: 'any' and JSON values, and shared types inside each other, at most 256 levels deep (an
  insert beyond that throws `ArgumentException` / `InvalidOperationException`), so nothing recurses
  without bound;
* size: a collection is never allocated larger than the remaining input can fill, and an update holds
  at most `UpdateLimits.MaxStructsPerUpdate` structs (131,072 by default), in either format: the v2
  format run-length encodes its columns (Yjs writes 20,000 deleted items in 42 bytes; 131,072 deleted
  items are 42 bytes too), and a v1 struct can be as small as 4 bytes. The limit is six times Yjs'
  example and can be set to anything above zero. The same limit, as their `limits` argument, bounds
  each input of the `UpdateOperations` functions; `ConvertUpdateFormatV2ToV1`, `DecodeUpdateV2` and
  `DescribeUpdateV2` expand a v2 update to the size of its structs (131,072 structs are about 0.8 MiB
  of v1, 30 MiB decoded, and a description of 11 million characters);
* pending data: what waits for structs the document does not have yet holds at most
  `UpdateLimits.MaxPendingStructs` structs (131,072) and `UpdateLimits.MaxPendingBytes` bytes (16 MiB):
  the structs as their v2 encoding counts them, the deletions as their v1 encoding does. The struct
  count bounds what a struct costs whatever its encoding; the bytes bound the content (text, values,
  keys), which both encodings carry as it is. An update that would grow either beyond its limit is
  rejected (a duplicate is merged away first). An update that lets pending structs integrate may add
  up to the limits on top of what waits, which is then read again and integrated, wholly or in part;
  so what waits, and what that re-read decodes, never exceeds twice the limits.

What the defaults allow, measured (`dotnet run -c Release --project bench/Ycs.Benchmarks -- limits`;
.NET 11, x64; the updates are valid ones made through the API, so an honest update of that many
structs costs the same):

| One update of 131,072 structs of | v1 / v2 bytes | allocated | kept by the document |
| --- | --- | --- | --- |
| characters, one item each | 0.9 MB / 131 KB | 35 MiB | 30 MiB |
| deleted items | 0.8 MB / 42 B | 38 MiB | 26 MiB |
| shared types (`YText`, `YArray`, `YMap`; `YXmlElement`) | 0.8 MB / 36 B; 1 MB / 131 KB | 88 to 105 MiB | 61 to 73 MiB |
| subdocuments | 5.8 MB / 5 MB | 184 MiB | 139 MiB |
| shared types, pending | 0.8 MB / 35 B | 60 MiB | (encoded, 35 B) |
| an update that lets those integrate and brings 131,072 more that wait (the largest re-read) | 45 B | 332 MiB | 73 MiB |
| GC structs, pending | 23 B | 8 MiB | (encoded) |

So a small update makes a document allocate at most about 340 MiB and keep about 140 MiB (the
default of 2,097,152 structs before allowed 2.3 GiB and 1.2 GiB), and
`EncodeStateAsUpdate` with 131,072 shared types pending allocates about 120 MiB per call (every
joining peer). Halving the limits halves these.

**Trusted input.** A document's own state can hold far more structs than the defaults allow (a
million characters typed one at a time and never merged are a million structs, about 7 MB of v1).
An application that applies a state it trusts (its own stored state, an update from its own
server) passes `UpdateLimits.Unlimited`, or its own instance, for that call:
`doc.ApplyUpdate(stored, origin, UpdateLimits.Unlimited)`; `YDocOptions.Limits` sets the document's
limits for every update. The limits of an update from an untrusted peer stay the document's.

**Conflict resolution.** Where several clients inserted at the same place, an item passes the
others' items between its origins (Yjs' conflict loop): n items of one client after n of another
take n^2 steps, in Yjs too, and a v2 update of 28 bytes can describe them. Ycs walks those items
without allocating (Yjs fills two sets per item: 40,000 after 40,000 took 97 s and 111 GiB, now 4.8 s
and 11 MiB) and bounds the walk by `UpdateLimits.MaxIntegrationSteps` per update (16,777,216 = 2^24
by default: about 0.1 s, 1.5 s at most where the items' origins branch). Honest updates take far
fewer steps: the Yjs corpus at most 102 per update, the unit tests 18,301, 64 peers that each typed
1,000 characters offline at the same place 33,888 per update when they meet; the largest honest
shape, one peer typing forward where another typed backwards at the same index, takes n·m steps,
and the default admits 4,096 by 4,096. This is the one limit found while the update is integrated:
**beyond it, the update is applied in part.** `ApplyUpdate` throws `YUpdateException` with
`PartlyApplied` set (`TryApplyUpdate` returns false); the structs integrated before stay (a causally
complete part of the update, as if the peer had sent less), observers and `Update` see them, and the
rest of the structs and the update's deletions are dropped. The document is valid, and converges
with any peer that has the same part; a peer that sends the rest again is refused again.

After these checks the integration does not fail on input alone; an exception from it other than
the one above would be a bug in Ycs and cannot be rolled back. What can still happen is
`OutOfMemoryException`: the limits bound one update (see the table above), not a document, which
grows with every update it accepts, and not a process that holds many documents. Structurally odd but
well-formed updates (an item whose two origins are in different parents, say) are integrated as Yjs
integrates them. Observers run after the update was applied; an exception from one propagates to the
caller of `ApplyUpdate` after all other observers ran, and the document stays usable.

## Thread safety

As in Yjs, a `YDoc` and everything in it is not thread-safe. Calls into a document, including
applying updates and reading values, must not run concurrently; a server that shares a document
between connections serializes access (one lock or one channel per document). Event handlers run
synchronously on the thread that made the change.
