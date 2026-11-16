# The sync server against the Node reference server

A benchmark of `samples/Ycs.Server` (one Ycs document per room) against `@y/websocket-server` 0.1.1, the reference
y-websocket server (Yjs 13.6.33), on one machine: each server started fresh on a free port for each scenario, pinned
to its own cores; the load driver on the other cores. Every run ends with a check of every room's text against the
merge of what was sent, built in plain Yjs; a run that fails it is shown as "not counted".

```bash
bench/server/run.sh                    # all three scenarios, defaults below; results in results/<UTC date>/
bench/server/run.sh fanout --clients 100 --rate 1,2,4,8 --duration 60   # a rate sweep for rooms of 100
bench/server/run.sh memory --duration 3600                              # the soak: an hour of editing
node bench/server/report.mjs bench/server/results/2026-11-16             # the tables below, from the JSON
```

`run.sh` installs the npm packages (`package.json`: the versions `interop/server` pins, nothing else), fetches the
automerge-paper trace with `../Ycs.Benchmarks/get-trace.sh` into `.trace/` (not committed; CC BY 4.0,
[josephg/editing-traces](https://github.com/josephg/editing-traces)) and checks its SHA-256, builds the server in
Release, and runs `bench.mjs` under `taskset`: the server on `SERVER_CPUS` (default the first half of the cores,
`0-3` here), the driver on `DRIVER_CPUS` (the second half). Without `taskset` it says so and shares every core.
Each scenario writes `results/<date>/<scenario>.json` (every number, the options, the machine, the load average before
and after); `--out` puts them elsewhere, `--servers ycs` or `--servers node` runs one side.

## What is measured

**The servers.** Ycs: `host/` compiles the sample's sources unchanged (all but `Program.cs`) with the sample's SDK and
settings (Server GC, as the Web SDK sets it) and adds one endpoint, `/bench/memory`, that forces a full compacting GC
and returns the managed heap. Node: `node-server.mjs` is `@y/websocket-server`'s `server.js` with the same endpoint
(`--expose-gc`; V8's used heap plus external memory). Neither endpoint is called while a load runs. Server CPU is
`utime + stime` of the process (all threads) from `/proc`, RSS is sampled every 500 ms; "waited for a core" is the
time the server's threads were runnable but not running (`/proc/<pid>/task/*/schedstat`), which shows how much of a
result is the machine's.

**The driver.** Raw y-protocols clients over `ws` (no compression), spread round robin over `worker_threads` (one per
driver core); no Yjs document per client. Each typing client's updates are generated with Yjs before the run (one
character per update, each at the end of what that client typed before, so an update depends only on the client's
own earlier ones: struct `(client, k)` is its update `k`). A receiver reads the client and clock of an update's first
struct and looks up when it was sent in a `SharedArrayBuffer` written by the sender (one monotonic clock for every
worker), so latency is send to receive, with no clock skew. The driver reports its own CPU: the busiest worker
thread's share of a core, and the worst event loop delay p99 of the workers. The reference server echoes each update
to its sender, Ycs does not; echoes are counted apart and not as deliveries.

**1. Fan-out** (`fanout`). `--total` connections (default 1,000) in rooms of `--clients` (10, 100, 1,000: 100, 10 and
1 rooms), every client typing at `--rate` updates/s (default 4, 1 and 0.1/s: about 36,000, 99,000 and 100,000
deliveries/s, so that the driver's receive load is the same order for each size) for `--duration` s (default 20), the
first `--warmup` s (5) not in the latency figures. Then the driver waits until every update reached every other
client of its room (or nothing arrived for 10 s). Reported: offered updates/s; delivered updates/s (deliveries per
receiver over typing plus drain time: below the offered rate when the server fell behind); latency percentiles; drain
time; server cores, CPU per delivery, RSS peak. Checks: no delivery missing, no connection closed by the server,
every room's text equal to the merge of what its clients sent.

**2. Join cost** (`join`). One room holding `--copies` (5) authors each typing the whole trace after the previous
one, one transaction per trace transaction: 1.12 MB of state (v1), 524,260 characters of text, 911,575 typed. Then
batches of `--joins` (1, 10, 100) clients joining at once, each batch `--repeat` (3) times, after 10 warm-up joins of
each kind: a fresh client (empty state vector) and a stale one (the state vector after 90% of the edits). Reported:
connect to sync step 2 received, server CPU per join. Check: the first join of each worker applies its step 2 (to the
stale state for a stale client) and compares the text.

**3. Memory per room** (`memory`). `--rooms` (100) rooms each loaded with the first `--room-txns` (20,000) edits of
the trace (23,490 bytes of state); RSS and heap of the empty server, after loading, and after `--editors` (2) clients
per room typed `--rate` (2/s) for `--duration` s (120; the plan's hour is `--duration 3600`). Per room: the difference
to the empty server divided by the rooms. Check: every room's text after editing.

## Results (2026-11-16, preliminary)

**Not a comparison yet.** These runs validate the benchmark; the numbers come from a machine that was far beyond
busy: load average 42 to 79 on 8 cores during the runs (other agents building, testing and running browsers, and
eight CPU-bound processes on every core). Pinning keeps the server and the driver apart from each other, not from
the rest. The scheduler shares a core per thread, so the single-threaded Node server got 0.15 to 0.26 cores and
waited 57 to 87 s for a core in runs of 60 to 115 s, and it fell far behind (latencies of tens of seconds; its
own 30 s ping timeout closed 62 connections in the room of 1,000, so that run is not counted); the Ycs server, with a
thread per core, got 0.7 to 0.8 cores. Throughput and latency below say more about that than about the servers. CPU
per delivery and the memory figures are less sensitive to it. The coordinator reruns the full set on a quiet machine.

The driver was never the bottleneck: its busiest worker used at most 11% of a core. Its event loop delay p99 (up to
58 ms) is the busy machine's, and is part of the latencies measured.

### Fan-out (2026-11-16T15:27Z)

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 31 GiB, kernel 7.0.0-43-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-3, driver on cores 4-7 (4 workers); load average at start 60.5 54.8 35.1.

| clients per room × rooms | rate per client | server | offered upd/s | delivered upd/s | latency p50 / p95 / p99 ms | server cores | server CPU µs per delivery | server waited for a core (s) | server RSS peak MiB | drain s | driver: busiest worker / loop delay p99 ms | checks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 10 × 100 | 4/s | node | 4000 | 1280 | 25008.3 / 44667.5 / 46857.6 | 0.15 | 13.0 | 57.0 | 162 | 42.5 | 3% / 17.9 | ok |
| 10 × 100 | 4/s | ycs | 4000 | 3972 | 26.1 / 83.6 / 115.1 | 0.70 | 19.5 | 99.4 | 194 | 0.1 | 11% / 47.7 | ok |
| 100 × 10 | 1/s | node | 1000 | 174 | 48951.6 / 96718.6 / 104247.4 | 0.26 | 15.2 | 86.8 | 225 | 94.6 | 4% / 17.5 | ok |
| 100 × 10 | 1/s | ycs | 1000 | 996 | 51.0 / 141.0 / 198.9 | 0.83 | 8.4 | 165.8 | 195 | 0.0 | 9% / 57.5 | ok |
| 1000 × 1 | 0.1/s | node | 100 | 21 | 46159.2 / 80984.4 / 82431.3 | 0.26 | 12.5 | 70.8 | 225 | 74.2 | 8% / 26.8 | **not counted**: 62 connections closed by the server |
| 1000 × 1 | 0.1/s | ycs | 100 | 99 | 45.8 / 124.2 / 180.7 | 0.76 | 7.7 | 116.8 | 204 | 0.1 | 11% / 42.1 | ok |

### Join cost (2026-11-16T15:33Z)

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 31 GiB, kernel 7.0.0-43-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-3, driver on cores 4-7 (4 workers); load average at start 41.8 51.9 41.2.

Document: 1.12 MB state (v1), 524260 characters of text; the stale client holds the state after 90% of the edits.

| client | joins at once | server | step 2 bytes | connect → synced p50 / p95 / max ms | server CPU ms per join | server waited for a core (s) | checks |
| --- | --- | --- | --- | --- | --- | --- | --- |
| fresh | 1 (× 3) | node | 1117050 | 130.4 / 143.5 / 143.5 | 13.3 | 0.4 | ok |
| fresh | 1 (× 3) | ycs | 1117050 | 27.2 / 56.3 / 56.3 | 16.7 | 0.1 | ok |
| fresh | 10 (× 3) | node | 1117050 | 933.4 / 1904.1 / 1922.8 | 15.7 | 3.8 | ok |
| fresh | 10 (× 3) | ycs | 1117050 | 311.5 / 1653.0 / 1656.6 | 16.7 | 6.8 | ok |
| fresh | 100 (× 3) | node | 1117050 | 5676.4 / 10475.2 / 12471.0 | 17.7 | 38.7 | ok |
| fresh | 100 (× 3) | ycs | 1117050 | 3183.8 / 4789.2 / 5009.9 | 8.9 | 33.3 | ok |
| stale | 1 (× 3) | node | 160422 | 14.2 / 23.6 / 23.6 | 3.3 | 0.1 | ok |
| stale | 1 (× 3) | ycs | 160422 | 12.7 / 17.8 / 17.8 | 3.3 | 0.2 | ok |
| stale | 10 (× 3) | node | 160422 | 826.0 / 1622.8 / 1622.8 | 5.0 | 1.3 | ok |
| stale | 10 (× 3) | ycs | 160422 | 151.0 / 1029.7 / 1029.9 | 2.0 | 1.6 | ok |
| stale | 100 (× 3) | node | 160422 | 1191.2 / 2242.4 / 2610.5 | 3.0 | 6.1 | ok |
| stale | 100 (× 3) | ycs | 160422 | 1093.9 / 1782.5 / 2352.5 | 1.9 | 14.5 | ok |

### Memory per room (2026-11-16T15:36Z)

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 31 GiB, kernel 7.0.0-43-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-3, driver on cores 4-7 (4 workers); load average at start 78.9 62.8 46.9.

100 rooms, each loaded with the first 20000 edits of the trace (23490 bytes of state, 14302 characters), then 2 clients per room typing 2/s each for 120 s. Heap: the managed heap after a full compacting GC (Ycs), V8's used heap plus external memory after a full GC (Node).

| server | empty: RSS / heap MiB | per room built: RSS / heap KiB | per room after editing: RSS / heap KiB | editing latency p50 / p99 ms | checks |
| --- | --- | --- | --- | --- | --- |
| ycs | 55.2 / 0.7 | 770 / 291 | 1699 / 298 | 2.1 / 14.0 | ok |
| node | 83.2 / 13.9 | 504 / 311 | 849 / 275 | 2.9 / 174.8 | ok |

Observations (from these runs, to be confirmed on a quiet machine):

* **CPU per delivery** (fan-out, the least load-sensitive figure): Ycs 19.5 µs in rooms of 10, 8.4 in rooms of 100,
  7.7 in the room of 1,000; Node 13.0, 15.2 and 12.5. Ycs's fixed cost per update (receive, apply under the room
  lock, one enqueue per receiver) is higher than Node's where it is spread over only 9 receivers.
* **Ycs's RSS grows with editing while its heap does not**: per room 770 KiB RSS / 291 KiB heap after loading,
  1,699 / 298 after 120 s of editing; the GC's committed memory went from 64 to 131 MB (Server GC, a heap per core).
  Node: 504 / 311 and 849 / 275. The live data per room is the same order on both (Ycs 291 KiB, Node 311 KiB for
  23 KB of state and 14,302 characters).
* **Join**: a fresh client of the 1.1 MB document syncs in 27 ms on Ycs and 130 ms on Node alone, at a similar server
  CPU (17 and 13 ms per join); the stale diff (160 KB) costs 2 to 5 ms of server CPU per join. The step 2 bytes are
  identical in size on both servers.
