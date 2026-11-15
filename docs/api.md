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
| Undo | `UndoManager(scope, { trackedOrigins, captureTimeout })` | `UndoManager(doc, branch, options)` | `UndoManager(scope, captureTimeout, deleteFilter, trackedOrigins)` |
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
| `ClientId { get; internal set; }`, random only | `YDocOptions.ClientId` (`int?`) | reproducible tests and benchmarks without reflection (YDotNet has `DocOptions.Id`) |
| `void Observe(handler)`, `void ObserveDeep(handler)` (PR #13) | return `IDisposable`; disposing unsubscribes | the YDotNet idiom; makes lambdas easy to unsubscribe. `Unobserve` and `+=`/`-=` still work |
| map/array values: anything, failing at encode time; `Newtonsoft.Json` tokens and reflection for JSON | the closed set of values above, checked when a value is inserted; numbers encoded with JavaScript's rules | Native AOT, byte-identical output, errors at the call that caused them |
| `UndoManager` tracked origin `Type` matched subclasses | matches that exact type | Yjs compares `origin.constructor` |
| `ApplyUpdate…(update, origin, bool local = false)` | `ApplyUpdate…(update, origin)`; the transaction an update is applied in is not local, also the caller's | Yjs' `readUpdateV2` forces `transaction.local = false`; a local transaction kept stale search markers (an insert after a nested apply landed 5 characters early) |
| malformed updates threw `EndOfStreamException`, `InvalidDataException`, `JsonReaderException`, `InvalidCastException`, `NullReferenceException`, `DivideByZeroException`, bare `Exception`, …, some after half the update was applied | `YUpdateException` (a `FormatException`), and the document is unchanged; `TryApplyUpdate` / `TryApplyUpdateV2` return false instead | see [Untrusted input](#untrusted-input) |
| `YDocOptions.Meta`, `YDoc.Meta`: `IDictionary<string, string>` (always null when read from an update) | `object` (any value of the 'any' encoding) | Yjs' `meta` is any value; the old cast always failed, and an update with a `meta` that was not an object was rejected |
| a subdocument's options written as `{ gc, guid, autoLoad }` with a random guid of their own; `ShouldLoad` defaulted to `AutoLoad`; `Destroy` threw with subdocuments and left a deleted subdocument's content without a document; a subdocument kept its own client id | as Yjs 13.6: the options are `{}` plus `gc: false`, `autoLoad: true`, `meta` when they differ from the defaults; reading lets every key override (a `guid` too, as `createDocFromOpts`); `YDocOptions.ShouldLoad` (default true) and `YDoc.ShouldLoad`; a destroyed subdocument is replaced by a fresh document that does not load; an added subdocument takes the parent's client id and `CollectionId` (new, Yjs' `collectionid`) | a Yjs peer reading Ycs' options took the random guid as the subdocument's identity |
| pending structs and deletions: unbounded | `YDocOptions.MaxPendingBytes` (16 MiB) | a peer could send updates that never integrate and fill memory |
| pending structs and deletions: not visible, and left out of `EncodeStateAsUpdate` | `YDoc.HasPending`; `EncodeStateAsUpdate…` includes them, as Yjs 13.6 does (the pending deletions whole, the pending structs beyond the target state vector, merged into the state) | Yjs exposes `doc.store.pendingStructs`/`pendingDs`; a server that persisted its state and restarted lost what was pending |
| — (Yjs has none either) | `YText.RemoveNonText()`: deletes every embed, nested type, other non-string content and formatting in one transaction; returns whether it changed anything | a plain-text editor (a textarea) bound to a `YText` that other peers may fill with rich content: afterwards `Length` is the string's length and indexes agree. It deletes the items themselves, so nothing is added to the document |
| every changed type built its event (`YTextEvent`, `YMapEvent`, …) and added itself and its ancestors to `Transaction.ChangedParentTypes`, observed or not | only when the type has an `EventHandler`, it or an ancestor a `DeepEventHandler`, or the document an `AfterTransaction`, `AfterTransactionCleanup` or `AfterAllTransactions` handler (an `UndoManager` is one); `Transaction.Changed` is filled as before. `YTextEvent.KeysChanged` and `YXmlEvent.AttributesChanged` are built when read (also after the transaction ended: they hold the event's own set of changed keys, not the transaction) | an unobserved document (a server that applies and relays updates) builds no events; Yjs always does. A handler of `Update` or of another type's events that reads `ChangedParentTypes` sees only the observed types |
| `Transaction.Changed`: a new `HashSet<string>` per changed type; `YMap.Count` walked the map (O(n)) | the types whose children changed and none of their keys share one read-only set (`{ null }`), so treat the sets as read-only; `YMap.Count` is kept as entries are added and deleted (O(1)) | a keystroke changed one list type and allocated a set for it; counting a map is common in bindings |
| `YArrayBase.FindMarker`, `YArrayBase._searchMarkers` and their nested marker types were `protected` | `private protected` | implementation details of the search markers; a subclass outside the assembly cannot use them meaningfully |
| `YText.ToString()` built the string each time, through a `StringBuilder` (twice the text's size) | kept until the text changes (an unchanged text returns the same string; the text holds on to it meanwhile), built in one copy; new `YText.CopyTo(Span<char>)` (returns the characters written; `Length` is always enough room) and `YText.WriteTo(IBufferWriter<char>)` write the text without a string | an editor binding or a server that reads the text after every update |
| the formatting cleanup of an earlier Yjs: run in `YText`'s observer call, before the observers, in a nested transaction; `CleanupFormatting` deleted every format whose value differed from the attributes at the next content; format keys compared with `Equals` (`1` and `1.0` differed, two equal maps did not) | Yjs 13.6's: once per transaction that changed a formatted text remotely, after its `AfterTransaction` handlers, in a transaction of its own (`cleanupYTextAfterTransaction`, `cleanupFormattingGap`, `cleanupContextlessFormattingGap`); values compared as Yjs' `equalAttrs` (JavaScript `===`, maps and lists flat). Also as Yjs: `Format` beyond the end appends one newline per missing character (one fewer before), and an `ApplyDelta` insert after a formatted one goes after its negating format | with concurrent overlapping bold, Ycs deleted three formats and ended `[abcdef bold]` where Yjs deletes none and ends `[ab bold][cd bold][ef]`; the documents of a Ycs and a Yjs peer diverged |
| `ID.Write(Stream)`, `RelativePosition.Write(Stream)`/`Read(Stream)`, `Snapshot.DecodeSnapshot(Stream)` | internal; use `RelativePosition.ToArray()`/`Read(byte[])`, `Snapshot.DecodeSnapshot(byte[])` | these exposed the internal encoding layer |
| a new `Transaction` for every transaction, readable forever, and so were an event's lazily computed `Changes`, `Keys`, `Delta`, `Adds`, `Deletes` | the document reuses its transaction objects: a `Transaction` (and its `BeforeState`, `AfterState`, `Changed`, `ChangedParentTypes`, `Meta`, the list `AfterAllTransactions` passes) is valid until the batch's `AfterAllTransactions` handlers return; reading an ended one, or computing an event's changes after its transaction ended, throws `InvalidOperationException`. Every event handed to a handler (a type's, a deep one's, an `UndoManager` stack event's) is in its transaction's `ChangedParentTypes`, and the transaction lets go of them all when it ends, so a kept event never reads the transaction that reuses the object. Compute what you need in the handler and keep that | a transaction allocated about 2 KB of bookkeeping per keystroke; Yjs also throws when an event's changes are computed after its handler. `UndoManager` stack items and its events keep their own data |
| `Transact(Action<Transaction>, origin, local)` only | also `Transact<TState>(TState state, Action<TState, Transaction> action, object origin = null, bool local = true)` | with a `static` lambda nothing is captured, so no closure is allocated per call |
| `AfterTransaction` before the deep observers; an observer that threw skipped the observers of the other changed types; two transactions an observer started one after the other were merged into one; `SubdocsChanged` after every transaction | as Yjs 13.6: observers, deep observers, then `AfterTransaction`, each called even if one before threw (the first exception is rethrown after the cleanup); a transaction started in an observer ends when its `Transact` returns; `SubdocsChanged` only when a subdocument was added, removed or loaded | Yjs' `cleanupTransactions` and `transact` |

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
- `YDoc.ClientId` is an `int`, so a document cannot use the upper half of Yjs' 32-bit client id
  range itself (it reads such ids from other clients fine). `uint` would match Yjs.
- `Item`, `AbstractStruct`, `GC`, `Skip` and the content classes are public but are
  implementation details of the CRDT; YDotNet exposes none of that.
- `YMap.Get` returns `object`. Typed accessors (`TryGet<T>(key, out T value)`) would remove casts
  without introducing YDotNet's `Output` wrapper.
- Nullable annotations cover the encoding layer only; the public types are not annotated yet.

## Untrusted input

Ycs is meant to apply updates from peers it does not trust (a server relaying for browsers, a
Blazor WebAssembly client). Every API that decodes bytes — `ApplyUpdate…`, `TryApplyUpdate…`,
`EncodeStateAsUpdate…(stateVector)`, everything in `UpdateOperations`, `Snapshot.DecodeSnapshot…`,
`SnapshotContainsUpdate…`, `RelativePosition.Read`, `SyncProtocol.Read…` — throws only
`YUpdateException` for malformed input, with a message that says what was wrong (never the
input's content).

**Atomicity.** `ApplyUpdate` decodes the whole update (structs and delete set) before it touches
the document, then plans the integration against the document without changing it (which structs
integrate in which order, which wait for others), and only then carries it out. A failure in
either step leaves the document exactly as it was: same text, state vector and encoding, no events
(an empty transaction may be observed when the failure is found in the planning step). What is
checked before anything changes:

* every value is in range: client ids up to 2^32 - 1, and **clocks**: every struct, delete range and
  state-vector entry ends at clock 2^31 - 1 or before (about two billion operations per client id),
  so no clock arithmetic overflows;
* no struct of length 0, no client listed twice, no reference to a later struct of the item's own
  client, no unknown content or type;
* nesting: 'any' and JSON values, and shared types inside each other, at most 256 levels deep (an
  insert beyond that throws `ArgumentException` / `InvalidOperationException`), so nothing recurses
  without bound;
* size: a collection is never allocated larger than the remaining input can fill, and a v2 update
  (whose run-length encoded columns can describe any number of structs in a few bytes) holds at most
  64 structs per byte plus 4096;
* pending data: what waits for structs the document does not have yet may grow to
  `YDocOptions.MaxPendingBytes` (16 MiB by default), counted as encoded bytes; an update that would
  grow it further is rejected (a duplicate is merged away first).

After these checks the integration does not fail on input alone; an exception from it would be a
bug in Ycs and cannot be rolled back. Structurally odd but well-formed updates (an item whose two
origins are in different parents, say) are integrated as Yjs integrates them. Observers run after
the update was applied; an exception from one propagates to the caller of `ApplyUpdate` after all
other observers ran, and the document stays usable.

## Thread safety

As in Yjs, a `YDoc` and everything in it is not thread-safe. Calls into a document, including
applying updates and reading values, must not run concurrently; a server that shares a document
between connections serializes access (one lock or one channel per document). Event handlers run
synchronously on the thread that made the change.
