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
| `EncodeStateAsUpdateV2(byte[] stateVector = null)` | `EncodeStateAsUpdate(ReadOnlySpan<byte> stateVector = default)` (v1), `EncodeStateAsUpdateV1`/`V2(...)`, and overloads `EncodeStateAsUpdate…(IBufferWriter<byte> destination, ReadOnlySpan<byte> stateVector = default)` | Yjs' `encodeStateAsUpdate`; writing into an `IBufferWriter<byte>` (a pipe, a pooled buffer) avoids the final copy. A `byte[]` or `null` still works |
| `EncodeStateVectorV2()` | `EncodeStateVector()` (plus `…V1`, `…V2`) | Yjs' `encodeStateVector`; the bytes are the same in both formats |
| event `UpdateV2`; PR #12 added `UpdateV1` | event `Update` (v1) and `UpdateV2` | Yjs' `doc.on('update')` and `doc.on('updateV2')` |
| `ClientId { get; internal set; }`, random only | `YDocOptions.ClientId` (`int?`) | reproducible tests and benchmarks without reflection (YDotNet has `DocOptions.Id`) |
| `void Observe(handler)`, `void ObserveDeep(handler)` (PR #13) | return `IDisposable`; disposing unsubscribes | the YDotNet idiom; makes lambdas easy to unsubscribe. `Unobserve` and `+=`/`-=` still work |
| map/array values: anything, failing at encode time; `Newtonsoft.Json` tokens and reflection for JSON | the closed set of values above, checked when a value is inserted; numbers encoded with JavaScript's rules | Native AOT, byte-identical output, errors at the call that caused them |
| `UndoManager` tracked origin `Type` matched subclasses | matches that exact type | Yjs compares `origin.constructor` |
| `ID.Write(Stream)`, `RelativePosition.Write(Stream)`/`Read(Stream)`, `Snapshot.DecodeSnapshot(Stream)` | internal; use `RelativePosition.ToArray()`/`Read(byte[])`, `Snapshot.DecodeSnapshot(byte[])` | these exposed the internal encoding layer |

## Proposed, not done

These would be improvements but change many call sites or the wire behaviour, so they are left
for a discussion with the maintainers:

- `SyncProtocol` still reads and writes `Stream`s. A span/`IBufferWriter<byte>` version, or an
  `IAsyncEnumerable` over a `PipeReader`, would fit modern servers better.
- Several public types expose mutable public fields (`ID.Client`, `Delta.Insert`,
  `RelativePosition.Assoc`, `StackItem.Meta`, …). Properties would be safer; it is a breaking change.
- `YDoc.ClientId` is an `int`, so a document cannot use the upper half of Yjs' 32-bit client id
  range itself (it reads such ids from other clients fine). `uint` would match Yjs.
- `Item`, `AbstractStruct`, `GC`, `Skip` and the content classes are public but are
  implementation details of the CRDT; YDotNet exposes none of that.
- `YMap.Get` returns `object`. Typed accessors (`TryGet<T>(key, out T value)`) would remove casts
  without introducing YDotNet's `Output` wrapper.
- Nullable annotations cover the encoding layer only; the public types are not annotated yet.

## Thread safety

As in Yjs, a `YDoc` and everything in it is not thread-safe. Calls into a document, including
applying updates and reading values, must not run concurrently; a server that shares a document
between connections serializes access (one lock or one channel per document). Event handlers run
synchronously on the thread that made the change.
