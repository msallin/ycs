Run 2026-11-19 16:07 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs E0F2E5BC71B79C7B. Children: JIT (the driver's own build).

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16376028 kB RAM, Ubuntu 24.04.5 LTS, kernel 6.8.0.124, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 1.26 1.47 1.48 1/398 867193.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 27.8 (27.4–29.7, ±4%) | 3561 (3523–3652, ±0%) | ycs 128.2× | 335 / 3615 | equal, v1 bytes identical |
| `text-append-utf8` | 27.3 (26.9–30.0, ±6%) | 1821 (1793–1941, ±3%) | ycs 66.8× | 348 / 1910 | equal, v1 bytes identical |
| `text-append-1txn` | 16.0 (15.2–19.1, ±17%) | 26275 (26248–29171, ±11%) | ycs 1643.3× | 174 / 26275 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 4.5 (4.5–5.1, ±2%) | 1422 (1370–1479, ±2%) | ycs 313.2× | 74.1 / 1437 | equal, v1 bytes identical |
| `text-append-1txn-observed` | 226 (223–255, ±2%) | 26758 (26242–29794, ±11%) | ycs 118.2× | 795 / 26758 | equal, v1 bytes identical |
| `text-append-1txn-observed-200k` | 463 (452–514, ±2%) | 124566 (122796–141569, ±14%) | ycs 268.8× | 1064 / 124566 | equal, v1 bytes identical |
| `text-append-1txn-observed-400k` | 962 (927–1032, ±6%) | 519916 (516706–582579, ±12%) | ycs 540.6× | 1581 / 519916 | equal, v1 bytes identical |
| `text-append-1txn-observed-25k` | 55.5 (53.3–59.1, ±5%) | 1428 (1412–1450, ±1%) | ycs 25.7× | 480 / 1454 | equal, v1 bytes identical |
| `txn-empty` | 8.1 (7.6–10.0, ±20%) | 12.1 (11.9–12.5, ±1%) | ycs 1.5× | 62.4 / 18.0 | equal, v1 bytes identical |
| `text-random` | 37.2 (36.4–45.3, ±2%) | 1766 (1749–1821, ±1%) | ycs 47.5× | 419 / 1817 | equal, v1 bytes identical |
| `text-random-100k` | 1387 (1351–1473, ±6%) | 181384 (164759–196821, ±9%) | ycs 130.7× | 1387 / 181384 | equal, v1 bytes identical |
| `trace-replay` | 360 (355–390, ±1%) | 12122 (11807–12273, ±1%) | ycs 33.7× | 1279 / 11759 | equal, v1 bytes identical |
| `concurrent-20` | 67.0 (65.5–70.4, ±1%) | 79.9 (77.6–83.3, ±3%) | ycs 1.2× | 410 / 99.9 | equal, v1 bytes differ |
| `many-clients-200` | 1.5 (1.4–1.8, ±8%) | 2.3 (2.2–2.4, ±2%) | ycs 1.5× | 14.9 / 3.0 | equal, v1 bytes differ |
| `many-clients-1000` | 5.7 (5.5–6.0, ±3%) | 24.4 (24.1–26.2, ±1%) | ycs 4.3× | 41.8 / 26.2 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 49.1 (47.0–58.1, ±4%) | 48.4 (48.2–49.3, ±1%) | YDotNet 1.0× | 476 / 58.6 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 48.3 (46.8–49.4, ±3%) | 48.9 (48.3–52.0, ±1%) | ycs 1.0× | 458 / 60.1 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 48.1 (47.4–48.9, ±1%) | 50.2 (49.5–54.2, ±2%) | ycs 1.0× | 459 / 61.0 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 48.7 (48.0–50.2, ±1%) | 50.1 (49.4–51.0, ±1%) | ycs 1.0× | 454 / 61.5 | equal, v1 bytes identical |
| `apply-state-x100/ycs` | 613 (555–688, ±2%) | 754 (732–794, ±2%) | ycs 1.2× | 1518 / 914 | equal, v1 bytes identical |
| `apply-updates/ycs` | 235 (232–246, ±1%) | 368 (362–381, ±0%) | ycs 1.6× | 837 / 384 | equal, v1 bytes identical |
| `apply-updates/yrs` | 240 (234–298, ±3%) | 370 (361–396, ±2%) | ycs 1.5× | 869 / 376 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 137 (132–151, ±3%) | 1794 (1755–1841, ±1%) | ycs 13.1× | 728 / 1851 | equal, v1 bytes identical |
| `encode-v1` | 8.2 (8.0–9.7, ±3%) | 8.9 (8.5–9.5, ±5%) | ycs 1.1× | 77.1 / 9.8 | equal, v1 bytes identical |
| `encode-v2` | 10.2 (10.1–11.1, ±5%) | 10.2 (9.9–10.8, ±3%) | YDotNet 1.0× | 85.5 / 11.3 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.1, ±4%) | 0.2 (0.2–0.3, ±3%) | ycs 2.5× | 4.9 / 1.1 | equal, v1 bytes identical |
| `encode-diff` | 4.9 (4.8–5.1, ±1%) | 4.6 (4.5–4.9, ±2%) | YDotNet 1.1× | 53.9 / 5.7 | equal, v1 bytes identical |
| `map-set-distinct` | 41.0 (37.4–56.7, ±2%) | 144 (138–150, ±3%) | ycs 3.5× | 437 / 223 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 31.0 (25.9–55.8, ±9%) | 126 (113–134, ±9%) | ycs 4.1× | 271 / 199 | equal, v1 bytes identical |
| `map-set-same` | 67.0 (65.8–77.3, ±7%) | 139 (131–148, ±5%) | ycs 2.1× | 636 / 188 | equal, v1 bytes identical |
| `array-push` | 25.5 (24.9–33.5, ±17%) | 115 (113–121, ±2%) | ycs 4.5× | 341 / 198 | equal, v1 bytes identical |
| `array-push-1txn` | 12.4 (11.7–13.1, ±3%) | 69833 (69566–70837, ±1%) | ycs 5630.4× | 130 / 69833 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 2.5 (2.3–2.6, ±7%) | 2200 (2168–2526, ±1%) | ycs 895.0× | 51.0 / 2249 | equal, v1 bytes identical |
| `array-insert-random` | 184 (175–198, ±3%) | 22230 (19335–24140, ±6%) | ycs 120.8× | 783 / 23137 | equal, v1 bytes identical |
| `array-delete-random` | 183 (166–209, ±11%) | 25429 (24564–26553, ±4%) | ycs 139.1× | 708 / 25429 | equal, v1 bytes identical |
| `read-text` | 0.3 (0.3–0.4, ±4%) | 48.2 (46.0–53.3, ±6%) | ycs 142.8× | 8.3 / 54.5 | equal, v1 bytes identical |
| `read-array` | 4.0 (3.9–4.1, ±2%) | 102 (99.3–108, ±1%) | ycs 25.7× | 9.4 / 174 | equal, v1 bytes identical |
| `read-map` | 26.2 (25.2–30.2, ±11%) | 407 (393–444, ±4%) | ycs 15.5× | 193 / 788 | equal, v1 bytes identical |
| `undo-type` | 31.0 (29.6–35.7, ±2%) | 161 (160–163, ±1%) | ycs 5.2× | 191 / 182 | equal, v1 bytes identical |
| `undo-all` | 43.3 (37.9–46.3, ±4%) | 366 (319–385, ±5%) | ycs 8.5× | 196 / 358 | equal, v1 bytes identical |
| `redo-all` | 47.8 (45.9–51.4, ±7%) | 80.5 (78.9–107, ±5%) | ycs 1.7× | 227 / 86.9 | equal, v1 bytes identical |
| `observe-count` | 56.5 (55.7–65.1, ±3%) | 3592 (3560–3719, ±2%) | ycs 63.5× | 588 / 3654 | equal, v1 bytes identical |
| `observe-delta` | 89.9 (88.9–96.1, ±6%) | 5437 (5359–5559, ±1%) | ycs 60.5× | 765 / 5498 | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 0.6 | 11.4 | 0.6 + 0.0 = 0.6 | 0.2 + 0.4 = 0.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 0.6 | 11.4 | 0.6 + 0.0 = 0.6 | 0.2 + 0.3 = 0.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 0.5 | 8.2 | 1.4 + 0.0 = 1.4 | 1.8 + 2.3 = 4.1 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 0.1 | 1.9 | 0.3 + 0.0 = 0.4 | 0.3 + 0.7 = 0.9 | 25,018 / 25,018 | 25,033 / 25,033 |
| `text-append-1txn-observed` | 8.3 | 8.2 | 1.9 + 0.0 = 1.9 | 1.3 + 2.3 = 3.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-observed-200k` | 16.5 | 15.9 | 3.7 + 0.9 = 4.5 | 2.5 + 4.5 = 6.9 | 200,018 / 200,018 | 200,033 / 200,033 |
| `text-append-1txn-observed-400k` | 32.9 | 31.1 | 7.2 + 0.9 = 8.1 | 4.9 + 8.7 = 13.6 | 400,018 / 400,018 | 400,033 / 400,033 |
| `text-append-1txn-observed-25k` | 2.1 | 1.9 | 0.6 + 0.0 = 0.6 | 0.3 + 0.7 = 1.0 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 1.7 | 2.2 | 3.1 + 0.0 = 3.1 | 0.4 + 4.0 = 4.4 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 7.9 | 12.0 | 16.1 + 4.7 = 20.7 | 3.3 + 19.0 = 22.3 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
| `trace-replay` | 24.9 | 28.5 | 1.6 + 0.0 = 1.6 | 0.2 + 2.4 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `concurrent-20` | 14.1 | 0.1 | 14.1 + 0.0 = 14.1 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `many-clients-200` | 0.4 | 0.0 | 0.5 + 0.0 = 0.5 | 0.1 + 0.5 = 0.6 | 34,878 / 34,878 | 20,223 / 20,223 |
| `many-clients-1000` | 1.3 | 0.0 | 2.0 + 0.0 = 2.0 | 0.3 + 1.9 = 2.2 | 149,755 / 149,755 | 93,984 / 93,984 |
| `apply-state-v1/ycs` | 16.2 | 0.0 | 2.8 + 0.0 = 2.8 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 16.2 | 0.0 | 2.8 + 0.0 = 2.8 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 16.2 | 0.0 | 2.7 + 0.0 = 2.7 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 16.2 | 0.0 | 2.7 + 0.0 = 2.7 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-x100/ycs` | 451.7 | 0.0 | 241.0 + 2.7 = 243.7 | 49.9 + 230.3 = 280.1 | 31,264,816 / 31,264,816 | 15,989,045 / 15,989,045 |
| `apply-updates/ycs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.8 + 1.0 = 15.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.8 + 1.0 = 15.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 18.2 | 0.0 | 27.5 + 0.0 = 27.6 | 14.8 + 6.2 = 21.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 1.5 + 0.0 = 1.6 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 2.2 + 0.0 = 2.2 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-sv` | 0.0 | 0.1 | 2.5 + 0.0 = 2.6 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-diff` | 1.4 | 1.4 | 2.0 + 0.0 = 2.1 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `map-set-distinct` | 22.6 | 21.3 | 24.9 + 0.0 = 24.9 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 25.3 | 17.5 | 24.9 + 0.0 = 24.9 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 6.8 | 21.3 | 0.0 + 0.0 = 0.1 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 4.3 | 35.8 | 10.4 + 0.0 = 10.4 | 5.2 + 3.0 = 8.2 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | 4.3 | 32.7 | 10.4 + 0.0 = 10.4 | 5.2 + 3.7 = 8.8 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn-20k` | 0.9 | 6.4 | 1.9 + 0.0 = 1.9 | 0.7 + 1.0 = 1.8 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 5.4 | 17.9 | 10.4 + 0.0 = 10.4 | 2.6 + 13.4 = 15.9 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 4.2 | 5.4 | 10.8 + 0.0 = 10.9 | 2.8 + 11.8 = 14.6 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 3.0 + 0.0 = 3.0 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 106.8 | 4.0 + 0.0 = 4.1 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 175.5 | 20.1 + 0.0 = 20.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 16.6 | 2.2 | 11.5 + 0.0 = 11.5 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 25.4 | 0.0 | 12.1 + 0.0 = 12.1 | 0.2 + 6.8 = 7.0 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 31.2 | 0.0 | 17.4 + 0.0 = 17.4 | 0.3 + 14.5 = 14.8 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 17.8 | 57.2 | 1.2 + 0.0 = 1.2 | 0.5 + 1.0 = 1.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 74.3 | 130.5 | 1.2 + 0.0 = 1.2 | 0.5 + 0.2 = 0.7 | 100,018 / 100,018 | 100,033 / 100,033 |

Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.

| case | bytes | ycs | YDotNet |
| --- | ---: | --- | --- |
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 5 ms, 99 MiB | rejected: EndOfStream; unchanged; 15 ms, 101 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 6 ms, 99 MiB | rejected: EndOfStream; unchanged; 10 ms, 100 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 10 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 32 ms, 100 MiB | rejected: EndOfStream; unchanged; 10 ms, 100 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 26 ms, 99 MiB | rejected: EndOfStream; unchanged; 15 ms, 101 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 29 ms, 104 MiB | applied; readable; 13 ms, 101 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 11 ms, 99 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (877234) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | rejected: EndOfStream; unchanged; 10 ms, 100 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | applied; readable; 13 ms, 101 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 13 ms, 99 MiB | applied; readable; 13 ms, 100 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | applied; readable; 78 ms, 163 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | applied; readable; 251 ms, 211 MiB |
| `conflict-2000` | 10,006 | applied; readable; 184 ms, 100 MiB | applied; readable; 332 ms, 101 MiB |
