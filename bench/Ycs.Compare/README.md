# ycs against YDotNet

A benchmark of ycs (this repository, pure C#) against [YDotNet](https://github.com/y-crdt/ydotnet) 0.7.0, the
.NET binding of yrs 0.26 (the Rust port of Yjs, called through P/Invoke). Same workloads, same inputs, same machine;
every result is checked before it counts. The current results are those of 2026-11-19 at 655a751, in
[`results/2026-11-19-655a751/`](results/2026-11-19-655a751/) (JIT; `aot/` and `aot-speed/` hold the same suite with every
child a Native AOT build). Older rounds are in git history.

```bash
bench/Ycs.Compare/run.sh            # everything: about 90 minutes; results in results/<UTC date>/
PASSES=1 RUNS=3 bench/Ycs.Compare/run.sh /tmp/quick   # a quicker look
dotnet run -c Release --project bench/Ycs.Compare -- list   # the workloads and hostile cases
bench/run-round.sh <out> [<baseline commit>]   # every bench of the repository, in sequence, as CI's bench.yml does
BASE=<built base> python3 bench/Ycs.Compare/ab.py 4 6 <workloads>   # the ycs side of two builds, interleaved
```

`run.sh` fetches the automerge-paper trace with `../Ycs.Benchmarks/get-trace.sh` (900 KB, not committed; CC BY 4.0,
[josephg/editing-traces](https://github.com/josephg/editing-traces), pure ASCII) and checks its SHA-256, builds in
Release, runs the suite, then `aot/measure.sh` and `wasm/probe.sh`. `results.json` holds every run of every process
with the load average; `tables.md` is the rendered form, with the hostile updates. `ab.py` compares the ycs side of
workloads for two builds built from separate trees (how the changes below were measured);
`COMPARE_ALLOC_TYPES=1` on a child prints its allocations by type to stderr. `run-round.sh` builds the baseline
(default 044bb73, the last commit with items as objects) from `git archive` and writes `ab-<commit>.txt`.

CI builds the project (it is in `Ycs.slnx`) and does not run it. It is the only place YDotNet is referenced: neither
`src`, the tests nor `bench/Ycs.Benchmarks` depends on a native library.

## Method

* **One process per workload, library and pass.** The driver runs each workload as a child process for ycs and then for
  YDotNet, three passes, alternating which library goes first. A child warms up until its last 3 runs are within 10 %
  of each other (at least 3 runs and 2 s, at most 200 runs or 30 s; the first run is reported as "first run", the cold
  JIT), then makes 5 timed runs, each on a fresh document built by an untimed setup, after a forced compacting full
  GC. A heavy workload (a minute or more on one side) has one timed run and no warm-up. Release build, workstation
  non-concurrent GC, TieredPGO on, same thread.
* **Same inputs.** Every input comes from SplitMix64 with a fixed seed (`Inputs.cs`); the fingerprint `inputs` in each
  result proves they were the same. The apply workloads read updates the driver wrote once; both libraries apply both
  libraries' bytes.
* **Checks (a result that fails them is "not counted").** Both libraries' content is equal; equal to a plain model
  (a string, list or dictionary replay) where one exists; and each library's state decoded by both libraries gives the
  same content. The driver records whether the two v1 encodings are byte-identical (they are everywhere except after
  the concurrent merge, where the content still agrees).
* **Wall time:** `Stopwatch` around the body only; the median of the 15 timed runs.
* **Allocations:** `GC.GetTotalAllocatedBytes(precise: true)` across the body. Managed only: yrs allocates through
  `malloc`, so the allocation columns of the two libraries are not comparable.
* **Memory held by the finished document:** managed `GC.GetTotalMemory(true)` plus the C heap in use (glibc
  `mallinfo2`), with the document alive. The error is about 1 MiB; the figure includes what the workload's setup keeps
  alive (the updates to apply), the same for both libraries.
* **Idiomatic use.** ycs: implicit transactions per call, `Transact` to group, `UpdateLimits.Unlimited` for a stored
  (trusted) state, default limits for peers' updates. YDotNet, as its README uses it: a `WriteTransaction` per change, a
  `ReadTransaction` per read, root types fetched before any transaction, `Input`s disposed. Where batching is the
  idiomatic fast path it is measured too, labelled `-1txn`. Integers are written with `Input.Double` on YDotNet, which
  is what yrs encodes as a Yjs number; with it both libraries write the same bytes.
* **Machine of the current round:** a dedicated runner (AMD EPYC-Genoa, 4 logical cores, 16 GiB, Ubuntu 24.04, kernel
  6.8, .NET 11.0.0, Node v24.22.3) that ran nothing else; load about 1 while it measured (one benchmark process on a
  core). Between two rounds a difference under about 3 % is noise. Allocation and held memory are deterministic.

## ycs against YDotNet

Median ms per run, 3 passes of 5 timed runs. "Held" is the managed plus native heap after the run, in MiB. The ranges,
the first runs, the allocations and the state sizes are in `tables.md`. Every result passed its checks.

| workload | ycs ms | YDotNet ms | faster | held MiB ycs / YDotNet |
| --- | ---: | ---: | --- | ---: |
| `text-append` | 27.8 | 3561 | ycs 128.2x | 0.6 / 0.6 |
| `text-append-utf8` | 27.3 | 1821 | ycs 66.8x | 0.6 / 0.5 |
| `text-append-1txn` | 16.0 | 26275 | ycs 1643.3x | 1.4 / 4.1 |
| `text-append-1txn-25k` | 4.5 | 1422 | ycs 313.2x | 0.4 / 0.9 |
| `text-append-1txn-observed` | 226 | 26758 | ycs 118.2x | 1.9 / 3.6 |
| `text-append-1txn-observed-200k` | 463 | 124566 | ycs 268.8x | 4.5 / 6.9 |
| `text-append-1txn-observed-400k` | 962 | 519916 | ycs 540.6x | 8.1 / 13.6 |
| `text-append-1txn-observed-25k` | 55.5 | 1428 | ycs 25.7x | 0.6 / 1.0 |
| `txn-empty` | 8.1 | 12.1 | ycs 1.5x | 0.0 / 0.0 |
| `text-random` | 37.2 | 1766 | ycs 47.5x | 3.1 / 4.4 |
| `text-random-100k` | 1387 | 181384 | ycs 130.7x | 20.7 / 22.3 |
| `trace-replay` | 360 | 12122 | ycs 33.7x | 1.6 / 2.6 |
| `concurrent-20` | 67.0 | 79.9 | ycs 1.2x | 14.1 / 25.4 |
| `many-clients-200` | 1.5 | 2.3 | ycs 1.5x | 0.5 / 0.6 |
| `many-clients-1000` | 5.7 | 24.4 | ycs 4.3x | 2.0 / 2.2 |
| `apply-state-v1/ycs` | 49.1 | 48.4 | YDotNet 1.0x | 2.8 / 2.7 |
| `apply-state-v1/yrs` | 48.3 | 48.9 | ycs 1.0x | 2.8 / 2.7 |
| `apply-state-v2/ycs` | 48.1 | 50.2 | ycs 1.0x | 2.7 / 2.6 |
| `apply-state-v2/yrs` | 48.7 | 50.1 | ycs 1.0x | 2.7 / 2.6 |
| `apply-state-x100/ycs` | 613 | 754 | ycs 1.2x | 243.7 / 280.1 |
| `apply-updates/ycs` | 235 | 368 | ycs 1.6x | 17.2 / 15.8 |
| `apply-updates/yrs` | 240 | 370 | ycs 1.5x | 17.2 / 15.8 |
| `apply-updates-1txn/yrs` | 137 | 1794 | ycs 13.1x | 27.6 / 21.0 |
| `encode-v1` | 8.2 | 8.9 | ycs 1.1x | 1.6 / 2.5 |
| `encode-v2` | 10.2 | 10.2 | YDotNet 1.0x | 2.2 / 2.5 |
| `encode-sv` | 0.1 | 0.2 | ycs 2.5x | 2.6 / 2.5 |
| `encode-diff` | 4.9 | 4.6 | YDotNet 1.1x | 2.1 / 2.5 |
| `map-set-distinct` | 41.0 | 144 | ycs 3.5x | 24.9 / 30.9 |
| `map-set-distinct-1txn` | 31.0 | 126 | ycs 4.1x | 24.9 / 30.9 |
| `map-set-same` | 67.0 | 139 | ycs 2.1x | 0.1 / 0.0 |
| `array-push` | 25.5 | 115 | ycs 4.5x | 10.4 / 8.2 |
| `array-push-1txn` | 12.4 | 69833 | ycs 5630.4x | 10.4 / 8.8 |
| `array-push-1txn-20k` | 2.5 | 2200 | ycs 895.0x | 1.9 / 1.8 |
| `array-insert-random` | 184 | 22230 | ycs 120.8x | 10.4 / 15.9 |
| `array-delete-random` | 183 | 25429 | ycs 139.1x | 10.9 / 14.6 |
| `read-text` | 0.3 | 48.2 | ycs 142.8x | 3.0 / 2.7 |
| `read-array` | 4.0 | 102 | ycs 25.7x | 4.1 / 3.0 |
| `read-map` | 26.2 | 407 | ycs 15.5x | 20.1 / 28.0 |
| `undo-type` | 31.0 | 161 | ycs 5.2x | 11.5 / 5.6 |
| `undo-all` | 43.3 | 366 | ycs 8.5x | 12.1 / 7.0 |
| `redo-all` | 47.8 | 80.5 | ycs 1.7x | 17.4 / 14.8 |
| `observe-count` | 56.5 | 3592 | ycs 63.5x | 1.2 / 1.5 |
| `observe-delta` | 89.9 | 5437 | ycs 60.5x | 1.2 / 0.7 |

**Native AOT** (`aot/measure.sh`, the same minimal program on each, linux-x64): ycs publishes with no trim or AOT
warning, 2.8 MB; YDotNet 4.3 MB (its `libyrs.so` is 2.3 MB of it) with 3 trim warnings (IL2091). Self-contained
trimmed: 21.1 MB against 22.6 MB. Framework-dependent: 0.5 MB against 2.4 MB.
**Browser wasm** (`wasm/probe.sh`): YDotNet publishes, but no package ships a browser-wasm build of yrs, so the first
call fails with `DllNotFoundException` ("yrs"). ycs runs there (`samples/Ycs.WasmSample`).

## Native AOT

The same suite with every child process a Native AOT build (`COMPARE_CHILD=<AOT build> Ycs.Compare suite`: the driver stays JIT; YDotNet's
children are AOT too), on the runner of 2026-11-19, 3 passes of 5 timed runs; `results/2026-11-19-655a751/aot/` is the default
`PublishAot`, `aot-speed/` adds `-p:OptimizationPreference=Speed -p:IlcInstructionSet=native`. Hostile cases not run. Median ms:

| workload | ycs JIT | ycs AOT | ycs AOT Speed + native | YDotNet JIT | YDotNet AOT | AOT: ycs against YDotNet | Speed + native: ycs against YDotNet |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| `text-append` | 27.8 | 51.3 | 48.2 | 3561 | 3621 | ycs 70.6x | ycs 75.3x |
| `text-append-utf8` | 27.3 | 50.3 | 46.9 | 1821 | 1889 | ycs 37.6x | ycs 40.3x |
| `text-append-1txn` | 16.0 | 26.2 | 22.3 | 26275 | 26004 | ycs 993x | ycs 1165x |
| `text-append-1txn-25k` | 4.5 | 6.4 | 5.6 | 1422 | 1438 | ycs 225x | ycs 255x |
| `text-append-1txn-observed` | 226 | 282 | 260 | 26758 | 25846 | ycs 91.7x | ycs 101x |
| `text-append-1txn-observed-200k` | 463 | 577 | 528 | 124566 | 121833 | ycs 211x | ycs 231x |
| `text-append-1txn-observed-400k` | 962 | 1166 | 1089 | 519916 | 513002 | ycs 440x | ycs 471x |
| `text-append-1txn-observed-25k` | 55.5 | 70.6 | 64.2 | 1428 | 1428 | ycs 20.2x | ycs 21.9x |
| `txn-empty` | 8.1 | 12.1 | 11.3 | 12.1 | 11.9 | YDotNet 1.02x | ycs 1.07x |
| `text-random` | 37.2 | 43.9 | 44.1 | 1766 | 1803 | ycs 41.1x | ycs 40.5x |
| `text-random-100k` | 1387 | 794 | 831 | 181384 | 172468 | ycs 217x | ycs 197x |
| `trace-replay` | 360 | 477 | 449 | 12122 | 12246 | ycs 25.7x | ycs 27.1x |
| `concurrent-20` | 67.0 | 88.8 | 82.5 | 79.9 | 78.5 | YDotNet 1.13x | YDotNet 1.05x |
| `many-clients-200` | 1.5 | 1.8 | 1.7 | 2.3 | 2.3 | ycs 1.28x | ycs 1.35x |
| `many-clients-1000` | 5.7 | 7.6 | 7.1 | 24.4 | 25.0 | ycs 3.29x | ycs 3.39x |
| `apply-state-v1/ycs` | 49.1 | 68.9 | 60.5 | 48.4 | 48.3 | YDotNet 1.43x | YDotNet 1.24x |
| `apply-state-v1/yrs` | 48.3 | 69.3 | 61.2 | 48.9 | 48.3 | YDotNet 1.43x | YDotNet 1.26x |
| `apply-state-v2/ycs` | 48.1 | 68.8 | 62.2 | 50.2 | 50.5 | YDotNet 1.36x | YDotNet 1.21x |
| `apply-state-v2/yrs` | 48.7 | 68.6 | 61.5 | 50.1 | 49.9 | YDotNet 1.37x | YDotNet 1.23x |
| `apply-state-x100/ycs` | 613 | 883 | 811 | 754 | 760 | YDotNet 1.16x | YDotNet 1.08x |
| `apply-updates/ycs` | 235 | 411 | 360 | 368 | 364 | YDotNet 1.13x | ycs 1.00x |
| `apply-updates/yrs` | 240 | 418 | 355 | 370 | 367 | YDotNet 1.14x | ycs 1.02x |
| `apply-updates-1txn/yrs` | 137 | 237 | 191 | 1794 | 1783 | ycs 7.52x | ycs 9.22x |
| `encode-v1` | 8.2 | 13.1 | 13.3 | 8.9 | 11.0 | YDotNet 1.19x | YDotNet 1.20x |
| `encode-v2` | 10.2 | 14.9 | 14.2 | 10.2 | 9.9 | YDotNet 1.51x | YDotNet 1.45x |
| `encode-sv` | 0.1 | 0.1 | 0.1 | 0.2 | 0.3 | ycs 3.00x | ycs 3.00x |
| `encode-diff` | 4.9 | 7.0 | 6.8 | 4.6 | 4.7 | YDotNet 1.49x | YDotNet 1.45x |
| `map-set-distinct` | 41.0 | 81.2 | 73.2 | 144 | 225 | ycs 2.77x | ycs 3.06x |
| `map-set-distinct-1txn` | 31.0 | 51.9 | 48.3 | 126 | 208 | ycs 4.01x | ycs 4.22x |
| `map-set-same` | 67.0 | 127 | 112 | 139 | 218 | ycs 1.72x | ycs 1.98x |
| `array-push` | 25.5 | 53.1 | 45.0 | 115 | 257 | ycs 4.84x | ycs 5.49x |
| `array-push-1txn` | 12.4 | 23.9 | 19.1 | 69833 | 67405 | ycs 2820x | ycs 3490x |
| `array-push-1txn-20k` | 2.5 | 4.6 | 3.6 | 2200 | 2263 | ycs 492x | ycs 623x |
| `array-insert-random` | 184 | 206 | 200 | 22230 | 21389 | ycs 104x | ycs 94.3x |
| `array-delete-random` | 183 | 218 | 205 | 25429 | 25591 | ycs 117x | ycs 117x |
| `read-text` | 0.3 | 0.4 | 0.4 | 48.2 | 46.7 | ycs 117x | ycs 114x |
| `read-array` | 4.0 | 5.1 | 6.0 | 102 | 718 | ycs 141x | ycs 117x |
| `read-map` | 26.2 | 37.4 | 35.4 | 407 | 1165 | ycs 31.1x | ycs 32.5x |
| `undo-type` | 31.0 | 28.2 | 25.2 | 161 | 178 | ycs 6.31x | ycs 6.98x |
| `undo-all` | 43.3 | 37.9 | 34.0 | 366 | 340 | ycs 8.97x | ycs 10.3x |
| `redo-all` | 47.8 | 45.6 | 41.5 | 80.5 | 83.5 | ycs 1.83x | ycs 2.00x |
| `observe-count` | 56.5 | 105 | 93.1 | 3592 | 3613 | ycs 34.4x | ycs 38.9x |
| `observe-delta` | 89.9 | 163 | 149 | 5437 | 5648 | ycs 34.7x | ycs 37.6x |

* **Steady state is slower in AOT: ycs 1.1 to 2.1x** (no tiered profile-guided optimization, below). YDotNet's managed side slows too
  (`read-array` 102 to 718 ms).
* **The first run is much faster**: `text-append` 335 ms (JIT) to 51, a state load 476 to 85, `apply-state-x100` 1518 to 996,
  `concurrent-20` 410 to 101. A process that loads one state is faster in AOT end to end.
* **Against YDotNet in AOT** ycs wins what it wins on the JIT (`text-append` 71x, `trace-replay` 26x, `array-push` 4.8x,
  `undo-all` 9x), is 1.4x behind on state loads (`Speed` and `native`: 1.2x), 1.2x on `apply-state-x100` (1.1x), 1.1x on
  `concurrent-20` and `apply-updates` (level), and 1.2 and 1.5x on `encode-v1` and `encode-v2` (the same with the settings).
* **Settings a consuming application can set** (the A/B of `results/2026-11-19-load-path-2d6c79a/aot-settings/`, ycs's default AOT build against
  the variant, `ab.py 3 6`): `OptimizationPreference=Speed` is 3 to 15 % faster (`array-push` -11 %, `apply-updates` -10 %, a state load
  -8 %, `encode-v1` +2 %), `IlcInstructionSet=native` alone 0 to 6 %, both together 7 to 15 %. `native` compiles for the machine that
  publishes; name a set (`x86-x64-v3`) to publish for another.
* **Why the rest**: the JIT with `DOTNET_TieredPGO=0` is 23 to 85 % slower (`text-append` +60 %, `array-push` +85 %, a state load
  +23 %), and with `EnableAVX2=0` as well 23 to 92 %: most of the AOT gap is the missing profile (guarded devirtualization of
  the interface and delegate calls), the instruction set about a tenth of it.
* **Further improvement**: devirtualize the write path by hand, which closes most of `encode-v1` and `encode-v2` and part of the rest
  (`IUpdateEncoder` as a struct-generic parameter, sealed v2 column encoders, no delegate call for the default garbage collection
  filter): the open item in [section 5](../../docs/design-item-layout.md#5-open-items) of the design document.

## The load path (2026-11-19)

Six changes to what a load does per struct, measured on the runner against `df6a0de` (the parent of the first), `ab.py 4 6`,
interleaved, in [`results/2026-11-19-load-path-2d6c79a/`](results/2026-11-19-load-path-2d6c79a/) (`ab-jit-base-vs-cN.txt` is
the base against the Nth commit, `commits.txt` their order):
the garbage collection check runs only where a key can exist (`HasKeys`, `HasKeyedItems`), `Refresh` finds the neighbour
`GetItemCleanStart` returned and the struct `AddStruct` added, the plan keeps its clocks in an array by the table's client
index, a section reserves room for the structs it announces, the staging table keeps 2 MiB between loads (docs: section 5).

| workload | JIT before / after ms | AOT before / after ms | against YDotNet in this round, JIT / AOT |
| --- | ---: | ---: | --- |
| `apply-state-v1/ycs` | 76.4 / 47.7 (-38 %) | 102.3 / 71.1 (-31 %) | ycs 1.01x ahead / YDotNet 1.44x (JIT 1.5x, AOT 2.0x behind) |
| `apply-state-v2/ycs` | 73.9 / 49.4 (-33 %) | 103.2 / 69.2 (-33 %) | ycs 1.01x ahead / YDotNet 1.35x |
| `apply-state-x100/ycs` | 905 / 648 (-28 %) | 1211 / 899 (-26 %) | ycs 1.11x ahead / YDotNet 1.17x |
| `concurrent-20` | 92.2 / 67.8 (-26 %) | 125 / 88.0 (-30 %) | ycs 1.17x ahead / YDotNet 1.09x |
| `apply-updates/ycs` | 272 / 237 (-13 %) | 482 / 416 (-14 %) | ycs 1.5x ahead / YDotNet 1.13x |

A state load allocates 38.1 to 16.2 MiB (of the 21.9 MiB less: the reserve 13, the larger retention 7, the rest 2); `apply-state-x100`
480 to 452 MiB. The rest of the suite is within 3 % in both builds (`text-random` +3.3 % on JIT is the noise of its spread; AOT
`text-append` -4.5 %, `encode-v1` -5 %). By commit, JIT state load: the check's gate -22 %, the hints -24 %, the plan array
-27 %, the reserve -33 %, the retention -35 %. The YDotNet side is the suite of this round (the table at the top and "Native AOT").

### A larger reserve and a pooled table (2026-11-20)

`apply-state-x100` (1.1 million structs in one update) allocated 452 MiB for 241 held: the section's reserve was capped at 65,536
structs, and the staging table then doubled past it (248 MiB of struct arrays for a 70 MiB table). The reserve is now the announced
count as far as the input can hold it (input bytes / 2; the update's struct limit bounds the count), so the table is made once.
`ab.py 5 6` on the runner, `results/2026-11-20-6a12b9d/`: JIT 617 to 519 ms (-16 %), allocation 452 to 267 MiB; the AOT build (measured
with the pooled build below) 871 to 778 ms (-11 %); every other workload within 2 % (state loads 47.4 to 47.9 ms).

A pooled table (struct buffers of 2 MiB or more rented from `ArrayPool<T>.Shared` and returned at `Reset`) cut the allocation of a
repeated large load from 267 to 200 MiB, and the time by nothing: 515 against 527 ms (+2.3 %), the state loads equal. Not kept: no gain
for the code, the retained memory it would leave to the pool's trimming, and a buffer longer than asked. Open only for a thread that
decodes states of 100 MB one after the other, which was not measured (many threads neither). The branch is `pooled-structs`.

## Where ycs is slower now

### Against YDotNet

Ratios are of the medians in the table above (JIT).

| workload | ycs | YDotNet | ratio | cause |
| --- | ---: | ---: | ---: | --- |
| `apply-state-v1/ycs` (load a stored state, 10 times) | 49.1 ms | 48.4 ms | YDotNet 1.01x | level; the other three state loads are 1.04x ahead (v2) to 1.2x ahead (x100) |
| `encode-v2` | 10.2 ms | 10.2 ms | level | |

No workload is clearly slower on the JIT. The first run of every ycs workload is much slower than YDotNet's (JIT; `tables.md`,
"first run": a state load 476 ms against 59, `concurrent-20` 410 against 100): a process that loads one state pays it once;
a Native AOT build has no such run (above). In Native AOT, which has no profile, YDotNet is ahead on state loads (1.4x, 1.2x
with `OptimizationPreference=Speed` and `IlcInstructionSet=native`) and on the encodes (1.2 and 1.5x): see "Native AOT".

Before 2026-11-19 the state loads were 1.5x behind (72 against 48 ms): "The load path" below and
[`docs/design-item-layout.md`](../../docs/design-item-layout.md#5-open-items) for what is left (a pooled staging table, 32-bit
clocks, pending structs as a staging table, a struct-generic write path).

### Against the object layout (044bb73)

044bb73 is the last commit with items as objects. It is built from its own tree and run interleaved with this commit
by `ab.py 4 6` (4 passes of 6 runs; `ab-044bb73.txt`). "Before" is 044bb73, "now" is e794d65. Under about 3 % is noise.
Allocation and held memory are deterministic.

| workload | before ms | now ms | change | alloc MiB before / now | held MiB before / now | cause, and whether it was left |
| --- | ---: | ---: | ---: | --- | --- | --- |
| `trace-replay` | 285.2 | 357.4 | +25 % | 43.04 / 24.86 | 3.0 / 1.6 | A delete walks 13 items in `DeleteText` and 36 in the formatting cleanup (Yjs' algorithm). Each step is a dependent load through two arrays (slab page table, then page); an object field was one load. Left: only a run index of deleted items would shorten the walk |
| `text-append` | 25.0 | 27.6 | +10 % | 0.20 / 0.57 | 1.3 / 0.6 | A per-transaction cost spread over the transaction, the item and the run extension; no phase above 10 %. Left |
| `text-append-1txn-observed` (100,000) | 206.5 | 236.6 | +15 % | 18.77 / 8.30 | 2.3 / 1.9 | The marker walk: each lookup crosses the run and each step reads a slot by index. Cut by 23 % on the way (scans of a leaf through its arrays, the walk left jumps after 8 steps); the rest is spread. Left |
| `text-append-1txn-observed-25k` | 47.5 | 55.4 | +17 % | 4.71 / 2.11 | 0.6 / 0.6 | The same |
| `map-set-same` | 57.3 | 66.6 | +16 % | 28.99 / 6.85 | 0.0 / 0.0 | Spread over the transaction, no single phase. Left |
| `array-push-1txn` | 10.9 | 12.3 | +13 % | 10.39 / 4.28 | 9.4 / 10.4 | The same. Held memory is higher by the arena's 8 bytes per value and its doubling |
| `encode-v1` | 7.0 | 8.3 | +19 % | 2.76 / 2.76 | 3.2 / 1.6 | Spread over the write of the structs, no single cost (about 0.13 ms per encode of a 289 KB state, 13 ns a struct). Accepted: invisible in a join or a save. A v1 write path that bypasses `IUpdateEncoder` would close it at the price of a second encoder |

`apply-state-v1/ycs` (+4 %, 71.3 to 74.3 ms) and `apply-updates/ycs` (-2 %) are at the edge of the noise.

What is not slower: random edits (`text-random-100k` -29 %, `array-insert-random` -34 %, `array-delete-random` -38 %),
map inserts (`map-set-distinct` -34 %), `read-array` (7.5 to 4.0 ms) and `read-map` (27.5 to 26.4 ms). Allocation is lower
on every editing workload and on `apply-updates` (59.3 to 11.9 MiB), and higher on the state loads (33 to 38 MiB, the
staging table). Held memory is 36 to 51 % lower on the documents that keep many structs (`text-random-100k` 27.1 to 16.1 MiB,
`map-set-distinct` 38.9 to 24.9, `concurrent-20` 29.1 to 14.2), higher on the array pushes. The open items are in the
design document, [section 5](../../docs/design-item-layout.md#5-open-items).

## Why: the main changes and what each bought

Each was measured against the commit before it with `ab.py`; the numbers are those of the run that decided it.

* **The first speed pass** (a run of typed characters or pushed values is one item until something observes the type;
  pushes start from the last item; merges at the end of a transaction in one pass). Typing allocates 2 bytes a keystroke
  instead of 202; `text-append-1txn` 58 s to 6 ms; `array-push-1txn` from killed at 8 GiB to 7 ms; the trace replays 10 %
  faster with half the allocations; loading allocates 20 % less. Not gained: random deletes stayed quadratic (60 s) and
  so did typing in one transaction into an observed type, because Yjs' marker walk crosses every piece.
* **The item layout** ([design](../../docs/design-item-layout.md)): each struct is a 64-byte slot of one slab, linked by
  32-bit handles; characters and values live in arenas; an update decodes into a staging table, so no `Item` object
  exists. It replaced a 128-byte object per item plus its content object. Held memory is 49 to 64 % of the object layout's
  on the six workloads of the model, allocation is down 38 to 87 % on most editing workloads, and `apply-updates` allocates
  59.3 to 11.9 MiB. The price is the table above: reading a slot by index costs more than a field of an object the JIT keeps
  in a register.
* **The run-head index** (a flag byte per position and visible sums per block of 512, later per leaf): the marker walks of
  Yjs jump within one client's chain of pieces and end where Yjs' loops end. `array-delete-random` from 57 s to 0.9 s,
  `text-append-1txn-observed` (one transaction, 100,000 characters) from 49 s to 0.8 s. Counting breaks per block made the
  observed typing 2 to 3x faster again.
* **The leaf order** (a client's structs in leaves of at most 128 positions, a directory of leaf sums over them): a split
  shifts one leaf instead of the rest of the list. `apply-updates` -26 %, `trace-replay` -29 %, observed typing linear
  in the characters (400,000 characters in 600 ms, 5.5x faster), held memory halved for it. It costs `apply-state` 7 to
  10 % and `concurrent-20` 10 %. Capacity 128 is best on the split-heavy workloads.
* **`ItemRef`**, a struct around the item (later around the slab handle), so the library can change how a struct is held
  without touching its callers. No allocation. The first build cost the observed typing 8 to 14 %: the JIT's inlining
  budget (the accessors are `AggressiveInlining` now) and a cast on every member read (a type test is faster).
* **The decoders as structs over the input span.** `ApplyUpdate` reads the input in place and the decoders hold no
  buffer of their own, so a string is decoded once, straight into the staging table. Time and allocation equal, within
  the noise of a shared machine; the point is that a load makes no copy of the update.
* **The large-text work.** `YText.Chunks` and `WriteTo` read a text as spans without a copy; `ToString` writes the
  characters straight into the string and a split that changes no character keeps the cached one; a run longer than the
  largest page gets a page of exactly its length, so typing at the end of a large text copies it O(log n) times; a large
  string is encoded into a buffer sized to it, not to three bytes a character; a compaction frees a moved large run's
  page at once.
* **Reading arrays and maps.** `read-array` was 2.7 times slower than on objects. The slab was not the cause: the
  value conversion `YValue.FromStored` returned its 24 bytes through memory and the caller's next read stalled on the store
  forwarding. The int, string and double cases are inlined into the readers now, the array enumerator's step is inlined,
  and `Current` no longer copies the walk state. `read-array` 14.7 to 2.8 ms (8 cores), `read-map` 27.1 to 14.9.
* **The fixed cost of a small document** (bytes per document, `bench/Ycs.Layout/README.md`): the page tables, the change
  log, the delete set, the handler holder and the first pages are made at first use. A one-insert document is 3.3 KB.

## Tried and dropped, or not done

* **One warm-up** (the first suite run): ycs was still reaching tier-1 code in the timed runs (`text-append` 160, 149, 44,
  22, 24 ms). Three were not enough for the encodes; a child warms up until stable now.
* **A flat form below one leaf** (no leaf object while a client has at most 128 structs): 0 to 3 % on the three workloads
  it had to help, because their clients hold far more structs. Not committed.
* **A pivot among the leaves** (Yjs' guess from the clocks): nothing; the cursor decides. Six other variants of the
  leaf's search and cursor (no cursor rewrite when unchanged, a 32-bit division, the indexer's test inlined, ...): none
  beyond 2 %. The leaf order's cost on loads stays.
* **Leaf capacity 64 or 256**: 64 is faster on the observed typing, 256 slower on it and on `array-delete-random`; 128 kept.
  Blocks of 4,096 positions for the run sums: slower (9.3 s for 400,000 characters).
* **Field order of the slot**: nothing beyond the noise. The array's start is only 8-byte aligned, so which slots straddle
  a cache line is luck.
* **Removing the slab's bounds checks**: needs `Unsafe` or `MemoryMarshal` (banned). Reading each slot once per step took
  most of them away.
* **An unchecked cast in `ItemRef`**: `Unsafe` is banned. An exact-type check made the observed typing 20 % slower.
* **A memo of the last changed type in `AddChangedTypeToTransaction`** (a dictionary lookup per integrated struct, skipped
  while a run lands in one type and key): on the runner, `ab.py 6 8`, state loads -4 % (75.0 to 72.0 ms), observed typing
  -1 to -5 %, `text-random` +4 % and `text-append` +2 % (both inside their spread), the rest within 2 %. The 1.5x against
  YDotNet stays. Not kept: the gain is about 3 %, for a field pair, a method and an invariant (cleared wherever an entry
  leaves the changed set). The delete set is applied with a galloping search from the last index found already.
* **Encode micro-variants** (one buffer check for both ids of a header, a short string in one pass, `in` parameters, a
  delete-set walk without building an id): none measurable.
* **Smaller edits to the walks** (a memo of the last changed type, one slot read in `MarkDeleted` and `ExtendRun`, a lower
  literal-walk limit, `Transaction` getters inlined): under 3 %, or worse on another workload.
* **Update operations through the staging table** (merge, diff and convert decoding each input into a staging table
  instead of streaming it): 7 to 20 times the memory (a merge with pending structs 707 KB to 5.4 MB). Dropped; the
  operations stream struct by struct over the input span.
* **Not done**: a Fenwick tree over positions and a full B+tree (the leaf sums are as fast; two levels hold a million
  structs), generation counters in Release, a separate small-document representation, a struct of arrays for the slot,
  pooled item objects (design document, section 4); the delta of a text without formats (`observe-delta` builds three
  attribute dictionaries per event delta: a text cannot reliably know it never held a format); 32-bit clocks, pending
  structs as a staging table, reuse of type-table indexes (section 5).

## Not compared

* XML types and formatting attributes, subdocuments, snapshots and relative positions: both have them, but not with
  the same shapes of API.
* v2 state vectors and update merging without a document (YDotNet exposes neither); native allocations of yrs.

## Not speed

* **Hostile input** (`tables.md`; the cases come from `tests/Ycs.Fuzz`): ycs rejects all 16 malformed or over-limit
  updates with `YUpdateException` and leaves the document unchanged. YDotNet rejects 10, applies 6 (3 malformed ones,
  one of which changes the document, and 3 valid ones) and **one aborts the process**: a Rust panic in
  `yrs/src/block_store.rs:53` is not caught at the FFI boundary, so a server applying peers' updates can be killed by
  one 54-byte message. yrs has no limits: it accepts a 42-byte update that expands to 524,288 deleted items and a
  24-byte one with 2^21 pending structs; ycs rejects both at its default `UpdateLimits`.
* **API differences that matter:** YDotNet needs a transaction object for every call and throws if a root type is
  fetched while one is open; its `Input` and `Output` have finalizers. Number types do not round-trip: `Input.Long`
  is written as a bigint, and a Yjs or ycs integer is read by YDotNet as a `Double`; use `Input.Double`.
* **Platforms** of YDotNet.Native 0.7.0: linux, osx and win on x64 and arm64; not browser-wasm, Android or iOS.

## Pinned versions

`YDotNet` 0.7.0 and `YDotNet.Native` 0.7.0 (which pulls `YDotNet.Native.Linux`, `.MacOS`, `.Win32` 0.7.0), source
commit c14ed517 of y-crdt/ydotnet, built from yrs `release-v0.26`. 0.7.0 was published on 2026-11-10; the previous
release, 0.6.0, is from February 2026.
