Run 2026-11-15 14:26 UTC: 3 passes, each workload in its own process per library and pass, 3 warm-up + 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs B0C9A1DE44B7103F.

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 32859788 kB RAM, Ubuntu 26.04 LTS, kernel 7.0.0.43, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 5.84 8.14 6.62 3/1227 3178302.

YDotNet was not run again except for `text-append`, `map-set-distinct`, `apply-state-v1/ycs`, `encode-v1`: its other measurements, marked (carried), and the content and states the checks read are those of results/2026-11-15.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 32.3 (25.9–45.3, ±2%) | 3058 (2926–3482, ±3%) | ycs 94.6× | 173 / 3147 | equal, v1 bytes identical |
| `text-append-utf8` | 31.0 (18.6–76.2, ±127%) | 1563 (1482–1767, ±5%) (carried) | ycs 50.4× | 170 / 1585 | equal, v1 bytes identical |
| `text-append-1txn` | 47.2 (14.6–95.0, ±21%) | 18154 (17775–18471, ±2%) (carried) | ycs 384.3× | 67.8 / 18154 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 11.3 (10.9–12.8, ±3%) | 1084 (1029–1112, ±4%) (carried) | ycs 96.4× | 32.9 / 1070 | equal, v1 bytes identical |
| `txn-empty` | 32.3 (31.8–33.5, ±1%) | 13.8 (13.3–14.2, ±4%) (carried) | YDotNet 2.3× | 46.7 / 18.0 | equal, v1 bytes identical |
| `text-random` | 23.7 (21.7–84.9, ±68%) | 1512 (1417–1626, ±2%) (carried) | ycs 63.9× | 220 / 1514 | equal, v1 bytes identical |
| `text-random-100k` | 1607 (819–1657, ±49%) | 175337 (146416–202685, ±16%) (carried) | ycs 109.1× | 1607 / 175337 | equal, v1 bytes identical |
| `trace-replay` | 221 (209–438, ±19%) | 8053 (7293–8783, ±7%) (carried) | ycs 36.4× | 751 / 7832 | equal, v1 bytes identical |
| `concurrent-20` | 72.0 (58.1–216, ±21%) | 65.8 (60.5–68.5, ±6%) (carried) | YDotNet 1.1× | 373 / 81.0 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 63.6 (59.9–122, ±9%) | 38.4 (37.9–48.1, ±1%) | YDotNet 1.7× | 355 / 45.7 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 62.5 (58.3–110, ±10%) | 39.9 (37.3–57.0, ±13%) (carried) | YDotNet 1.6× | 364 / 50.3 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 67.5 (56.4–93.8, ±16%) | 40.3 (38.9–43.1, ±6%) (carried) | YDotNet 1.7× | 332 / 49.7 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 63.7 (55.6–107, ±13%) | 40.6 (39.7–43.3, ±5%) (carried) | YDotNet 1.6× | 338 / 50.7 | equal, v1 bytes identical |
| `apply-updates/ycs` | 255 (240–335, ±8%) | 261 (244–305, ±6%) (carried) | ycs 1.0× | 1057 / 265 | equal, v1 bytes identical |
| `apply-updates/yrs` | 252 (237–653, ±83%) | 256 (242–262, ±0%) (carried) | ycs 1.0× | 1054 / 258 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 144 (132–374, ±25%) | 1164 (1121–1245, ±8%) (carried) | ycs 8.1× | 834 / 1200 | equal, v1 bytes identical |
| `encode-v1` | 45.8 (9.0–69.5, ±5%) | 6.2 (6.1–6.5, ±1%) | YDotNet 7.4× | 43.1 / 7.3 | equal, v1 bytes identical |
| `encode-v2` | 27.5 (15.0–72.7, ±22%) | 7.7 (7.1–10.4, ±7%) (carried) | YDotNet 3.6× | 47.2 / 9.0 | equal, v1 bytes identical |
| `encode-sv` | 0.3 (0.2–0.4, ±2%) | 0.2 (0.2–0.3, ±7%) (carried) | YDotNet 1.1× | 3.9 / 1.2 | equal, v1 bytes identical |
| `encode-diff` | 16.0 (15.5–42.8, ±75%) | 3.3 (3.2–3.6, ±5%) (carried) | YDotNet 4.9× | 29.9 / 4.4 | equal, v1 bytes identical |
| `map-set-distinct` | 37.8 (33.6–51.0, ±13%) | 118 (110–263, ±35%) | ycs 3.1× | 223 / 194 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 61.2 (25.0–134, ±99%) | 106 (90.8–141, ±11%) (carried) | ycs 1.7× | 144 / 187 | equal, v1 bytes identical |
| `map-set-same` | 38.2 (32.6–54.1, ±17%) | 103 (95.5–109, ±7%) (carried) | ycs 2.7× | 284 / 128 | equal, v1 bytes identical |
| `array-push` | 23.3 (17.7–44.6, ±17%) | 92.5 (80.8–106, ±12%) (carried) | ycs 4.0× | 161 / 136 | equal, v1 bytes identical |
| `array-push-1txn` | 59.6 (7.8–105, ±34%) | 50769 (47708–57764, ±14%) (carried) | ycs 852.3× | 59.5 / 50769 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 8.0 (7.8–9.3, ±1%) | 1641 (1561–1736, ±4%) (carried) | ycs 205.7× | 21.4 / 1619 | equal, v1 bytes identical |
| `array-insert-random` | 141 (119–254, ±51%) | 18023 (13758–33397, ±27%) (carried) | ycs 127.5× | 476 / 20544 | equal, v1 bytes identical |
| `array-delete-random` | 687 (549–1011, ±30%) | 25511 (24529–31255, ±23%) (carried) | ycs 37.2× | 925 / 25511 | equal, v1 bytes identical |
| `read-text` | 0.4 (0.2–0.4, ±38%) | 41.9 (38.6–55.1, ±8%) (carried) | ycs 110.5× | 4.3 / 47.1 | equal, v1 bytes identical |
| `read-array` | 4.8 (4.5–31.7, ±569%) | 65.3 (62.6–74.9, ±6%) (carried) | ycs 13.6× | 37.3 / 116 | equal, v1 bytes identical |
| `read-map` | 15.2 (13.8–18.8, ±7%) | 341 (334–359, ±5%) (carried) | ycs 22.5× | 114 / 666 | equal, v1 bytes identical |
| `undo-type` | 69.6 (39.5–109, ±34%) | 134 (132–166, ±2%) (carried) | ycs 1.9× | 115 / 144 | equal, v1 bytes identical |
| `undo-all` | 37.6 (35.2–66.2, ±14%) | 206 (196–242, ±3%) (carried) | ycs 5.5× | 111 / 202 | equal, v1 bytes identical |
| `redo-all` | 81.4 (70.5–120, ±21%) | 52.1 (50.1–55.1, ±3%) (carried) | YDotNet 1.6× | 247 / 55.4 | equal, v1 bytes identical |
| `observe-count` | 34.5 (29.9–62.0, ±19%) | 3069 (2954–3371, ±4%) (carried) | ycs 88.9× | 355 / 3123 | equal, v1 bytes identical |
| `observe-delta` | 57.8 (49.3–76.3, ±1%) | 4619 (4502–4813, ±3%) (carried) | ycs 80.0× | 362 / 4556 | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 0.2 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + 0.4 = 0.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 0.2 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + 0.3 = 0.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 0.2 | 8.2 | 2.1 + 0.0 = 2.1 | 1.8 + 2.6 = 4.3 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 0.1 | 1.9 | 0.5 + 0.0 = 0.5 | 0.3 + 0.7 = 0.9 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 3.9 | 2.2 | 5.2 + 0.0 = 5.2 | 0.4 + 4.0 = 4.4 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 18.8 | 12.0 | 26.3 + 2.6 = 29.0 | 3.3 + 19.2 = 22.5 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
| `trace-replay` | 42.8 | 28.5 | 2.8 + 0.0 = 2.8 | -15.8 + 2.3 = -13.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `concurrent-20` | 26.4 | 0.1 | 26.9 + 0.0 = 26.9 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `apply-state-v1/ycs` | 32.0 | 0.0 | 4.4 + 0.0 = 4.4 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 32.0 | 0.0 | 4.4 + 0.0 = 4.4 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 34.0 | 0.0 | 4.3 + 0.0 = 4.3 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 34.0 | 0.0 | 4.3 + 0.0 = 4.3 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/ycs` | 54.8 | 9.9 | 18.3 + 0.0 = 18.3 | 14.8 + 2.2 = 17.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 54.8 | 9.9 | 18.3 + 0.0 = 18.3 | 14.8 + 2.2 = 17.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 47.7 | 0.0 | 20.3 + 0.0 = 20.3 | 14.8 + 6.2 = 21.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 3.1 + 0.0 = 3.1 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 3.8 + 0.0 = 3.8 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-sv` | 0.0 | 0.1 | 4.1 + 0.0 = 4.1 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-diff` | 1.4 | 1.4 | 3.6 + 0.0 = 3.6 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `map-set-distinct` | 36.0 | 21.3 | 38.2 + 0.0 = 38.2 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 39.5 | 17.6 | 38.2 + 0.0 = 38.2 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 28.2 | 21.4 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 10.4 | 35.9 | 9.4 + 0.0 = 9.4 | 5.2 + 3.0 = 8.2 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | 10.4 | 32.9 | 9.4 + 0.0 = 9.5 | 5.2 + 4.9 = 10.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn-20k` | 2.2 | 6.4 | 1.7 + 0.0 = 1.7 | 0.7 + 0.1 = 0.9 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 17.0 | 17.9 | 18.6 + 0.0 = 18.6 | 2.6 + 15.5 = 18.1 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 32.4 | 5.5 | 19.3 + 0.9 = 20.2 | 2.8 + 12.1 = 14.9 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 4.1 + 0.0 = 4.1 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 106.8 | 4.3 + 0.0 = 4.3 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 175.5 | 33.4 + 0.0 = 33.4 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 21.3 | 2.3 | 12.0 + 0.0 = 12.0 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 30.1 | 0.0 | 12.1 + 0.0 = 12.1 | 0.2 + 7.6 = 7.7 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 35.5 | 0.0 | 20.8 + 0.0 = 20.8 | 0.3 + 15.3 = 15.6 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 36.8 | 57.2 | 1.5 + 0.0 = 1.5 | 0.5 + 1.0 = 1.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 82.6 | 130.5 | 1.5 + 0.0 = 1.5 | 0.5 + 0.2 = 0.7 | 100,018 / 100,018 | 100,033 / 100,033 |

Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.

| case | bytes | ycs | YDotNet |
| --- | ---: | --- | --- |
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 4 ms, 238 MiB | rejected: EndOfStream; unchanged; 12 ms, 240 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 3 ms, 238 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 4 ms, 238 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 4 ms, 238 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 7 ms, 239 MiB | rejected: EndOfStream; unchanged; 11 ms, 240 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 23 ms, 239 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 19 ms, 239 MiB | rejected: EndOfStream; unchanged; 13 ms, 241 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 21 ms, 244 MiB | applied; readable; 10 ms, 240 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 6 ms, 239 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (3185771) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 4 ms, 238 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 5 ms, 238 MiB | applied; readable; 11 ms, 240 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 6 ms, 238 MiB | rejected: EndOfStream; unchanged; 11 ms, 240 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 9 ms, 239 MiB | applied; readable; 11 ms, 240 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 5 ms, 238 MiB | rejected: EndOfStream; unchanged; 13 ms, 241 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 6 ms, 239 MiB | applied; readable; 62 ms, 302 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 6 ms, 238 MiB | applied; readable; 194 ms, 350 MiB |
| `conflict-2000` | 10,006 | applied; readable; 113 ms, 240 MiB | applied; readable; 261 ms, 241 MiB |
