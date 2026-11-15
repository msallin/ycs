Run 2026-11-15 10:12 UTC: 3 passes, each workload in its own process per library and pass, 1 warm-up + 5 timed runs per process; inputs B0C9A1DE44B7103F.

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 32859788 kB RAM, Ubuntu 26.04 LTS, kernel 7.0.0.43, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 0.90 1.46 3.29 1/1973 2974111.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median). The last column says which is faster, by the ratio of the medians.

| workload | ycs | YDotNet | faster | checks |
| --- | ---: | ---: | --- | --- |
| `text-append` | 49.2 (20.3–161, ±77%) | 3019 (2949–3174, ±2%) | ycs 61.3× | equal, v1 bytes identical |
| `text-append-utf8` | 52.4 (20.6–164, ±123%) | 1588 (1498–1663, ±3%) | ycs 30.3× | equal, v1 bytes identical |
| `text-append-1txn` | 53343 (53238–53814, ±1%) | 16695 (16687–17505, ±5%) | YDotNet 3.2× | equal, v1 bytes identical |
| `text-append-1txn-25k` | 3284 (3063–3355, ±0%) | 1034 (1027–1054, ±1%) | YDotNet 3.2× | equal, v1 bytes identical |
| `txn-empty` | 30.6 (29.8–59.3, ±12%) | 13.8 (13.2–15.5, ±6%) | YDotNet 2.2× | equal, v1 bytes identical |
| `text-random` | 72.0 (22.7–172, ±13%) | 1489 (1434–1559, ±3%) | ycs 20.7× | equal, v1 bytes identical |
| `text-random-100k` | 1522 (1504–1576, ±4%) | 148705 (129134–158019, ±13%) | ycs 97.7× | equal, v1 bytes identical |
| `trace-replay` | 260 (245–295, ±8%) | 7536 (7264–8256, ±5%) | ycs 28.9× | equal, v1 bytes identical |
| `concurrent-20` | 87.8 (63.1–320, ±44%) | 62.2 (59.0–64.8, ±5%) | YDotNet 1.4× | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 119 (63.9–195, ±20%) | 38.4 (37.9–40.1, ±4%) | YDotNet 3.1× | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 80.7 (62.5–232, ±33%) | 38.1 (37.4–42.6, ±4%) | YDotNet 2.1× | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 96.4 (66.6–190, ±24%) | 39.9 (39.1–43.3, ±5%) | YDotNet 2.4× | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 79.7 (68.6–194, ±6%) | 39.8 (39.3–42.1, ±1%) | YDotNet 2.0× | equal, v1 bytes identical |
| `apply-updates/ycs` | 234 (230–246, ±1%) | 250 (244–267, ±2%) | ycs 1.1× | equal, v1 bytes identical |
| `apply-updates/yrs` | 230 (222–235, ±3%) | 256 (242–288, ±4%) | ycs 1.1× | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 138 (125–316, ±7%) | 1169 (1135–1224, ±3%) | ycs 8.5× | equal, v1 bytes identical |
| `encode-v1` | 36.4 (26.2–48.0, ±31%) | 6.3 (6.1–8.0, ±9%) | YDotNet 5.8× | equal, v1 bytes identical |
| `encode-v2` | 27.5 (7.4–58.0, ±3%) | 7.6 (7.2–10.9, ±25%) | YDotNet 3.6× | equal, v1 bytes identical |
| `encode-sv` | 0.3 (0.2–0.3, ±9%) | 0.2 (0.2–0.3, ±9%) | YDotNet 1.1× | equal, v1 bytes identical |
| `encode-diff` | 16.0 (15.6–25.5, ±1%) | 3.3 (3.1–5.1, ±50%) | YDotNet 4.9× | equal, v1 bytes identical |
| `map-set-distinct` | 50.9 (43.9–157, ±12%) | 118 (109–164, ±21%) | ycs 2.3× | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 58.3 (28.3–142, ±23%) | 108 (96.3–152, ±11%) | ycs 1.9× | equal, v1 bytes identical |
| `map-set-same` | 63.1 (41.1–123, ±42%) | 106 (98.8–133, ±7%) | ycs 1.7× | equal, v1 bytes identical |
| `array-push` | 41.3 (34.3–287, ±68%) | 111 (80.2–138, ±21%) | ycs 2.7× | equal, v1 bytes identical |
| `array-push-1txn` | **killed at 8227 MiB resident (limit 8192 MiB) after 21.2 s** | 48787 (48753–52040, ±7%) | not counted | a run produced no content |
| `array-push-1txn-20k` | 1129 (913–1240, ±3%) | 1568 (1542–1600, ±2%) | ycs 1.4× | equal, v1 bytes identical |
| `array-insert-random` | 207 (148–319, ±27%) | 13536 (11856–24988, ±15%) | ycs 65.4× | equal, v1 bytes identical |
| `array-delete-random` | 53447 (45672–60495, ±15%) | 20569 (19351–24282, ±18%) | YDotNet 2.6× | equal, v1 bytes identical |
| `read-text` | 0.4 (0.3–0.5, ±22%) | 40.1 (38.7–46.4, ±13%) | ycs 105.2× | equal, v1 bytes identical |
| `read-array` | 32.0 (4.6–45.0, ±1%) | 66.8 (63.2–177, ±4%) | ycs 2.1× | equal, v1 bytes identical |
| `read-map` | 16.1 (15.4–23.8, ±4%) | 359 (333–459, ±6%) | ycs 22.3× | equal, v1 bytes identical |
| `undo-type` | 68.2 (57.9–89.2, ±4%) | 136 (131–141, ±0%) | ycs 2.0× | equal, v1 bytes identical |
| `undo-all` | 45.5 (31.1–121, ±9%) | 207 (196–237, ±2%) | ycs 4.5× | equal, v1 bytes identical |
| `redo-all` | 72.2 (66.8–110, ±6%) | 50.5 (49.3–52.0, ±2%) | YDotNet 1.4× | equal, v1 bytes identical |
| `observe-count` | 95.6 (27.0–148, ±16%) | 3032 (2972–3238, ±2%) | ycs 31.7× | equal, v1 bytes identical |
| `observe-delta` | 49.4 (45.0–74.5, ±38%) | 4584 (4410–4884, ±3%) | ycs 92.8× | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 19.3 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + 0.3 = 0.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 19.3 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + -0.1 = 0.1 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 21.7 | 8.2 | 4.3 + 1.5 = 5.7 | 1.8 + 2.6 = 4.3 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 5.4 | 1.9 | 0.9 + 1.0 = 1.9 | 0.3 + 0.3 = 0.6 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 4.3 | 2.2 | 5.6 + 0.0 = 5.7 | 0.4 + 3.8 = 4.2 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 21.1 | 12.0 | 28.6 + 1.6 = 30.2 | 3.3 + 19.2 = 22.5 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
| `trace-replay` | 84.8 | 28.5 | 3.0 + 0.0 = 3.0 | 0.2 + 3.1 = 3.3 | 289,132 / 289,132 | 159,927 / 159,927 |
| `concurrent-20` | 31.5 | 0.1 | 30.5 + 0.0 = 30.5 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `apply-state-v1/ycs` | 39.5 | 0.0 | 4.6 + 0.0 = 4.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 39.5 | 0.0 | 4.6 + 0.0 = 4.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 41.5 | 0.0 | 4.5 + 0.0 = 4.5 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 41.5 | 0.0 | 4.5 + 0.0 = 4.5 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/ycs` | 67.2 | 9.9 | 18.6 + 0.0 = 18.6 | 14.8 + 2.2 = 17.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 67.2 | 9.9 | 18.6 + 0.0 = 18.6 | 14.8 + 2.2 = 17.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 52.0 | 0.0 | 20.8 + 0.0 = 20.8 | 14.8 + 5.5 = 20.3 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 3.4 + 0.0 = 3.4 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 4.0 + 0.0 = 4.0 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-sv` | 0.0 | 0.1 | 4.4 + 0.0 = 4.4 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-diff` | 1.4 | 1.4 | 3.9 + 0.0 = 3.9 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `map-set-distinct` | 52.8 | 21.4 | 40.5 + 0.0 = 40.5 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 41.8 | 17.6 | 40.5 + 0.0 = 40.5 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 53.4 | 22.5 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 47.0 | 37.8 | 9.4 + 0.0 = 9.5 | 5.2 + 3.0 = 8.2 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | | | | | | |
| `array-push-1txn-20k` | 1535.9 | 6.4 | 1.8 + 1.2 = 3.0 | 0.7 + 1.9 = 2.6 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 23.5 | 17.9 | 19.7 + 0.0 = 19.7 | 2.6 + 16.2 = 18.8 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 38.3 | 5.5 | 20.6 + 3.2 = 23.8 | 2.8 + 12.1 = 14.9 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 4.3 + 0.0 = 4.3 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 106.8 | 4.3 + 0.0 = 4.3 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 175.5 | 35.7 + 0.0 = 35.7 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 21.5 | 3.0 | 11.7 + 0.0 = 11.7 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 30.3 | 0.0 | 11.8 + 0.0 = 11.8 | 0.2 + 7.6 = 7.8 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 36.1 | 0.0 | 21.4 + 0.0 = 21.4 | 0.3 + 15.3 = 15.6 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 39.1 | 57.2 | 1.5 + 0.0 = 1.5 | 0.5 + 1.3 = 1.8 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 84.9 | 130.5 | 1.5 + 0.0 = 1.5 | 0.5 + 0.1 = 0.6 | 100,018 / 100,018 | 100,033 / 100,033 |

Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.

| case | bytes | ycs | YDotNet |
| --- | ---: | --- | --- |
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 4 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 3 ms, 265 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 4 ms, 265 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 3 ms, 265 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 6 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 21 ms, 266 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 16 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 18 ms, 271 MiB | applied; readable; 10 ms, 267 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 5 ms, 265 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (2989110) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 4 ms, 265 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 5 ms, 265 MiB | applied; readable; 10 ms, 267 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 6 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 9 ms, 265 MiB | applied; readable; 10 ms, 267 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 5 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 5 ms, 265 MiB | applied; readable; 61 ms, 329 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 6 ms, 265 MiB | applied; readable; 184 ms, 377 MiB |
| `conflict-2000` | 10,006 | applied; readable; 102 ms, 267 MiB | applied; readable; 217 ms, 268 MiB |
