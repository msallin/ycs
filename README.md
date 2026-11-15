Ycs
-------

A `.NET` implementation of the [Yjs](https://github.com/yjs/yjs) CRDT framework: host Yjs documents
in a .NET application and synchronize them with Yjs running anywhere else.

This is a modernized fork of [yjs/ycs](https://github.com/yjs/ycs) (dormant since 2023). It builds on
three open upstream pull requests — #12 (v1 update format), #13 (parity with Yjs 13.6: XML types,
`updates.js`, snapshots, relative positions) and #14 (an RLE encoder fix) — and then:

* targets **.NET 11 only**, with nullable reference types (partly annotated), implicit usings and
  file-scoped namespaces;
* has **no reflection, no `dynamic` and no third-party dependency**, is marked
  `IsAotCompatible`/`IsTrimmable` with zero trim/AOT warnings, and ships a Native AOT sample
  (`samples/Ycs.AotSample`, about 2 MB) that CI publishes and runs;
* encodes and decodes with byte buffers and spans instead of `MemoryStream`s
  (`ApplyUpdate(ReadOnlySpan<byte>)`, `EncodeStateAsUpdate(IBufferWriter<byte>)`), and uses v1 as the
  default format, as Yjs does — see [`docs/api.md`](docs/api.md) for every API change and how the API
  compares to Yjs and [YDotNet](https://github.com/y-crdt/ydotnet);
* fixes divergences from Yjs found by differential testing (below).

#### Why only .NET 11

The previous builds targeted `netstandard2.0`, which has no `Span<T>`-based `Encoding`,
`BinaryPrimitives` overloads, `ArgumentNullException.ThrowIfNull`, `Convert.ToHexString`,
`RandomNumberGenerator.GetInt32` or trimming/AOT annotations, and needed `#if` branches and a
System.Text.Json package to make up for it. Dropping it removes those branches and lets the
encoding layer use spans throughout. If you need .NET Framework, Unity or older runtimes, use
upstream ycs.

#### Verified against Yjs 13.6.32 and 13.6.33 (and 14.0.0-16 beta)

* `tests/Ycs.Tests` (364 tests) pins updates written by real Yjs as base64 and checks byte-identical
  re-encoding, and `TextDifferentialTests` replays 40 seeded random concurrent editing sessions on
  2–4 replicas that were run in Yjs 13.6.33 (`interop/differential/gen.mjs`), asserting the same final
  text and byte-identical v1 re-encoding.
* `tests/Ycs.Corpus` (486 tests) replays a recorded corpus of Yjs 13.6.32 sessions
  ([`interop/corpus/`](interop/corpus/README.md)).
* `interop/run.sh` and `interop/corpus/roundtrip.sh` run Ycs and Yjs against each other live
  (Node needed).

#### Untrusted input

Updates from peers can be malformed or hostile: applying, merging or diffing them throws only
`YUpdateException`, leaves the document unchanged, and is bounded in time, memory and stack depth
(clocks up to 2^31 - 1, nesting up to 256 levels, structs per v2 update up to
`YDocOptions.MaxStructsPerUpdate`, pending data up to `YDocOptions.MaxPendingBytes`).
`TryApplyUpdate` returns false instead. See [`docs/api.md`](docs/api.md#untrusted-input).

#### Thread safety

As in Yjs, a `YDoc` is not thread-safe: the caller must make sure that no two threads use the same
document (or its shared types) at the same time, e.g. with one lock or one channel per document.
Observers run synchronously on the thread that made the change.

### Shared types

`Y.Map`, `Y.Array`, `Y.Text`, `Y.XmlFragment`, `Y.XmlElement`, `Y.XmlText`, `Y.XmlHook` — the full
set, including nested types, embeds, text formatting attributes and subdocuments. Documents produced
by `y-prosemirror`, `y-quill`, Tiptap and BlockNote decode.

DOM bindings (`toDOM`) are deliberately not ported — there is no DOM to bind to. The traversal API
they exist for is: `YXmlFragment.QuerySelector` / `QuerySelectorAll` and `YXmlTreeWalker`.

### Wire formats

| | Yjs | Ycs |
|---|---|---|
| v1 (default; what `y-protocols` and every provider speaks) | `Y.encodeStateAsUpdate` / `Y.applyUpdate` | `EncodeStateAsUpdate` / `ApplyUpdate` (or `…V1`) |
| v2 (denser) | `Y.encodeStateAsUpdateV2` / `Y.applyUpdateV2` | `EncodeStateAsUpdateV2` / `ApplyUpdateV2` |

`ApplyUpdate`, `EncodeStateAsUpdate` and `EncodeStateVector` without a suffix use v1, as in Yjs.
`SyncProtocol` speaks v1 by default, matching `y-protocols/sync`; pass `v2: true` on both ends for
the v2 encoding. `YDoc` raises `Update` (v1) and `UpdateV2` for the two formats.

### Update manipulation (`Ycs.UpdateOperations`)

`MergeUpdates`, `DiffUpdate`, `EncodeStateVectorFromUpdate`, `ParseUpdateMeta`, `DecodeUpdate`,
`ConvertUpdateFormatV1ToV2` / `V2ToV1`, `DescribeUpdate` — each with a `…V2` sibling. These are what
persistence providers are built on: `MergeUpdates` compacts a log without instantiating a document,
`DiffUpdate` sends a peer only what it is missing.

`ObfuscateUpdate` is **not** implemented (it needs mutable access to every content type, for the
least useful entry in that list).

### Versioning and presence

`Snapshot` (`EncodeSnapshot` / `DecodeSnapshot` / `EqualSnapshots` / `SnapshotContainsUpdate` /
`YDoc.CreateDocFromSnapshot`), `RelativePosition` and `AbsolutePosition` (binary and JSON forms, both
`assoc` directions) and `PermanentUserData` are all present and byte-compatible. Note Yjs' rule:
snapshots are only meaningful on a document created with `Gc = false`.

### Not supported

* `ObfuscateUpdate`, and the DOM bindings (`toDOM`) of the XML types.
* `undefined` is read as `null` (and written back as `null`); JavaScript objects with integer-like
  keys are written in insertion order, not in JavaScript's key order.
* A document's own client id is a non-negative `int` (31 bits); ids up to 2^32 from other clients
  are read fine.
* Integers beyond ±(2^53 - 1) are rejected (no exact JavaScript number); use `BigInteger`.

Changes in this fork
-------

The commit history is meant to be read: each commit is one step, and each bug fix says what Yjs
does. In short:

**Divergences from Yjs fixed here** (each with a test)
* Search markers were never created (`FindMarker` bailed out when none existed), so every index
  lookup in a text or array walked from the start; enabling them needed four alignments with Yjs
  (no markers for XML fragments, markers disabled once a text has formatting, markers moved on
  merge, markers only where Yjs' `findPosition` uses them).
* A `Y.Text` holding an embedded shared type was indexed wrongly.
* State vectors and delete sets were written in dictionary order instead of highest client first.
* While one struct waited for a missing dependency, nothing else integrated; and pending structs
  kept a stale root placeholder, losing content when the root was realized in between. Pending
  structs are now kept as an encoded update, as in Yjs 13.6.
* The 'any' encoding now follows lib0's number rules (an integral `double` is a var-int, exact
  floats are float32, JavaScript's canonical NaN), so values written by Ycs have Yjs' bytes.
* The shared types and their events, checked against 1,425 traces recorded from Yjs 13.6.32: a
  text event's delta repeated earlier inserts and kept the 13.4 formatting logic, `Push` was an
  insert at the end (different bytes), a shared type could not be a text embed, `ToDelta` opened a
  transaction and misreported `computeYChange`, the XML string forms used .NET formatting, and
  `Get`, `Insert`, `Slice` and `Delete` threw where Yjs does not, and the reverse.

**Performance** (the automerge-paper editing trace, 260k edits, `bench/Ycs.Benchmarks`,
median of 5 runs on the same machine, before = upstream PRs merged, after = this branch)

| | before | after |
| --- | --- | --- |
| replay the trace locally, one transaction per edit | 11 786 ms | 658 ms |
| apply the 260k v1 updates to a fresh document | 398 ms, 860 MiB | 371 ms, 921 MiB |
| encode the final document, v1, 10× | 14.2 ms, 26.2 MiB | 7.6 ms, 14.0 MiB |
| encode the final document, v2, 10× | 12.0 ms, 31.3 MiB | 9.3 ms, 15.2 MiB |
| decode the final document, v1, 10× | 217 ms, 87.7 MiB | 65 ms, 36.6 MiB |
| decode the final document, v2, 10× | 258 ms, 89.6 MiB | 68 ms, 38.6 MiB |

Run it with `bench/Ycs.Benchmarks/get-trace.sh` and
`dotnet run -c Release --project bench/Ycs.Benchmarks -- <trace.json.gz>`.

**From the upstream pull requests** (#12, #13 by Yuriy Habarov; #14 by Kieren Johnstone): the v1
format, the XML types, `UpdateOperations`, `PermanentUserData`, snapshots and relative positions,
a CSPRNG client id, delete-set coalescing, the v2 JSON encoding, the `IntDiffOptRleEncoder` sign fix
and many smaller fixes listed in those pull requests.

Running the checks
-------

```bash
dotnet test tests/Ycs.Tests      # unit, interop fixtures and differential tests; no Node needed
dotnet test tests/Ycs.Corpus     # the recorded Yjs corpus; no Node needed
dotnet test tests/Ycs.Fuzz -c Release     # damaged updates must fail cleanly
dotnet test tests/Ycs.Budgets -c Release  # allocation and time budgets per operation
dotnet publish samples/Ycs.AotSample -c Release -o out/aot && ./out/aot/Ycs.AotSample
dotnet publish samples/Ycs.WasmSample -c Release -o out/wasm && node out/wasm/wwwroot/main.mjs
./interop/run.sh                 # live round-trips against yjs 13.6.32 and 14 beta (Node)
./interop/corpus/roundtrip.sh    # Ycs encodings of the corpus read back by Yjs (Node)
(cd interop/differential && npm ci && node gen.mjs > ../../tests/Ycs.Tests/Fixtures/text-differential.json)
```

[`docs/testing.md`](docs/testing.md) says what each of these guards, how to run a fuzz soak, how
to accept a public API change and how to update the budgets.

Demo
-------

`samples/YcsSample` is a SignalR server with a React/Monaco client. The server targets .NET 11 and
starts; the client (`ClientApp`, 2021 dependencies built with yarn) was not updated or tested.

Client: [`Yjs`](https://github.com/yjs/yjs), [`MonacoEditor`](https://github.com/microsoft/monaco-editor), [`SignalR`](https://github.com/dotnet/aspnetcore/tree/master/src/SignalR).

Server: `Ycs`, `SignalR`, [`AspNetCore`](https://github.com/dotnet/aspnetcore).

![img](https://github.com/yjs/ycs/blob/main/docs/ycs.gif)
