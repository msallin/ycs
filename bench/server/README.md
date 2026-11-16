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

## Results (2026-11-16, 18:12 to 18:19 UTC)

A quieter machine than the first run (load average 42 to 79, replaced): the fan-out ran at load average 1.9; a
compiler and a test run of another session started during the join and memory runs (load average 6.4 and 6.8 at their
start), which affects their latencies little and their CPU and memory figures less. Every run passed its check. The
driver's busiest worker used at most 26% of a core.

### Fan-out (2026-11-16T18:12Z)

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 31 GiB, kernel 7.0.0-43-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-3, driver on cores 4-7 (4 workers); load average at start 1.9 0.7 0.7.

| clients per room × rooms | rate per client | server | offered upd/s | delivered upd/s | latency p50 / p95 / p99 ms | server cores | server CPU µs per delivery | server waited for a core (s) | server RSS peak MiB | drain s | driver: busiest worker / loop delay p99 ms | checks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 10 × 100 | 4/s | node | 4000 | 4000 | 0.6 / 1.6 / 2.9 | 0.59 | 16.4 | 0.4 | 158 | 0.0 | 15% / 11.1 | ok |
| 10 × 100 | 4/s | ycs | 4000 | 3999 | 0.8 / 4.7 / 8.8 | 0.74 | 20.6 | 16.6 | 190 | 0.0 | 15% / 13.7 | ok |
| 100 × 10 | 1/s | node | 1000 | 879 | 1245.0 / 4300.7 / 5641.4 | 0.93 | 10.7 | 2.2 | 166 | 2.8 | 23% / 14.7 | ok |
| 100 × 10 | 1/s | ycs | 1000 | 1000 | 2.0 / 10.7 / 24.2 | 1.27 | 12.8 | 39.2 | 200 | 0.0 | 23% / 17.5 | ok |
| 1000 × 1 | 0.1/s | node | 100 | 89 | 1458.6 / 2445.8 / 2486.4 | 0.99 | 11.2 | 0.6 | 226 | 2.5 | 25% / 13.9 | ok |
| 1000 × 1 | 0.1/s | ycs | 100 | 100 | 4.8 / 10.9 / 14.8 | 1.29 | 12.9 | 14.3 | 203 | 0.0 | 26% / 13.9 | ok |

### Join cost (2026-11-16T18:14Z)

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 31 GiB, kernel 7.0.0-43-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-3, driver on cores 4-7 (4 workers); load average at start 6.4 3.4 1.7.

Document: 1.12 MB state (v1), 524260 characters of text; the stale client holds the state after 90% of the edits.

| client | joins at once | server | step 2 bytes | connect → synced p50 / p95 / max ms | server CPU ms per join | server waited for a core (s) | checks |
| --- | --- | --- | --- | --- | --- | --- | --- |
| fresh | 1 (× 3) | node | 1117050 | 14.8 / 15.2 / 15.2 | 23.3 | 0.1 | ok |
| fresh | 1 (× 3) | ycs | 1117050 | 7.4 / 7.6 / 7.6 | 23.3 | 0.0 | ok |
| fresh | 10 (× 3) | node | 1117050 | 107.3 / 213.9 / 255.6 | 22.7 | 0.2 | ok |
| fresh | 10 (× 3) | ycs | 1117050 | 40.8 / 215.6 / 270.7 | 21.7 | 0.1 | ok |
| fresh | 100 (× 3) | node | 1117050 | 531.4 / 1021.5 / 1141.4 | 14.8 | 0.7 | ok |
| fresh | 100 (× 3) | ycs | 1117050 | 414.1 / 486.4 / 532.5 | 7.2 | 0.8 | ok |
| stale | 1 (× 3) | node | 160422 | 4.5 / 5.6 / 5.6 | 10.0 | 0.0 | ok |
| stale | 1 (× 3) | ycs | 160422 | 3.6 / 4.2 / 4.2 | 6.7 | 0.0 | ok |
| stale | 10 (× 3) | node | 160422 | 92.0 / 152.5 / 155.7 | 4.3 | 0.0 | ok |
| stale | 10 (× 3) | ycs | 160422 | 10.7 / 143.8 / 144.7 | 1.7 | 0.0 | ok |
| stale | 100 (× 3) | node | 160422 | 127.8 / 197.3 / 216.6 | 2.2 | 0.1 | ok |
| stale | 100 (× 3) | ycs | 160422 | 167.4 / 200.0 / 203.8 | 1.4 | 0.3 | ok |

### Memory per room (2026-11-16T18:14Z)

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 31 GiB, kernel 7.0.0-43-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-3, driver on cores 4-7 (4 workers); load average at start 6.8 3.8 1.9.

100 rooms, each loaded with the first 20000 edits of the trace (23490 bytes of state, 14302 characters), then 2 clients per room typing 2/s each for 120 s. Heap: the managed heap after a full compacting GC (Ycs), V8's used heap plus external memory after a full GC (Node).

| server | empty: RSS / heap MiB | per room built: RSS / heap KiB | per room after editing: RSS / heap KiB | editing latency p50 / p99 ms | checks |
| --- | --- | --- | --- | --- | --- |
| ycs | 63.8 / 0.8 | 696 / 292 | 1605 / 298 | 0.1 / 0.7 | ok |
| node | 82.4 / 13.9 | 563 / 331 | 1629 / 266 | 0.1 / 1.1 | ok |

Observations:

* **Throughput**: Ycs delivers everything offered in all three shapes with p99 under 25 ms. Node falls behind in the
  rooms of 100 and in the room of 1,000 (879 of 1,000 and 89 of 100 updates/s delivered, p50 over a second): one
  thread at about one core is its ceiling, while Ycs spreads the fan-out over its four cores (up to 1.3 cores used).
* **CPU per delivery**: Ycs 20.6 µs in rooms of 10 and 12.8 to 12.9 in the larger rooms; Node 16.4, and 10.7 to 11.2.
  Ycs costs about 20 % more CPU per delivered update in every shape, plus a fixed cost per update (receive, apply
  under the room lock, one enqueue per receiver) that rooms of 10 spread over only 9 receivers. Its threads also
  waited 14 to 39 s for a core in its four pinned cores, the price of more threads than cores.
* **Join**: a fresh client of the 1.1 MB document syncs in 7 ms on Ycs and 15 ms on Node alone, at the same server
  CPU (23 ms per join); with 100 fresh clients at once, 414 against 531 ms (p50). The stale diff (160 KB) costs 1 to
  7 ms of server CPU per join on Ycs and 2 to 10 on Node.
* **Memory per room**: built, Ycs 696 KiB RSS / 292 KiB heap against Node's 563 / 331; after 120 s of editing both
  hold about 1.6 MiB RSS per room (Ycs 1,605, Node 1,629) at a similar live heap (298 and 266 KiB). The growth on
  Ycs is the server GC keeping committed memory, not live data, and Node grows as much: no reason to change the GC
  mode of the sample.
