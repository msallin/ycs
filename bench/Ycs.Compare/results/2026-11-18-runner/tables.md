Run 2026-11-18 13:00 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs E0F2E5BC71B79C7B.

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16376028 kB RAM, Ubuntu 24.04.5 LTS, kernel 6.8.0.124, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 0.92 0.34 0.12 1/441 562775.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 27.4 (26.8–30.4, ±6%) | 3574 (3542–3632, ±1%) | ycs 130.7× | 304 / 3619 | equal, v1 bytes identical |
| `text-append-utf8` | 27.8 (26.8–29.2, ±2%) | 1831 (1806–1946, ±1%) | ycs 65.8× | 316 / 1894 | equal, v1 bytes identical |
| `text-append-1txn` | 15.5 (15.1–19.8, ±6%) | 26357 (26278–26554, ±1%) | ycs 1698.8× | 173 / 26357 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 3.8 (3.8–3.9, ±1%) | 1428 (1392–1464, ±1%) | ycs 374.5× | 82.0 / 1433 | equal, v1 bytes identical |
| `text-append-1txn-observed` | 222 (219–229, ±1%) | 26079 (25790–26523, ±2%) | ycs 117.7× | 779 / 26079 | equal, v1 bytes identical |
| `text-append-1txn-observed-200k` | 449 (441–488, ±2%) | 123470 (121986–123496, ±1%) | ycs 275.0× | 1031 / 123470 | equal, v1 bytes identical |
| `text-append-1txn-observed-400k` | 931 (916–955, ±2%) | 517954 (516318–518566, ±0%) | ycs 556.1× | 1554 / 517954 | equal, v1 bytes identical |
| `text-append-1txn-observed-25k` | 53.5 (53.1–60.3, ±11%) | 1433 (1401–1503, ±2%) | ycs 26.8× | 490 / 1434 | equal, v1 bytes identical |
| `txn-empty` | 7.7 (7.5–8.0, ±1%) | 12.3 (11.7–12.7, ±2%) | ycs 1.6× | 56.3 / 18.9 | equal, v1 bytes identical |
| `text-random` | 36.4 (35.7–38.3, ±2%) | 1772 (1747–1850, ±1%) | ycs 48.7× | 411 / 1795 | equal, v1 bytes identical |
| `text-random-100k` | 1310 (1300–1320, ±1%) | 172108 (166783–185142, ±8%) | ycs 131.4× | 1310 / 172108 | equal, v1 bytes identical |
| `trace-replay` | 351 (347–375, ±2%) | 12037 (11843–12458, ±3%) | ycs 34.2× | 1236 / 11742 | equal, v1 bytes identical |
| `concurrent-20` | 92.4 (89.8–107, ±6%) | 79.5 (78.3–81.5, ±1%) | YDotNet 1.2× | 563 / 100 | equal, v1 bytes differ |
| `many-clients-200` | 2.0 (1.9–2.2, ±7%) | 2.3 (2.3–2.5, ±2%) | ycs 1.2× | 19.0 / 3.0 | equal, v1 bytes differ |
| `many-clients-1000` | 7.5 (7.3–7.6, ±2%) | 25.1 (24.3–26.5, ±3%) | ycs 3.4× | 53.5 / 26.3 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 74.8 (72.8–77.3, ±1%) | 48.9 (47.9–55.8, ±3%) | YDotNet 1.5× | 446 / 58.5 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 74.8 (71.9–83.5, ±2%) | 48.7 (48.3–49.2, ±0%) | YDotNet 1.5× | 533 / 59.0 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 76.4 (74.6–80.5, ±1%) | 50.4 (50.0–54.6, ±3%) | YDotNet 1.5× | 460 / 60.5 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 77.6 (74.6–80.0, ±3%) | 50.5 (49.4–52.5, ±2%) | YDotNet 1.5× | 462 / 60.2 | equal, v1 bytes identical |
| `apply-state-x100/ycs` | 827 (777–966, ±2%) | 732 (711–757, ±0%) | YDotNet 1.1× | 1999 / 908 | equal, v1 bytes identical |
| `apply-updates/ycs` | 271 (266–288, ±5%) | 365 (361–390, ±1%) | ycs 1.3× | 1013 / 378 | equal, v1 bytes identical |
| `apply-updates/yrs` | 275 (266–296, ±2%) | 365 (360–376, ±1%) | ycs 1.3× | 1080 / 378 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 171 (167–201, ±2%) | 1797 (1760–1842, ±1%) | ycs 10.5× | 795 / 1823 | equal, v1 bytes identical |
| `encode-v1` | 8.2 (8.1–9.0, ±2%) | 8.9 (8.8–9.1, ±0%) | ycs 1.1× | 77.5 / 10.2 | equal, v1 bytes identical |
| `encode-v2` | 10.0 (9.9–10.5, ±2%) | 10.4 (9.9–11.6, ±5%) | ycs 1.0× | 77.2 / 11.3 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.1, ±7%) | 0.2 (0.2–0.3, ±7%) | ycs 2.4× | 3.8 / 1.1 | equal, v1 bytes identical |
| `encode-diff` | 4.9 (4.8–6.2, ±2%) | 4.7 (4.5–5.0, ±3%) | YDotNet 1.0× | 50.6 / 5.6 | equal, v1 bytes identical |
| `map-set-distinct` | 40.8 (36.9–51.2, ±1%) | 139 (137–153, ±1%) | ycs 3.4× | 429 / 218 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 28.8 (25.7–52.2, ±19%) | 116 (112–123, ±5%) | ycs 4.0× | 265 / 201 | equal, v1 bytes identical |
| `map-set-same` | 66.3 (64.6–76.7, ±2%) | 133 (130–139, ±2%) | ycs 2.0× | 631 / 190 | equal, v1 bytes identical |
| `array-push` | 25.3 (24.3–27.9, ±6%) | 116 (113–120, ±2%) | ycs 4.6× | 325 / 196 | equal, v1 bytes identical |
| `array-push-1txn` | 12.5 (11.6–17.8, ±26%) | 68823 (68306–70363, ±2%) | ycs 5501.7× | 121 / 68823 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 2.5 (2.3–2.7, ±3%) | 2207 (2169–2386, ±1%) | ycs 888.3× | 50.0 / 2241 | equal, v1 bytes identical |
| `array-insert-random` | 184 (176–219, ±3%) | 20342 (17883–22819, ±9%) | ycs 110.5× | 765 / 19770 | equal, v1 bytes identical |
| `array-delete-random` | 176 (164–195, ±5%) | 25457 (24829–26487, ±4%) | ycs 144.4× | 717 / 25457 | equal, v1 bytes identical |
| `read-text` | 0.2 (0.2–0.3, ±4%) | 47.4 (46.2–50.3, ±1%) | ycs 191.0× | 5.9 / 53.5 | equal, v1 bytes identical |
| `read-array` | 4.0 (3.9–4.3, ±5%) | 101 (98.0–111, ±1%) | ycs 25.3× | 9.7 / 178 | equal, v1 bytes identical |
| `read-map` | 26.0 (25.0–29.5, ±2%) | 404 (387–425, ±4%) | ycs 15.6× | 208 / 777 | equal, v1 bytes identical |
| `undo-type` | 33.0 (29.0–35.7, ±11%) | 164 (160–196, ±1%) | ycs 5.0× | 185 / 191 | equal, v1 bytes identical |
| `undo-all` | 43.5 (38.8–49.1, ±8%) | 350 (316–383, ±9%) | ycs 8.0× | 193 / 338 | equal, v1 bytes identical |
| `redo-all` | 46.5 (43.9–58.2, ±2%) | 81.2 (78.5–89.1, ±5%) | ycs 1.7× | 197 / 86.7 | equal, v1 bytes identical |
| `observe-count` | 57.7 (55.2–68.6, ±2%) | 3620 (3585–3707, ±1%) | ycs 62.7× | 584 / 3673 | equal, v1 bytes identical |
| `observe-delta` | 90.0 (88.3–92.5, ±2%) | 5409 (5371–5721, ±1%) | ycs 60.1× | 759 / 5610 | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 0.6 | 11.4 | 1.5 + 0.0 = 1.5 | 0.2 + 0.4 = 0.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 0.6 | 11.4 | 1.5 + 0.0 = 1.5 | 0.2 + 0.3 = 0.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 0.5 | 8.2 | 2.1 + 0.0 = 2.1 | 1.8 + 2.3 = 4.1 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 0.1 | 1.9 | 0.5 + 0.0 = 0.5 | 0.3 + 0.7 = 0.9 | 25,018 / 25,018 | 25,033 / 25,033 |
| `text-append-1txn-observed` | 8.3 | 8.2 | 2.4 + 0.0 = 2.4 | 1.3 + 2.3 = 3.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-observed-200k` | 16.5 | 15.9 | 4.7 + -5.0 = -0.3 | 2.5 + 4.5 = 6.9 | 200,018 / 200,018 | 200,033 / 200,033 |
| `text-append-1txn-observed-400k` | 32.9 | 31.1 | 9.2 + 0.9 = 10.1 | 4.9 + 8.7 = 13.6 | 400,018 / 400,018 | 400,033 / 400,033 |
| `text-append-1txn-observed-25k` | 2.1 | 1.9 | 0.7 + 0.0 = 0.7 | 0.3 + 0.7 = 1.0 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 1.7 | 2.2 | 3.2 + 0.0 = 3.2 | 0.4 + 4.0 = 4.4 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 7.9 | 12.0 | 16.3 + 3.4 = 19.7 | 3.3 + 19.0 = 22.3 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
| `trace-replay` | 24.9 | 28.5 | 1.9 + 0.0 = 1.9 | 0.2 + 2.4 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `concurrent-20` | 14.2 | 0.1 | 14.2 + 0.0 = 14.2 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `many-clients-200` | 0.4 | 0.0 | 0.6 + 0.0 = 0.6 | 0.1 + 0.5 = 0.6 | 34,878 / 34,878 | 20,223 / 20,223 |
| `many-clients-1000` | 1.3 | 0.0 | 2.2 + 0.0 = 2.2 | 0.3 + 1.9 = 2.2 | 149,755 / 149,755 | 93,984 / 93,984 |
| `apply-state-v1/ycs` | 38.1 | 0.0 | 2.7 + 0.0 = 2.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 38.1 | 0.0 | 2.7 + 0.0 = 2.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 40.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 40.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-x100/ycs` | 479.7 | 0.0 | 253.0 + 2.7 = 255.7 | 49.9 + 230.3 = 280.1 | 31,264,816 / 31,264,816 | 15,989,045 / 15,989,045 |
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
| `array-insert-random` | 5.4 | 17.9 | 10.4 + 0.0 = 10.4 | 2.6 + 13.4 = 15.9 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 4.2 | 5.4 | 11.6 + 0.0 = 11.6 | 2.8 + 11.8 = 14.6 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 2.3 + 0.0 = 2.4 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 106.8 | 4.8 + 0.0 = 4.8 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 175.5 | 20.1 + 0.0 = 20.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 16.6 | 2.2 | 11.6 + 0.0 = 11.6 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 25.4 | 0.0 | 12.1 + 0.0 = 12.2 | 0.2 + 7.6 = 7.8 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 31.2 | 0.0 | 17.4 + 0.0 = 17.4 | 0.3 + 15.3 = 15.6 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 17.8 | 57.2 | 1.7 + 0.0 = 1.7 | 0.5 + 1.0 = 1.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 74.3 | 130.5 | 1.7 + 0.0 = 1.7 | 0.5 + 0.2 = 0.7 | 100,018 / 100,018 | 100,033 / 100,033 |

Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.

