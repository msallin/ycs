Run 2026-11-18 22:00 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs E0F2E5BC71B79C7B.

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16376028 kB RAM, Ubuntu 24.04.5 LTS, kernel 6.8.0.124, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 1.46 1.18 1.18 1/400 702429.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 27.8 (27.4–28.3, ±1%) | 3561 (3509–3634, ±0%) | ycs 128.0× | 335 / 3572 | equal, v1 bytes identical |
| `text-append-utf8` | 28.1 (27.7–34.2, ±0%) | 1812 (1797–1913, ±1%) | ycs 64.4× | 332 / 1886 | equal, v1 bytes identical |
| `text-append-1txn` | 18.1 (15.1–19.1, ±16%) | 25563 (25331–26047, ±2%) | ycs 1416.1× | 171 / 25563 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 4.6 (4.5–7.4, ±4%) | 1422 (1399–1447, ±0%) | ycs 306.4× | 74.9 / 1426 | equal, v1 bytes identical |
| `text-append-1txn-observed` | 224 (220–253, ±14%) | 25522 (25394–25853, ±1%) | ycs 114.0× | 794 / 25522 | equal, v1 bytes identical |
| `text-append-1txn-observed-200k` | 457 (450–471, ±2%) | 119278 (118997–121627, ±2%) | ycs 260.7× | 1035 / 119278 | equal, v1 bytes identical |
| `text-append-1txn-observed-400k` | 967 (939–1003, ±1%) | 503642 (502665–504314, ±0%) | ycs 520.8× | 1592 / 503642 | equal, v1 bytes identical |
| `text-append-1txn-observed-25k` | 55.2 (54.1–59.6, ±7%) | 1424 (1396–1479, ±2%) | ycs 25.8× | 472 / 1418 | equal, v1 bytes identical |
| `txn-empty` | 7.8 (7.6–8.0, ±2%) | 12.1 (12.0–14.3, ±18%) | ycs 1.5× | 60.0 / 18.1 | equal, v1 bytes identical |
| `text-random` | 38.6 (36.3–49.7, ±7%) | 1741 (1724–1778, ±1%) | ycs 45.1× | 418 / 1810 | equal, v1 bytes identical |
| `text-random-100k` | 1253 (1243–1257, ±1%) | 150269 (149046–152486, ±1%) | ycs 120.0× | 1253 / 150269 | equal, v1 bytes identical |
| `trace-replay` | 356 (353–396, ±1%) | 11957 (11671–12478, ±1%) | ycs 33.6× | 1274 / 11496 | equal, v1 bytes identical |
| `concurrent-20` | 92.1 (88.0–99.5, ±3%) | 79.4 (77.2–85.0, ±3%) | YDotNet 1.2× | 484 / 100 | equal, v1 bytes differ |
| `many-clients-200` | 1.9 (1.9–2.8, ±41%) | 2.3 (2.2–2.5, ±3%) | ycs 1.2× | 17.7 / 3.0 | equal, v1 bytes differ |
| `many-clients-1000` | 7.4 (7.2–8.2, ±7%) | 24.5 (24.0–25.0, ±1%) | ycs 3.3× | 53.6 / 27.2 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 72.0 (70.0–75.6, ±1%) | 48.2 (47.9–49.5, ±0%) | YDotNet 1.5× | 455 / 57.3 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 72.4 (70.1–75.8, ±2%) | 48.6 (47.7–56.8, ±1%) | YDotNet 1.5× | 436 / 59.8 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 73.5 (70.3–80.6, ±3%) | 50.1 (49.2–54.6, ±3%) | YDotNet 1.5× | 462 / 58.8 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 72.9 (71.7–75.4, ±1%) | 50.4 (49.7–51.3, ±1%) | YDotNet 1.4× | 460 / 58.5 | equal, v1 bytes identical |
| `apply-state-x100/ycs` | 856 (786–972, ±1%) | 721 (704–735, ±1%) | YDotNet 1.2× | 1946 / 927 | equal, v1 bytes identical |
| `apply-updates/ycs` | 269 (261–281, ±2%) | 366 (359–402, ±7%) | ycs 1.4× | 893 / 380 | equal, v1 bytes identical |
| `apply-updates/yrs` | 272 (269–285, ±1%) | 366 (361–395, ±1%) | ycs 1.3× | 880 / 380 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 159 (154–185, ±3%) | 1774 (1734–1818, ±2%) | ycs 11.2× | 683 / 1831 | equal, v1 bytes identical |
| `encode-v1` | 8.3 (8.1–8.7, ±1%) | 8.7 (8.5–9.0, ±2%) | ycs 1.1× | 78.4 / 9.8 | equal, v1 bytes identical |
| `encode-v2` | 10.4 (10.0–10.9, ±3%) | 10.1 (9.7–12.0, ±2%) | YDotNet 1.0× | 77.3 / 11.4 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.1, ±10%) | 0.2 (0.2–0.4, ±2%) | ycs 2.3× | 4.5 / 1.1 | equal, v1 bytes identical |
| `encode-diff` | 4.8 (4.7–5.0, ±1%) | 4.6 (4.5–6.9, ±0%) | YDotNet 1.0× | 53.2 / 6.2 | equal, v1 bytes identical |
| `map-set-distinct` | 38.6 (35.7–52.0, ±3%) | 142 (137–158, ±4%) | ycs 3.7× | 433 / 217 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 29.0 (25.3–53.1, ±11%) | 116 (106–136, ±5%) | ycs 4.0× | 260 / 193 | equal, v1 bytes identical |
| `map-set-same` | 66.3 (62.3–69.8, ±6%) | 139 (134–163, ±8%) | ycs 2.1× | 634 / 194 | equal, v1 bytes identical |
| `array-push` | 26.1 (24.5–27.0, ±4%) | 114 (112–118, ±3%) | ycs 4.4× | 340 / 194 | equal, v1 bytes identical |
| `array-push-1txn` | 13.1 (11.6–17.3, ±8%) | 67273 (67122–68245, ±1%) | ycs 5145.6× | 120 / 67273 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 2.7 (2.3–4.1, ±49%) | 2192 (2113–2237, ±1%) | ycs 814.8× | 50.5 / 2241 | equal, v1 bytes identical |
| `array-insert-random` | 180 (175–195, ±1%) | 21125 (17951–23657, ±13%) | ycs 117.6× | 753 / 20662 | equal, v1 bytes identical |
| `array-delete-random` | 182 (172–203, ±6%) | 25350 (22454–26444, ±11%) | ycs 139.2× | 718 / 25350 | equal, v1 bytes identical |
| `read-text` | 0.3 (0.3–0.4, ±3%) | 48.0 (46.1–50.0, ±3%) | ycs 141.5× | 8.2 / 53.4 | equal, v1 bytes identical |
| `read-array` | 3.9 (3.8–4.1, ±5%) | 96.9 (94.9–99.8, ±2%) | ycs 24.7× | 9.3 / 174 | equal, v1 bytes identical |
| `read-map` | 25.8 (25.0–27.1, ±3%) | 396 (386–423, ±1%) | ycs 15.4× | 209 / 794 | equal, v1 bytes identical |
| `undo-type` | 31.0 (28.7–35.2, ±5%) | 160 (160–161, ±0%) | ycs 5.2× | 186 / 182 | equal, v1 bytes identical |
| `undo-all` | 42.1 (39.0–48.0, ±5%) | 340 (319–375, ±4%) | ycs 8.1× | 189 / 348 | equal, v1 bytes identical |
| `redo-all` | 46.6 (43.2–56.6, ±5%) | 83.5 (78.6–85.2, ±6%) | ycs 1.8× | 216 / 89.0 | equal, v1 bytes identical |
| `observe-count` | 56.5 (53.6–59.3, ±5%) | 3590 (3561–3713, ±1%) | ycs 63.6× | 586 / 3631 | equal, v1 bytes identical |
| `observe-delta` | 92.7 (89.3–98.8, ±2%) | 5368 (5325–5443, ±0%) | ycs 57.9× | 760 / 5480 | equal, v1 bytes identical |

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
| `concurrent-20` | 14.2 | 0.1 | 14.2 + 0.0 = 14.2 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `many-clients-200` | 0.4 | 0.0 | 0.5 + 0.0 = 0.5 | 0.1 + 0.5 = 0.6 | 34,878 / 34,878 | 20,223 / 20,223 |
| `many-clients-1000` | 1.3 | 0.0 | 2.0 + 0.0 = 2.1 | 0.3 + 1.9 = 2.2 | 149,755 / 149,755 | 93,984 / 93,984 |
| `apply-state-v1/ycs` | 38.1 | 0.0 | 2.9 + 0.0 = 2.9 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 38.1 | 0.0 | 2.9 + 0.0 = 2.9 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 38.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 38.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-x100/ycs` | 479.7 | 0.0 | 253.0 + 2.7 = 255.7 | 49.9 + 229.3 = 279.2 | 31,264,816 / 31,264,816 | 15,989,045 / 15,989,045 |
| `apply-updates/ycs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.8 + 1.0 = 15.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.8 + 1.0 = 15.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 18.2 | 0.0 | 27.5 + 0.0 = 27.6 | 14.8 + 6.2 = 21.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 1.6 + 0.0 = 1.6 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 2.3 + 0.0 = 2.3 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-sv` | 0.0 | 0.1 | 2.6 + 0.0 = 2.7 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-diff` | 1.4 | 1.4 | 2.1 + 0.0 = 2.1 | 0.2 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `map-set-distinct` | 22.6 | 21.3 | 24.9 + 0.0 = 24.9 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 25.3 | 17.5 | 24.9 + 0.0 = 24.9 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 6.8 | 21.3 | 0.0 + 0.0 = 0.1 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 4.3 | 35.8 | 10.4 + 0.0 = 10.4 | 5.2 + 3.0 = 8.2 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | 4.3 | 32.7 | 10.4 + 0.0 = 10.4 | 5.2 + 3.7 = 8.8 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn-20k` | 0.9 | 6.4 | 1.9 + 0.0 = 1.9 | 0.7 + 1.0 = 1.8 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 5.4 | 17.9 | 10.4 + 0.0 = 10.4 | 2.6 + 13.4 = 15.9 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 4.2 | 5.4 | 10.8 + 0.0 = 10.9 | 2.8 + 11.8 = 14.6 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 3.1 + 0.0 = 3.1 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 106.8 | 4.0 + 0.0 = 4.1 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 175.5 | 20.1 + 0.0 = 20.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 16.6 | 2.2 | 11.5 + 0.0 = 11.5 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 25.4 | 0.0 | 12.1 + 0.0 = 12.1 | 0.2 + 7.6 = 7.7 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 31.2 | 0.0 | 17.4 + 0.0 = 17.4 | 0.3 + 14.5 = 14.8 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 17.8 | 57.2 | 1.2 + 0.0 = 1.2 | 0.5 + 1.0 = 1.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 74.3 | 130.5 | 1.2 + 0.0 = 1.2 | 0.5 + 0.2 = 0.7 | 100,018 / 100,018 | 100,033 / 100,033 |

Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.

| case | bytes | ycs | YDotNet |
| --- | ---: | --- | --- |
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 6 ms, 99 MiB | rejected: EndOfStream; unchanged; 15 ms, 101 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 5 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 6 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 6 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 10 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 35 ms, 100 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 27 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 29 ms, 104 MiB | applied; readable; 12 ms, 101 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (711980) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | applied; readable; 12 ms, 101 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 12 ms, 99 MiB | applied; readable; 13 ms, 100 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | applied; readable; 77 ms, 162 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | applied; readable; 240 ms, 211 MiB |
| `conflict-2000` | 10,006 | applied; readable; 194 ms, 100 MiB | applied; readable; 338 ms, 101 MiB |
