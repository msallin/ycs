Ycs
-------

A compatible `.Net` implementation of the [Yjs](https://github.com/yjs/yjs) CRDT framework.

With this, you can host CRDTs in your `.Net` application and synchronize them with the existing
`Yjs` models running elsewhere.

#### Verified against Yjs [13.6.32](https://github.com/yjs/yjs) (latest stable) and 14.0.0-16 (beta)

Every shared type, both update formats, snapshots and relative positions round-trip in both
directions against the real library — see [`interop/`](interop/) and the `*ParityTests` /
`YjsInteropTests` suites. Runs on .NET 8 / .NET 10; the package still targets `netstandard2.0`.

### Shared types

`Y.Map`, `Y.Array`, `Y.Text`, `Y.XmlFragment`, `Y.XmlElement`, `Y.XmlText`, `Y.XmlHook` — the full
set, including nested types, embeds, text formatting attributes and subdocuments. Documents produced
by `y-prosemirror`, `y-quill`, Tiptap and BlockNote decode.

DOM bindings (`toDOM`) are deliberately not ported — there is no DOM to bind to. The traversal API
they exist for is: `YXmlFragment.QuerySelector` / `QuerySelectorAll` and `YXmlTreeWalker`.

### Wire formats

| | Yjs | Ycs |
|---|---|---|
| v1 (default; what `y-protocols` and every provider speaks) | `Y.encodeStateAsUpdate` / `Y.applyUpdate` | `EncodeStateAsUpdateV1` / `ApplyUpdateV1` |
| v2 (denser) | `Y.encodeStateAsUpdateV2` / `Y.applyUpdateV2` | `EncodeStateAsUpdateV2` / `ApplyUpdateV2` |

`SyncProtocol` speaks v1 by default, matching `y-protocols/sync`; pass `v2: true` on both ends for
the v2 encoding. `YDoc` raises `UpdateV1` and `UpdateV2` for the two formats.

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

Changes against upstream `main`
-------

Upstream was last tested against Yjs 13.4.14 and had never been able to talk to a real JS provider.

**Compatibility**
* v1 update encoder/decoder — upstream implemented only v2, which no JS provider speaks.
* v2 `writeJSON`/`readJSON` now uses the `any` encoding, as Yjs does. The previous JSON-string
  encoding made every formatted text run and every embed unreadable by Yjs.
* `ReadJson` yields plain CLR values (`Dictionary<string, object>`, `List<object>`, `long`/`double`,
  `string`, `bool`, `null`), and JSON in the v1 format is written by `JSON.stringify`'s rules
  (`Utils/YJson.cs`: `1` not `1.0`, ISO-looking strings stay strings) on System.Text.Json — no
  Newtonsoft.Json dependency.
* The XML shared types, the `updates.js` operations, `PermanentUserData`, and the missing parts of
  the `Snapshot` / `RelativePosition` / `YMap` / `YArray` / `YText` / `YEvent` / `UndoManager` APIs.
* `RelativePosition` and `AbsolutePosition` are public — they were `internal`, which put shared
  cursors out of reach entirely.

**Bugs found on the way** (all pre-existing, all with a regression test)
* `GenerateNewClientId` used a clock-seeded `Random`: two documents created in the same tick — or two
  server processes started together — could share a client id and silently corrupt the merge.
* `SortAndMergeDeleteSet` coalesced only exactly-adjacent delete ranges, never overlapping ones.
* `DecodeSnapshot(Stream)` decoded with the **v2** decoder despite its name.
* The snapshot helpers did not split snapshot-affected structs, leaking post-snapshot content.
* `YTextEvent.Delta`'s trailing-retain trim was inverted: it dropped retains that assign attributes
  and kept the bare ones Yjs discards. `YTextTests.TestBasicFormat` had encoded the wrong behaviour;
  its expectations are now the output of yjs 13.6.32, quoted in the test.
* `YEvent`'s per-key loop used `break` where it needed `continue`, dropping changed keys.
* `YEvent.GetPathTo` counted preceding *items* rather than their content length.
* Flushing prelim list content during `Integrate` double-counted `Length` (`YArray`, and the same
  shape in `YXmlFragment`).
* `YMap.Clone` stored the raw `Item` instead of the value and never recursed into nested types.
* `ContentFormat.GetContent` threw instead of returning an empty list.

**Build**
* Retargeted to `netstandard2.0;net8.0;net10.0`, MSTest 3; no third-party runtime dependency (System.Text.Json is a package only on netstandard2.0).
* The 3.1-era `samples/YcsSample` is no longer part of the solution (stale SPA toolchain).

Running the checks
-------

```bash
dotnet test tests/Ycs.Tests/Ycs.Tests.csproj   # 296 tests, no Node needed
./interop/run.sh                                # live round-trips against yjs 13.6.32 + 14 beta
```

The test suite pins base64 updates produced by the real library, so format drift fails the build
without Node installed. `interop/run.sh` goes further and runs both implementations against each
other; see [`interop/README.md`](interop/README.md).

Demo
-------

Client: [`Yjs`](https://github.com/yjs/yjs), [`MonacoEditor`](https://github.com/microsoft/monaco-editor), [`SignalR`](https://github.com/dotnet/aspnetcore/tree/master/src/SignalR).

Server: `Ycs`, `SignalR`, [`AspNetCore`](https://github.com/dotnet/aspnetcore).

![img](https://github.com/yjs/ycs/blob/main/docs/ycs.gif)
