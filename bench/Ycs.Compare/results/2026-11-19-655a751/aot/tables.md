Run 2026-11-19 17:33 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs E0F2E5BC71B79C7B. Children: Native AOT (Ycs.Compare).

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16376028 kB RAM, Ubuntu 24.04.5 LTS, kernel 6.8.0.124, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 1.61 1.50 1.20 1/404 878433.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 51.3 (49.6–52.8, ±3%) | 3621 (3574–3668, ±1%) | ycs 70.6× | 51.4 / 3633 | equal, v1 bytes identical |
| `text-append-utf8` | 50.3 (49.6–51.4, ±1%) | 1889 (1856–1969, ±0%) | ycs 37.5× | 51.6 / 1907 | equal, v1 bytes identical |
| `text-append-1txn` | 26.2 (25.0–29.8, ±6%) | 26004 (25676–26089, ±1%) | ycs 991.5× | 25.6 / 26004 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 6.4 (6.4–9.5, ±1%) | 1438 (1396–1473, ±1%) | ycs 223.7× | 6.6 / 1426 | equal, v1 bytes identical |
| `text-append-1txn-observed` | 282 (280–286, ±0%) | 25846 (25710–25993, ±1%) | ycs 91.5× | 294 / 25846 | equal, v1 bytes identical |
| `text-append-1txn-observed-200k` | 577 (563–599, ±1%) | 121833 (121528–122252, ±0%) | ycs 211.2× | 594 / 121833 | equal, v1 bytes identical |
| `text-append-1txn-observed-400k` | 1166 (1154–1205, ±1%) | 513002 (511335–515916, ±1%) | ycs 440.2× | 1203 / 513002 | equal, v1 bytes identical |
| `text-append-1txn-observed-25k` | 70.6 (68.6–74.6, ±3%) | 1428 (1402–1462, ±1%) | ycs 20.2× | 71.0 / 1423 | equal, v1 bytes identical |
| `txn-empty` | 12.1 (11.8–12.9, ±3%) | 11.9 (11.7–12.3, ±1%) | YDotNet 1.0× | 12.1 / 17.0 | equal, v1 bytes identical |
| `text-random` | 43.9 (43.4–45.1, ±0%) | 1803 (1756–1831, ±2%) | ycs 41.0× | 46.6 / 1832 | equal, v1 bytes identical |
| `text-random-100k` | 794 (717–846, ±10%) | 172468 (171836–173038, ±0%) | ycs 217.2× | 794 / 172468 | equal, v1 bytes identical |
| `trace-replay` | 477 (465–492, ±1%) | 12246 (11759–12440, ±1%) | ycs 25.7× | 490 / 11908 | equal, v1 bytes identical |
| `concurrent-20` | 88.8 (87.2–93.5, ±3%) | 78.5 (77.1–80.6, ±1%) | YDotNet 1.1× | 101 / 99.4 | equal, v1 bytes differ |
| `many-clients-200` | 1.8 (1.8–1.9, ±2%) | 2.3 (2.2–2.5, ±5%) | ycs 1.2× | 1.9 / 2.7 | equal, v1 bytes differ |
| `many-clients-1000` | 7.6 (7.4–10.9, ±3%) | 25.0 (24.3–28.3, ±1%) | ycs 3.3× | 7.6 / 25.4 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 68.9 (66.4–72.8, ±2%) | 48.3 (47.8–50.5, ±1%) | YDotNet 1.4× | 84.5 / 53.0 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 69.3 (66.9–91.1, ±3%) | 48.3 (47.8–49.5, ±1%) | YDotNet 1.4× | 83.3 / 52.5 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 68.8 (67.8–71.0, ±1%) | 50.5 (49.6–58.1, ±11%) | YDotNet 1.4× | 85.8 / 56.0 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 68.6 (66.9–72.7, ±2%) | 49.9 (49.4–52.3, ±2%) | YDotNet 1.4× | 83.7 / 53.9 | equal, v1 bytes identical |
| `apply-state-x100/ycs` | 883 (847–981, ±1%) | 760 (720–798, ±2%) | YDotNet 1.2× | 996 / 910 | equal, v1 bytes identical |
| `apply-updates/ycs` | 411 (409–424, ±2%) | 364 (360–391, ±5%) | YDotNet 1.1× | 422 / 382 | equal, v1 bytes identical |
| `apply-updates/yrs` | 418 (413–422, ±1%) | 367 (361–415, ±1%) | YDotNet 1.1× | 433 / 380 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 237 (232–254, ±2%) | 1783 (1743–1822, ±2%) | ycs 7.5× | 249 / 1834 | equal, v1 bytes identical |
| `encode-v1` | 13.1 (11.4–15.1, ±4%) | 11.0 (10.9–11.9, ±5%) | YDotNet 1.2× | 14.4 / 10.5 | equal, v1 bytes identical |
| `encode-v2` | 14.9 (14.2–19.1, ±9%) | 9.9 (9.7–10.4, ±2%) | YDotNet 1.5× | 15.9 / 11.9 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.1, ±4%) | 0.3 (0.2–0.4, ±11%) | ycs 2.7× | 0.1 / 0.3 | equal, v1 bytes identical |
| `encode-diff` | 7.0 (6.6–8.0, ±7%) | 4.7 (4.5–5.2, ±4%) | YDotNet 1.5× | 7.9 / 4.5 | equal, v1 bytes identical |
| `map-set-distinct` | 81.2 (79.3–86.8, ±2%) | 225 (222–250, ±2%) | ycs 2.8× | 94.2 / 288 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 51.9 (50.7–55.9, ±2%) | 208 (204–218, ±1%) | ycs 4.0× | 62.6 / 280 | equal, v1 bytes identical |
| `map-set-same` | 127 (123–137, ±2%) | 218 (215–237, ±8%) | ycs 1.7× | 133 / 232 | equal, v1 bytes identical |
| `array-push` | 53.1 (52.0–56.9, ±0%) | 257 (249–271, ±4%) | ycs 4.8× | 58.6 / 273 | equal, v1 bytes identical |
| `array-push-1txn` | 23.9 (22.6–46.6, ±4%) | 67405 (67189–68063, ±1%) | ycs 2823.5× | 27.0 / 67405 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 4.6 (4.3–6.8, ±8%) | 2263 (2217–2332, ±1%) | ycs 495.3× | 6.0 / 2291 | equal, v1 bytes identical |
| `array-insert-random` | 206 (200–236, ±3%) | 21389 (18519–26439, ±4%) | ycs 104.0× | 211 / 21024 | equal, v1 bytes identical |
| `array-delete-random` | 218 (207–242, ±0%) | 25591 (25019–25981, ±2%) | ycs 117.2× | 224 / 25591 | equal, v1 bytes identical |
| `read-text` | 0.4 (0.3–0.4, ±2%) | 46.7 (45.0–54.4, ±5%) | ycs 132.8× | 0.4 / 46.2 | equal, v1 bytes identical |
| `read-array` | 5.1 (5.1–5.4, ±1%) | 718 (621–746, ±1%) | ycs 140.8× | 5.6 / 667 | equal, v1 bytes identical |
| `read-map` | 37.4 (36.0–43.2, ±3%) | 1165 (1141–1201, ±1%) | ycs 31.1× | 37.6 / 1180 | equal, v1 bytes identical |
| `undo-type` | 28.2 (27.3–41.3, ±2%) | 178 (175–179, ±1%) | ycs 6.3× | 59.6 / 186 | equal, v1 bytes identical |
| `undo-all` | 37.9 (36.7–40.2, ±3%) | 340 (323–383, ±5%) | ycs 9.0× | 55.0 / 344 | equal, v1 bytes identical |
| `redo-all` | 45.6 (43.6–52.8, ±8%) | 83.5 (82.2–91.8, ±1%) | ycs 1.8× | 49.9 / 86.5 | equal, v1 bytes identical |
| `observe-count` | 105 (103–119, ±2%) | 3613 (3592–3756, ±1%) | ycs 34.4× | 121 / 3670 | equal, v1 bytes identical |
| `observe-delta` | 163 (159–181, ±0%) | 5648 (5539–5675, ±2%) | ycs 34.7× | 177 / 5660 | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 0.6 | 22.9 | 0.6 + 0.0 = 0.6 | 0.0 + 0.1 = 0.2 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 0.6 | 22.9 | 0.6 + 0.0 = 0.6 | 0.0 + 0.1 = 0.2 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 0.5 | 19.1 | 1.4 + 0.0 = 1.4 | 2.6 + 2.1 = 4.7 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 0.1 | 4.8 | 0.4 + 0.0 = 0.4 | 0.2 + 0.5 = 0.8 | 25,018 / 25,018 | 25,033 / 25,033 |
| `text-append-1txn-observed` | 8.3 | 19.1 | 1.7 + 0.0 = 1.7 | 2.1 + 2.1 = 4.2 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-observed-200k` | 16.5 | 38.1 | 3.3 + 0.0 = 3.3 | 2.1 + 4.3 = 6.3 | 200,018 / 200,018 | 200,033 / 200,033 |
| `text-append-1txn-observed-400k` | 32.9 | 76.3 | 6.5 + 0.0 = 6.5 | 4.1 + 8.5 = 12.6 | 400,018 / 400,018 | 400,033 / 400,033 |
| `text-append-1txn-observed-25k` | 2.1 | 4.8 | 0.5 + 0.0 = 0.5 | 0.3 + 0.5 = 0.8 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 1.7 | 4.6 | 3.1 + 0.0 = 3.1 | 0.3 + 3.9 = 4.2 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 7.9 | 22.9 | 16.1 + 0.0 = 16.1 | 3.1 + 18.8 = 21.9 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
| `trace-replay` | 22.9 | 67.1 | 1.6 + 0.0 = 1.6 | 0.0 + 2.2 = 2.3 | 289,132 / 289,132 | 159,927 / 159,927 |
| `concurrent-20` | 14.1 | 0.1 | 14.1 + 0.0 = 14.1 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `many-clients-200` | 0.4 | 0.0 | 0.6 + 0.0 = 0.6 | 0.1 + 0.5 = 0.6 | 34,878 / 34,878 | 20,223 / 20,223 |
| `many-clients-1000` | 1.3 | 0.0 | 2.1 + 0.0 = 2.1 | 0.2 + 1.9 = 2.1 | 149,755 / 149,755 | 93,984 / 93,984 |
| `apply-state-v1/ycs` | 16.2 | 0.0 | 2.8 + 0.0 = 2.8 | 0.3 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 16.2 | 0.0 | 2.8 + 0.0 = 2.8 | 0.3 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 16.2 | 0.0 | 2.7 + 0.0 = 2.7 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 16.2 | 0.0 | 2.7 + 0.0 = 2.7 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-x100/ycs` | 451.7 | 0.0 | 241.0 + 0.0 = 241.0 | 29.9 + 229.2 = 259.0 | 31,264,816 / 31,264,816 | 15,989,045 / 15,989,045 |
| `apply-updates/ycs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.6 + 2.2 = 16.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.6 + 2.2 = 16.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 18.2 | 0.0 | 27.6 + 0.0 = 27.6 | 14.6 + 6.0 = 20.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 1.6 + 0.0 = 1.6 | 0.0 + 2.2 = 2.2 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 2.2 + 0.0 = 2.2 | 0.0 + 2.2 = 2.2 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-sv` | 0.0 | 0.1 | 2.6 + 0.0 = 2.6 | 0.0 + 2.2 = 2.3 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-diff` | 1.4 | 1.4 | 2.1 + 0.0 = 2.1 | 0.0 + 2.2 = 2.2 | 289,132 / 289,132 | 159,927 / 159,927 |
| `map-set-distinct` | 22.6 | 42.0 | 22.1 + 0.0 = 22.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 25.3 | 38.1 | 22.1 + 0.0 = 22.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 6.8 | 42.0 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 4.3 | 71.7 | 9.3 + 0.0 = 9.3 | 4.0 + 3.0 = 7.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | 4.3 | 67.9 | 9.3 + 0.0 = 9.3 | 4.0 + 4.6 = 8.6 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn-20k` | 0.9 | 13.6 | 1.7 + 0.0 = 1.7 | 0.5 + 1.0 = 1.5 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 5.4 | 35.9 | 9.9 + 0.0 = 9.9 | 2.0 + 14.7 = 16.8 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 4.2 | 16.4 | 10.3 + 0.0 = 10.3 | 6.5 + 11.8 = 18.3 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 2.8 + 0.0 = 2.8 | 0.3 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 229.0 | 4.0 + 0.0 = 4.0 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 359.0 | 20.1 + 0.0 = 20.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 16.4 | 4.6 | 11.5 + 0.0 = 11.5 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 24.8 | 0.0 | 12.1 + 0.0 = 12.1 | 0.2 + 7.6 = 7.7 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 30.6 | 0.0 | 17.3 + 0.0 = 17.3 | 0.3 + 15.3 = 15.5 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 17.8 | 66.4 | 1.0 + 0.0 = 1.0 | 0.3 + 0.1 = 0.4 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 73.5 | 159.6 | 1.0 + 0.0 = 1.0 | 0.3 + 0.1 = 0.4 | 100,018 / 100,018 | 100,033 / 100,033 |
