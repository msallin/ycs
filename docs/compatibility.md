# Compatibility

Ycs aims at Yjs 13.6: the same concepts and names, the same bytes on the wire, the same result after any sequence
of edits. Where it differs, this page says so.

## Tested Yjs versions

| Version | How it is checked |
| --- | --- |
| Yjs 13.6.32 | updates written by Yjs pinned as fixtures and re-encoded byte for byte; a recorded corpus of Yjs sessions replayed; 1,425 traces of the shared types and their events; live round trips (`interop/run.sh`) |
| Yjs 13.6.33 | 40 seeded random editing sessions of 2 to 4 replicas run in Yjs and replayed in Ycs: the same text, byte-identical updates; the sync server against y-websocket clients |
| Yjs 14.0.0-16 (beta) | live round trips |
| `@y/y` 14.0.0-rc.30 | documents written by v14 (`Y.Node`, client ids up to 2^53 - 1) read and re-encoded byte for byte; v14 converges with Ycs' answers |

Yjs 14 is wire-compatible with 13: Ycs reads and writes what v14 does. No v14 API is ported.

Editor documents written by `y-prosemirror`, `y-quill`, Tiptap and BlockNote decode.

## Wire formats

| | Yjs | Ycs |
| --- | --- | --- |
| v1 (the default; what y-protocols and every provider speaks) | `Y.encodeStateAsUpdate` / `Y.applyUpdate` | `EncodeStateAsUpdate` / `ApplyUpdate` |
| v2 (denser) | `Y.encodeStateAsUpdateV2` / `Y.applyUpdateV2` | `EncodeStateAsUpdate(format: UpdateFormat.V2)` / `ApplyUpdate(format: UpdateFormat.V2)` |
| state vector | `Y.encodeStateVector` | `EncodeStateVector` |
| update events | `doc.on('update')`, `doc.on('updateV2')` | `YDoc.Updated`, `YDoc.UpdatedV2` |
| sync messages | `y-protocols/sync` | `SyncProtocol` (v1; `UpdateFormat.V2` on both ends for v2) |
| awareness | `y-protocols/awareness` | not in the library; a port in `samples/Ycs.Server/Awareness` |

## What is there

| Area | Ycs |
| --- | --- |
| Shared types | `YText` (formatting, embeds, embedded shared types), `YMap`, `YArray`, `YXmlFragment`, `YXmlElement`, `YXmlText`, `YXmlHook`, nested types, subdocuments |
| XML traversal | `YXmlFragment.QuerySelector`, `QuerySelectorAll`, `CreateTreeWalker` |
| Updates without a document (`YUpdate`) | `Merge`, `Diff`, `EncodeStateVector`, `ParseMeta`, `Decode`, `Convert`, `Describe`, each with a `format` argument |
| Versions and positions | `Snapshot` (`Encode`, `Decode`, `Equals`, `ContainsUpdate`, `YDoc.CreateDocFromSnapshot`), `RelativePosition` and `AbsolutePosition` (binary and JSON forms), `PermanentUserData`; all byte-compatible |
| Undo | `UndoManager` with Yjs 13.6's behaviour |

Snapshots are only meaningful on a document created with `GarbageCollection = false` (`new YDoc(new YDocOptions { GarbageCollection = false })`),
as in Yjs.

## Not supported

- `ObfuscateUpdate`.
- The DOM bindings of the XML types (`toDOM`): there is no DOM. The traversal API above is there.
- JavaScript's `undefined` is read as `null`, and written back as `null`.
- JavaScript objects with integer-like keys are written in insertion order, not in JavaScript's key order.
- Integers beyond ±(2^53 - 1) have no exact JavaScript number and are rejected; use `BigInteger` (a JavaScript
  `bigint`).
- Clocks beyond 2^31 - 1 per client (about two billion operations by one client id) are rejected; Yjs reads up to
  2^53 - 1.

## Where Ycs differs from Yjs on purpose

- **Limits on untrusted input.** Yjs applies whatever it is given; Ycs bounds what one update may cost and rejects
  the rest ([Untrusted input and threads](untrusted-input.md)).
- **Throwing handlers.** In Yjs, an exception in a handler such as `update`, `afterTransactionCleanup` or
  `beforeTransaction` leaves the document broken for good. Ycs finishes the cleanup and then rethrows, so the document
  stays usable ([API reference](api.md#exceptions-of-handlers)).
- **The API is .NET-shaped where it helps:** events, `IDisposable` subscriptions, spans and `IBufferWriter<byte>`
  for encoding. [The API reference](api.md) describes the shape.

## Performance

The automerge-paper editing trace (260k edits), `bench/Ycs.Benchmarks`, median of 5 runs. The times are those of 2026-11-15
(measured before the slab and the arenas; not repeated since, a clean timing round is still to come); the allocations are
those of 2026-11-17 (the current layout, 3 runs; `bench/Ycs.Compare/results/2026-11-17-8-12/benchmarks-trace.txt`):

| Operation | Time | Allocated |
| --- | --- | --- |
| replay the trace locally, one transaction per edit | 368 ms | 53 MiB |
| apply the 260k v1 updates to a fresh document | 254 ms | 12 MiB |
| encode the final document, v1, 10 times | 5.3 ms | 2.8 MiB |
| decode the final document, v1, 10 times | 58 ms | 38 MiB |

Run it with `bench/Ycs.Benchmarks/get-trace.sh` and
`dotnet run -c Release --project bench/Ycs.Benchmarks -- <trace.json.gz>`.

## Ycs or YDotNet

[YDotNet](https://github.com/y-crdt/ydotnet) is the .NET binding of yrs, the Rust port of Yjs. Both speak the same
wire format. Measured on the same workloads ([`bench/Ycs.Compare`](https://github.com/msallin/ycs/blob/modernize/bench/Ycs.Compare/README.md),
2026-11-15):

| | Ycs | YDotNet 0.7.0 |
| --- | --- | --- |
| Typing, keystroke by keystroke | 10 to 100 times faster (100,000 keystrokes at the end: 25 ms against 3.1 s) | |
| Loading and encoding whole documents | | 1.8 to 7 times faster |
| Native library | none | `libyrs`, through P/Invoke |
| Native AOT | 2.35 MB, no warning | 4.29 MB, 3 trim warnings |
| Blazor WebAssembly | runs | no browser-wasm build of yrs |
| Malformed or over-limit updates (16 cases) | all rejected, document unchanged | 3 malformed ones applied; one aborts the process |
| Transactions | implicit, as in Yjs | an explicit transaction object for every call |

Ycs has slow paths of its own: very many inserts or pushes within one transaction, and many deletes at random places
of a long array, grow quadratically. The comparison lists them.