| case | bytes | ycs | YDotNet |
| --- | ---: | --- | --- |
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 6 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 5 ms, 99 MiB | rejected: EndOfStream; unchanged; 11 ms, 100 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 6 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 6 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 10 ms, 99 MiB | rejected: EndOfStream; unchanged; 14 ms, 101 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 35 ms, 100 MiB | rejected: EndOfStream; unchanged; 10 ms, 100 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 28 ms, 99 MiB | rejected: EndOfStream; unchanged; 15 ms, 101 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 31 ms, 104 MiB | applied; readable; 12 ms, 101 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 11 ms, 99 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (575470) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 7 ms, 99 MiB | rejected: EndOfStream; unchanged; 9 ms, 100 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | applied; readable; 12 ms, 100 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 10 ms, 99 MiB | rejected: EndOfStream; unchanged; 15 ms, 101 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 13 ms, 99 MiB | applied; readable; 12 ms, 101 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | rejected: EndOfStream; unchanged; 15 ms, 101 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 8 ms, 99 MiB | applied; readable; 76 ms, 163 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 9 ms, 99 MiB | applied; readable; 249 ms, 211 MiB |
| `conflict-2000` | 10,006 | applied; readable; 188 ms, 100 MiB | applied; readable; 333 ms, 101 MiB |
