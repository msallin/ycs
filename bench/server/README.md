# The sync server against the Node reference server

A benchmark of `samples/Ycs.Server` (one Ycs document per room) against `@y/websocket-server` 0.1.1, the reference
y-websocket server (Yjs 13.6.33), on one machine: each server started fresh on a free port for each scenario, pinned
to its own cores; the load driver on the other cores. Every run ends with a check of every room's text against the
merge of what was sent, built in plain Yjs; a run that fails it is shown as "not counted".

```bash
bench/server/run.sh                    # all three scenarios, defaults below; results in results/<UTC date>/
bench/server/run.sh fanout --clients 100 --rate 1,2,4,8 --duration 60   # a rate sweep for rooms of 100
bench/server/run.sh memory --duration 3600                              # the soak: an hour of editing
node bench/server/report.mjs bench/server/results/2026-11-19-29facf5            # the tables below, from the JSON
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

## Results (2026-11-19, a dedicated runner)

`.github/workflows/bench.yml` (`bench/server/run.sh all`) at 29facf5 on a machine that ran nothing else, 4 cores: the server on 2, the driver on the other 2.
Every run passed its check. JSON in [`results/2026-11-19-29facf5/`](results/2026-11-19-29facf5/).

#### Fan-out (2026-11-19T22:18Z)

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16 GiB, kernel 6.8.0-124-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-1, driver on cores 2,3 (2 workers); load average at start 1.7 1.3 1.3.

| clients per room × rooms | rate per client | server | offered upd/s | delivered upd/s | latency p50 / p95 / p99 ms | server cores | server CPU µs per delivery | server waited for a core (s) | server RSS peak MiB | drain s | driver: busiest worker / loop delay p99 ms | checks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 10 × 100 | 4/s | node | 4000 | 3979 | 1.4 / 8.9 / 30.4 | 0.84 | 23.5 | 0.5 | 162 | 0.1 | 37% / 11.8 | ok |
| 10 × 100 | 4/s | ycs | 4000 | 3977 | 0.6 / 1.4 / 2.0 | 0.93 | 25.9 | 8.5 | 164 | 0.1 | 33% / 11.0 | ok |
| 100 × 10 | 1/s | node | 1000 | 773 | 3474.8 / 8286.3 / 11851.1 | 1.01 | 13.2 | 0.3 | 163 | 5.9 | 52% / 11.8 | ok |
| 100 × 10 | 1/s | ycs | 1000 | 1000 | 2.0 / 5.3 / 7.4 | 1.53 | 15.5 | 12.4 | 139 | 0.0 | 57% / 12.3 | ok |
| 1000 × 1 | 0.1/s | node | 100 | 73 | 5127.1 / 7077.4 / 7220.1 | 1.01 | 13.7 | 0.3 | 220 | 7.2 | 51% / 11.1 | ok |
| 1000 × 1 | 0.1/s | ycs | 100 | 100 | 8.9 / 16.4 / 20.0 | 1.32 | 13.2 | 9.5 | 135 | 0.0 | 56% / 12.8 | ok |

#### Join cost (2026-11-19T22:21Z)

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16 GiB, kernel 6.8.0-124-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-1, driver on cores 2,3 (2 workers); load average at start 2.4 1.7 1.4.

Document: 1.12 MB state (v1), 524260 characters of text; the stale client holds the state after 90% of the edits.

| client | joins at once | server | step 2 bytes | connect → synced p50 / p95 / max ms | server CPU ms per join | server waited for a core (s) | checks |
| --- | --- | --- | --- | --- | --- | --- | --- |
| fresh | 1 (× 3) | node | 1117050 | 22.9 / 33.0 / 33.0 | 40.0 | 0.1 | ok |
| fresh | 1 (× 3) | ycs | 1117050 | 10.3 / 14.0 / 14.0 | 30.0 | 0.0 | ok |
| fresh | 10 (× 3) | node | 1117050 | 150.6 / 276.0 / 277.5 | 23.0 | 0.4 | ok |
| fresh | 10 (× 3) | ycs | 1117050 | 172.6 / 285.6 / 287.0 | 29.7 | 0.1 | ok |
| fresh | 100 (× 3) | node | 1117050 | 854.3 / 1560.0 / 1798.4 | 20.3 | 3.1 | ok |
| fresh | 100 (× 3) | ycs | 1117050 | 661.5 / 985.5 / 1014.0 | 10.8 | 1.4 | ok |
| stale | 1 (× 3) | node | 160422 | 6.1 / 9.3 / 9.3 | 36.7 | 0.1 | ok |
| stale | 1 (× 3) | ycs | 160422 | 3.9 / 4.2 / 4.2 | 3.3 | 0.0 | ok |
| stale | 10 (× 3) | node | 160422 | 124.7 / 203.3 / 206.5 | 6.7 | 0.1 | ok |
| stale | 10 (× 3) | ycs | 160422 | 17.6 / 129.7 / 139.3 | 3.0 | 0.0 | ok |
| stale | 100 (× 3) | node | 160422 | 200.3 / 341.9 / 366.5 | 3.9 | 0.4 | ok |
| stale | 100 (× 3) | ycs | 160422 | 206.3 / 307.0 / 309.7 | 2.3 | 0.1 | ok |

#### Memory per room (2026-11-19T22:22Z)

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16 GiB, kernel 6.8.0-124-generic, Node v24.22.3, .NET SDK 11.0.100; server on cores 0-1, driver on cores 2,3 (2 workers); load average at start 1.9 1.7 1.4.

100 rooms, each loaded with the first 20000 edits of the trace (23490 bytes of state, 14302 characters), then 2 clients per room typing 2/s each for 120 s. Heap: the managed heap after a full compacting GC (Ycs), V8's used heap plus external memory after a full GC (Node).

| server | empty: RSS / heap MiB | per room built: RSS / heap KiB | per room after editing: RSS / heap KiB | editing latency p50 / p99 ms | checks |
| --- | --- | --- | --- | --- | --- |
| ycs | 63.3 / 0.8 | 510 / 205 | 1244 / 219 | 0.3 / 0.6 | ok |
| node | 82.9 / 13.9 | 481 / 308 | 1743 / 276 | 0.3 / 0.8 | ok |

Observations:

* **Throughput**: Node delivers 773 of 1,000 updates/s in the rooms of 100 and 73 of 100 in the room of 1,000, at p50 of
  3.5 and 5.1 s; Ycs delivers everything with p99 at most 20 ms, using 0.9 to 1.5 cores.
* **CPU per delivery**: Ycs 25.9 µs in rooms of 10 (Node 23.5), 15.5 in rooms of 100 (Node 13.2) and 13.2 in the room of
  1,000 (Node 13.7). Its threads waited 8 to 12 s for one of its two cores (more threads than cores); Node's one thread
  did not wait.
* **Join**: a fresh client of the 1.1 MB document syncs in 10 ms on Ycs and 23 ms on Node (30 and 40 ms of server CPU);
  100 at once take 662 against 854 ms at p50 and 1,014 against 1,798 ms at the maximum, at 11 against 20 ms of CPU each.
  A stale client syncs in 3.9 against 6.1 ms (3.3 against 36.7 ms of CPU).
* **Memory per room**: built, Ycs 510 KiB RSS / 205 KiB heap against Node's 481 / 308; after 120 s of editing Ycs holds
  1,244 KiB RSS per room and Node 1,743.
