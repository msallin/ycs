Run 2026-11-17 21:31 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs E0F2E5BC71B79C7B.

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16376028 kB RAM, Ubuntu 24.04.5 LTS, kernel 6.8.0.124, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 1.79 2.41 2.14 1/517 77092.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 27.6 (27.1–28.6, ±2%) | 3582 (3546–3713, ±2%) | ycs 129.9× | 316 / 3634 | equal, v1 bytes identical |
| `text-append-utf8` | 27.6 (27.1–28.1, ±1%) | 1851 (1818–1899, ±2%) | ycs 67.1× | 320 / 1927 | equal, v1 bytes identical |
| `text-append-1txn` | 15.3 (15.1–15.7, ±2%) | 26459 (25801–27026, ±2%) | ycs 1729.5× | 171 / 26459 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 3.9 (3.8–5.0, ±1%) | 1414 (1394–1471, ±2%) | ycs 367.1× | 72.0 / 1458 | equal, v1 bytes identical |
| `text-append-1txn-observed` | 227 (220–253, ±1%) | 26474 (26112–27579, ±4%) | ycs 116.5× | 800 / 26474 | equal, v1 bytes identical |
| `text-append-1txn-observed-200k` | 451 (445–459, ±1%) | 124528 (124333–133461, ±7%) | ycs 276.1× | 1036 / 124528 | equal, v1 bytes identical |
| `text-append-1txn-observed-400k` | 952 (941–979, ±1%) | 520778 (520751–552002, ±6%) | ycs 546.8× | 1569 / 520778 | equal, v1 bytes identical |
| `text-append-1txn-observed-25k` | 54.1 (53.7–56.1, ±2%) | 1441 (1417–1487, ±2%) | ycs 26.7× | 474 / 1453 | equal, v1 bytes identical |
| `txn-empty` | 7.6 (7.5–8.0, ±1%) | 13.0 (12.2–14.6, ±10%) | ycs 1.7× | 56.2 / 18.8 | equal, v1 bytes identical |
| `text-random` | 37.2 (36.3–38.9, ±3%) | 1778 (1759–1819, ±1%) | ycs 47.8× | 438 / 1808 | equal, v1 bytes identical |
| `text-random-100k` | 1324 (1256–1358, ±5%) | 168610 (163551–183516, ±9%) | ycs 127.4× | 1324 / 168610 | equal, v1 bytes identical |
| `trace-replay` | 350 (341–374, ±3%) | 12139 (11885–12530, ±2%) | ycs 34.7× | 1255 / 11830 | equal, v1 bytes identical |
| `concurrent-20` | 91.3 (89.4–95.0, ±1%) | 79.4 (78.5–81.4, ±1%) | YDotNet 1.2× | 582 / 100 | equal, v1 bytes differ |
| `many-clients-200` | 1.9 (1.9–2.1, ±4%) | 2.3 (2.2–2.6, ±2%) | ycs 1.2× | 18.4 / 3.1 | equal, v1 bytes differ |
| `many-clients-1000` | 7.5 (7.3–10.2, ±2%) | 24.5 (24.0–37.6, ±2%) | ycs 3.3× | 52.1 / 26.1 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 75.6 (72.4–78.3, ±3%) | 48.8 (47.9–54.9, ±3%) | YDotNet 1.6× | 471 / 58.4 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 76.3 (73.4–78.1, ±2%) | 49.0 (48.4–53.0, ±1%) | YDotNet 1.6× | 445 / 58.0 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 77.1 (75.1–82.4, ±1%) | 51.2 (49.6–52.9, ±3%) | YDotNet 1.5× | 530 / 64.4 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 76.4 (75.1–79.0, ±2%) | 50.5 (49.5–52.4, ±1%) | YDotNet 1.5× | 442 / 61.2 | equal, v1 bytes identical |
| `apply-state-x100/ycs` | 839 (781–977, ±0%) | 752 (724–833, ±2%) | YDotNet 1.1× | 2014 / 946 | equal, v1 bytes identical |
| `apply-updates/ycs` | 270 (267–290, ±1%) | 370 (362–382, ±1%) | ycs 1.4× | 996 / 387 | equal, v1 bytes identical |
| `apply-updates/yrs` | 279 (271–295, ±5%) | 369 (363–424, ±2%) | ycs 1.3× | 1011 / 383 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 172 (164–189, ±2%) | 1770 (1756–1810, ±2%) | ycs 10.3× | 789 / 1826 | equal, v1 bytes identical |
| `encode-v1` | 8.2 (8.0–8.8, ±1%) | 8.9 (8.7–9.0, ±1%) | ycs 1.1× | 74.3 / 10.4 | equal, v1 bytes identical |
| `encode-v2` | 9.9 (9.7–10.4, ±3%) | 10.0 (9.7–10.3, ±1%) | ycs 1.0× | 76.7 / 11.3 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.1, ±4%) | 0.3 (0.2–0.3, ±3%) | ycs 3.0× | 3.6 / 1.1 | equal, v1 bytes identical |
| `encode-diff` | 4.9 (4.7–5.0, ±3%) | 4.6 (4.5–5.0, ±5%) | YDotNet 1.0× | 52.0 / 5.8 | equal, v1 bytes identical |
| `map-set-distinct` | 40.1 (36.0–50.4, ±4%) | 144 (136–149, ±7%) | ycs 3.6× | 420 / 215 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 29.2 (26.7–44.8, ±8%) | 115 (109–131, ±2%) | ycs 3.9× | 273 / 202 | equal, v1 bytes identical |
| `map-set-same` | 65.1 (63.9–68.2, ±1%) | 132 (131–151, ±8%) | ycs 2.0× | 626 / 187 | equal, v1 bytes identical |
| `array-push` | 25.4 (24.4–26.9, ±1%) | 115 (112–121, ±4%) | ycs 4.5× | 326 / 196 | equal, v1 bytes identical |
| `array-push-1txn` | 12.6 (11.6–14.3, ±6%) | 70664 (70144–71260, ±1%) | ycs 5605.3× | 121 / 70664 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 2.5 (2.4–2.6, ±2%) | 2203 (2138–2409, ±2%) | ycs 866.1× | 48.6 / 2238 | equal, v1 bytes identical |
| `array-insert-random` | 191 (175–215, ±12%) | 21050 (19266–29483, ±5%) | ycs 110.1× | 762 / 20554 | equal, v1 bytes identical |
| `array-delete-random` | 180 (167–211, ±6%) | 25925 (24798–28681, ±11%) | ycs 144.0× | 699 / 25925 | equal, v1 bytes identical |
| `read-text` | 0.2 (0.2–0.3, ±6%) | 46.3 (45.3–47.9, ±2%) | ycs 194.0× | 5.8 / 52.8 | equal, v1 bytes identical |
| `read-array` | 20.7 (20.4–22.1, ±2%) | 98.2 (97.0–103, ±1%) | ycs 4.8× | 40.2 / 182 | equal, v1 bytes identical |
| `read-map` | 40.1 (39.5–40.8, ±1%) | 395 (382–405, ±2%) | ycs 9.8× | 212 / 777 | equal, v1 bytes identical |
| `undo-type` | 30.3 (29.1–34.3, ±6%) | 161 (160–206, ±1%) | ycs 5.3× | 181 / 182 | equal, v1 bytes identical |
| `undo-all` | 43.5 (41.6–45.0, ±2%) | 344 (318–377, ±4%) | ycs 7.9× | 191 / 338 | equal, v1 bytes identical |
| `redo-all` | 47.6 (46.4–49.0, ±0%) | 80.1 (77.8–85.0, ±2%) | ycs 1.7× | 194 / 85.6 | equal, v1 bytes identical |
| `observe-count` | 54.9 (54.0–60.1, ±4%) | 3607 (3558–3752, ±2%) | ycs 65.8× | 583 / 3660 | equal, v1 bytes identical |
| `observe-delta` | 92.3 (90.8–101, ±8%) | 5417 (5376–5619, ±1%) | ycs 58.7× | 765 / 5527 | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 0.6 | 11.4 | 1.5 + 0.0 = 1.5 | 0.2 + 0.4 = 0.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 0.6 | 11.4 | 1.5 + 0.0 = 1.5 | 0.2 + 0.3 = 0.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 0.5 | 8.2 | 2.1 + 0.0 = 2.1 | 1.8 + 2.3 = 4.1 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 0.1 | 1.9 | 0.5 + 0.0 = 0.5 | 0.3 + 0.7 = 0.9 | 25,018 / 25,018 | 25,033 / 25,033 |
| `text-append-1txn-observed` | 8.3 | 8.2 | 2.4 + 0.0 = 2.4 | 1.3 + 2.3 = 3.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-observed-200k` | 16.5 | 15.9 | 4.7 + -4.2 = 0.5 | 2.5 + 4.5 = 6.9 | 200,018 / 200,018 | 200,033 / 200,033 |
| `text-append-1txn-observed-400k` | 32.9 | 31.1 | 9.2 + 0.9 = 10.1 | 4.9 + 8.7 = 13.6 | 400,018 / 400,018 | 400,033 / 400,033 |
| `text-append-1txn-observed-25k` | 2.1 | 1.9 | 0.7 + 0.0 = 0.7 | 0.3 + 0.7 = 1.0 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 1.7 | 2.2 | 3.2 + 0.0 = 3.2 | 0.4 + 4.0 = 4.4 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 7.9 | 12.0 | 16.3 + 3.4 = 19.7 | 3.3 + 19.0 = 22.3 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
| `trace-replay` | 24.9 | 28.5 | 1.9 + 0.0 = 1.9 | 0.2 + 2.4 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `concurrent-20` | 14.2 | 0.1 | 14.2 + 0.0 = 14.3 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `many-clients-200` | 0.4 | 0.0 | 0.6 + 0.0 = 0.6 | 0.1 + 0.5 = 0.6 | 34,878 / 34,878 | 20,223 / 20,223 |
| `many-clients-1000` | 1.3 | 0.0 | 2.2 + 0.0 = 2.2 | 0.3 + 1.9 = 2.2 | 149,755 / 149,755 | 93,984 / 93,984 |
| `apply-state-v1/ycs` | 38.1 | 0.0 | 2.7 + 0.0 = 2.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 38.1 | 0.0 | 2.7 + 0.0 = 2.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 40.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 40.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-x100/ycs` | 479.7 | 0.0 | 253.0 + 2.7 = 255.7 | 49.9 + 229.3 = 279.2 | 31,264,816 / 31,264,816 | 15,989,045 / 15,989,045 |
| `apply-updates/ycs` | 11.9 | 9.9 | 17.4 + 0.0 = 17.4 | 14.8 + 1.0 = 15.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 11.9 | 9.9 | 17.4 + 0.0 = 17.4 | 14.8 + 1.0 = 15.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 18.2 | 0.0 | 27.8 + 0.0 = 27.8 | 14.8 + 6.2 = 21.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 1.9 + 0.0 = 1.9 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 2.1 + 0.0 = 2.1 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-sv` | 0.0 | 0.1 | 2.4 + 0.0 = 2.4 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-diff` | 1.4 | 1.4 | 1.9 + 0.0 = 1.9 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `map-set-distinct` | 22.6 | 21.3 | 24.9 + 0.0 = 24.9 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 25.3 | 17.5 | 24.9 + 0.0 = 24.9 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 6.8 | 21.3 | 0.0 + 0.0 = 0.1 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 4.3 | 35.8 | 10.9 + 0.0 = 10.9 | 5.2 + 3.0 = 8.2 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | 4.3 | 32.7 | 10.4 + 0.0 = 10.4 | 5.2 + 3.7 = 8.8 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn-20k` | 0.9 | 6.4 | 1.9 + 0.0 = 1.9 | 0.7 + 1.0 = 1.8 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 5.4 | 17.9 | 10.4 + 0.0 = 10.4 | 2.6 + 13.4 = 16.0 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 4.2 | 5.4 | 11.6 + 0.0 = 11.6 | 2.8 + 11.8 = 14.6 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 2.3 + 0.0 = 2.4 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 106.8 | 4.8 + 0.0 = 4.8 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 175.5 | 20.1 + 0.0 = 20.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 16.6 | 2.2 | 11.6 + 0.0 = 11.6 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 25.4 | 0.0 | 12.1 + 0.0 = 12.2 | 0.2 + 6.8 = 6.9 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 31.2 | 0.0 | 17.4 + 0.0 = 17.4 | 0.3 + 14.5 = 14.8 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 17.8 | 57.2 | 1.7 + 0.0 = 1.7 | 0.5 + 1.0 = 1.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 74.3 | 130.5 | 1.7 + 0.0 = 1.7 | 0.5 + 0.2 = 0.7 | 100,018 / 100,018 | 100,033 / 100,033 |

Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.

| case | bytes | ycs | YDotNet |
| --- | ---: | --- | --- |
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 5 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 5 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 10 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 32 ms, 100 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 27 ms, 99 MiB | rejected: EndOfStream; unchanged; 15 ms, 101 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 28 ms, 104 MiB | applied; readable; 12 ms, 101 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (94510) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | applied; readable; 12 ms, 100 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 13 ms, 99 MiB | applied; readable; 12 ms, 101 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | applied; readable; 80 ms, 163 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | applied; readable; 247 ms, 211 MiB |
| `conflict-2000` | 10,006 | applied; readable; 191 ms, 100 MiB | applied; readable; 336 ms, 101 MiB |
