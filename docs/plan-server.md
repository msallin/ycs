# Plan: a Yjs sync server on Ycs, as a test bed

A server that speaks the Yjs network protocol (y-protocols: sync and awareness) with one Ycs document per room, so
that unmodified Yjs clients drive Ycs as a long-lived, concurrently edited document. It is a test bed first: it
exercises what the interop harness (`interop/`, `docs/testing.md`) does not, and gives a like-for-like benchmark
against the reference server in Node. It replaces `samples/YcsSample` and keeps SignalR as a second transport.

## What it adds to the existing tests

`interop/` already checks the update formats, state-vector diffs, out-of-order delivery and ~173,000 differential
steps against Yjs; `bench/Ycs.Compare/Hostile.cs` and the fuzzers cover hostile updates. The server adds:

* **Real clients:** `y-websocket`'s provider and editor bindings, not replayed scripts.
* **One long-lived document:** hours of concurrent updates; memory, garbage collection, pending structs that never
  resolve.
* **Diffs on demand under load:** every (re)connect asks for a diff against the client's state vector while
  updates keep arriving.
* **A system-level oracle:** scripted clients (edit, drop, edit offline, reconnect) must converge with each other and
  with the server, and with the same scripts run against the Node server.
* **A benchmark:** throughput, fan-out latency and memory per room against the Node server, measured the same way.

## The wire

The y-websocket wire, unchanged: one binary WebSocket message per y-protocols message, lib0 encoding, the first
varuint the type: `0` sync (sub-types `0` step 1, `1` step 2, `2` update), `1` awareness, `3` query awareness. The
room is the URL path. On connect the server sends step 1 and the full awareness state; it answers a step 1 with step
2; it applies step 2 and updates to the room's document and passes each resulting update to the room's other
connections. On close it removes the awareness states that connection set and tells the others.

Ycs has the sync half (`src/Ycs/Protocols/SyncProtocol.cs`, the same bytes as y-protocols); awareness is not ported.

SignalR carries the same messages: one hub method each way taking `byte[]` (base64 in SignalR's JSON protocol, no
MessagePack package), one group per room. Hub invocations of one connection run one at a time
(`MaximumParallelInvocationsPerClient` = 1), so the sample's clocks and reordering go. The JS side is a small
provider over `@microsoft/signalr` that feeds the same y-protocols encoders the WebSocket provider uses.

## Layout

```
samples/Ycs.Server/            ASP.NET app: the rooms, both transports, a static demo page
  Rooms/                       Room (one YDoc, one lock), RoomRegistry, Session (bytes in, bytes out, no transport)
  Awareness/                   the y-protocols/awareness port (encode, decode, apply, remove on close)
  Transports/                  WebSocketEndpoint (`/ws/{room}`), SyncHub (`/hub`)
  wwwroot/                     demo page: CodeMirror 6 + y-codemirror.next, transport chosen in the URL
tests/Ycs.Server.Tests/        unit tests of Session and Awareness (in-memory peers, no sockets)
interop/server/                Node driver: scripted clients against any server, the reference server, run.sh
bench/Ycs.Server.Bench/        the load driver and its README with the results
```

`Session` is the whole protocol; a transport only moves bytes and reports close. Both transports and the tests use
the same `Session`, so the SignalR path tests nothing but its own plumbing.

## Packages

P0 (the coordinator) creates both projects, adds them to `Ycs.sln` and CI, and deletes `samples/YcsSample`. Then P1
and P2 run in parallel, P3 after both, P4 and P8 after P3, P5 and P6 after P4, P7 last. The coordinator adds the CI
steps at each merge. Each leaves every suite green.

| pkg | concern | owns | proof | risk |
| --- | --- | --- | --- | --- |
| P1 | Awareness port | `samples/Ycs.Server/Awareness/*`, `tests/Ycs.Server.Tests/AwarenessTests.cs`, `interop/server/gen-awareness.mjs` | byte vectors written by y-protocols/awareness decode and re-encode identically; clocks, removal, outdated states | low |
| P2 | Rooms and Session, sync only | `Rooms/*`, `tests/Ycs.Server.Tests/SessionTests.cs` | in-memory peers: initial sync both ways, live fan-out, offline edits merged on reconnect, a bad message closes only its connection and leaves the document as it was | medium |
| P3 | Awareness in the room, WebSocket transport | `Rooms/*` (awareness), `Program.cs`, `Transports/WebSocketEndpoint.cs` | a `y-websocket` provider in Node syncs a text and its awareness with the server and with a second provider; a closed connection's awareness states vanish | low |
| P4 | Convergence driver | `interop/server/*` | N clients (Y.Text, Y.Map, Y.Array), random edits, disconnects, offline edits, reconnects, from a seed; after quiescence every client and a fresh client equal the merge of every update any client made, built in plain Yjs. Runs against the Node reference server and the Ycs server. A seed that fails is printed and replays. | medium |
| P5 | SignalR transport | `Transports/SyncHub.cs`, `interop/server/signalr-provider.mjs` | P4's driver passes over SignalR | low |
| P6 | Benchmark | `bench/Ycs.Server.Bench/*` | updates/s at 10/100/1,000 clients per room, p50/p99 fan-out latency, memory per room after 1 h, the cost of a reconnect diff on a 1 MB document; Ycs and Node servers on one machine, results in its README | low |
| P8 | Blazor client | new `samples/Ycs.Client/**` (a library: the y-websocket provider over `ClientWebSocket`, the client side of awareness, the text binding), new `samples/Ycs.Blazor/**` (the page), new `tests/Ycs.Client.Tests/**`, new `interop/blazor/**`, `Ycs.sln` | the provider syncs and keeps presence against the in-process server, reconnects and merges offline edits; in a browser (Playwright), the Blazor page and a `y-websocket` client edit one text and converge | medium |
| P7 | Demo page, docs | `wwwroot/*`, `README.md` ("Demo"), `docs/testing.md` | two browsers edit one room over each transport (Playwright) | low |

A soak (P4's driver for hours, memory sampled) runs by hand like the fuzz soaks; how is in `docs/testing.md`.

## Out of scope

Persistence (the document lives as long as the process), authentication, many documents per connection
(Hocuspocus), subdocuments, horizontal scaling. The server is a sample and a test bed, not a product; hardening for
the open internet is not a goal.

## Decisions taken with this plan (2026-11-16)

1. **`samples/YcsSample` is replaced** (React and Monaco on 2021 dependencies, untested) by `samples/Ycs.Server`,
   which keeps SignalR as the second transport.
2. **npm packages for the tests and the demo:** `y-websocket` (the client provider), `@y/websocket-server` (the
   reference server, the oracle and the benchmark baseline), `y-protocols`, `ws`, `@microsoft/signalr`;
   `y-codemirror.next` and CodeMirror for the demo page.
3. **No NuGet package** beyond the shared framework: SignalR's JSON protocol, not MessagePack; the Blazor client
   (P8) takes `Microsoft.AspNetCore.Components.WebAssembly`.
4. **A Blazor WebAssembly client** (P8), ported from Plenum's Blazor client (`clients/` in Plenum, which Plenum's
   contract v2 removes): Ycs as a client in the browser, editing with Yjs clients through the Ycs server.
5. **Awareness stays in the server sample**; the client compiles the same file (shared source, as
   `Ycs.WasmSample` compiles the AOT sample's program). No awareness API in the Ycs library until a consumer outside
   this repository needs one.
