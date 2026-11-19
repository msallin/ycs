Run 2026-11-19 18:55 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs E0F2E5BC71B79C7B. Children: Native AOT (Ycs.Compare).

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16376028 kB RAM, Ubuntu 24.04.5 LTS, kernel 6.8.0.124, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 1.00 1.00 1.00 1/352 887115.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 48.2 (47.0–50.1, ±2%) | 3628 (3558–3694, ±1%) | ycs 75.3× | 47.6 / 3628 | equal, v1 bytes identical |
| `text-append-utf8` | 46.9 (46.4–48.4, ±3%) | 1891 (1845–1949, ±1%) | ycs 40.3× | 48.7 / 1894 | equal, v1 bytes identical |
| `text-append-1txn` | 22.3 (21.9–23.6, ±4%) | 25985 (25969–26175, ±1%) | ycs 1166.8× | 22.4 / 25985 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 5.6 (5.4–6.3, ±9%) | 1426 (1402–1466, ±3%) | ycs 252.6× | 5.7 / 1404 | equal, v1 bytes identical |
| `text-append-1txn-observed` | 260 (257–276, ±3%) | 26150 (25665–26381, ±2%) | ycs 100.7× | 273 / 26150 | equal, v1 bytes identical |
| `text-append-1txn-observed-200k` | 528 (521–546, ±2%) | 121945 (121419–122110, ±0%) | ycs 230.9× | 541 / 121945 | equal, v1 bytes identical |
| `text-append-1txn-observed-400k` | 1089 (1076–1123, ±1%) | 512549 (512451–512666, ±0%) | ycs 470.6× | 1112 / 512549 | equal, v1 bytes identical |
| `text-append-1txn-observed-25k` | 64.2 (62.9–72.5, ±9%) | 1404 (1382–1459, ±2%) | ycs 21.9× | 66.9 / 1443 | equal, v1 bytes identical |
| `txn-empty` | 11.3 (11.0–11.8, ±4%) | 12.1 (11.9–12.5, ±1%) | ycs 1.1× | 11.4 / 15.7 | equal, v1 bytes identical |
| `text-random` | 44.1 (42.4–45.7, ±3%) | 1785 (1769–1813, ±1%) | ycs 40.5× | 46.9 / 1785 | equal, v1 bytes identical |
| `text-random-100k` | 831 (819–836, ±1%) | 163465 (152473–163521, ±7%) | ycs 196.8× | 831 / 163465 | equal, v1 bytes identical |
| `trace-replay` | 449 (436–461, ±3%) | 12153 (12082–12416, ±1%) | ycs 27.1× | 460 / 11898 | equal, v1 bytes identical |
| `concurrent-20` | 82.5 (79.5–91.6, ±3%) | 78.3 (77.4–80.5, ±2%) | YDotNet 1.1× | 94.1 / 99.7 | equal, v1 bytes differ |
| `many-clients-200` | 1.7 (1.6–1.7, ±3%) | 2.3 (2.2–2.4, ±3%) | ycs 1.4× | 1.7 / 2.8 | equal, v1 bytes differ |
| `many-clients-1000` | 7.1 (6.9–8.4, ±16%) | 24.1 (23.9–25.4, ±2%) | ycs 3.4× | 7.5 / 25.5 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 60.5 (59.7–64.9, ±1%) | 48.6 (47.9–49.3, ±1%) | YDotNet 1.2× | 75.1 / 52.2 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 61.2 (59.7–64.6, ±2%) | 48.7 (48.3–50.7, ±2%) | YDotNet 1.3× | 78.9 / 52.0 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 62.2 (60.0–68.8, ±7%) | 51.5 (49.7–54.1, ±3%) | YDotNet 1.2× | 77.1 / 55.1 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 61.5 (60.4–67.0, ±5%) | 50.2 (49.5–52.4, ±2%) | YDotNet 1.2× | 78.5 / 55.3 | equal, v1 bytes identical |
| `apply-state-x100/ycs` | 811 (773–879, ±3%) | 750 (710–787, ±1%) | YDotNet 1.1× | 911 / 901 | equal, v1 bytes identical |
| `apply-updates/ycs` | 360 (353–393, ±4%) | 361 (357–384, ±4%) | ycs 1.0× | 374 / 374 | equal, v1 bytes identical |
| `apply-updates/yrs` | 355 (352–361, ±1%) | 362 (359–373, ±3%) | ycs 1.0× | 375 / 394 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 191 (188–204, ±4%) | 1761 (1748–1810, ±1%) | ycs 9.2× | 206 / 1815 | equal, v1 bytes identical |
| `encode-v1` | 13.3 (11.4–13.8, ±1%) | 11.1 (10.8–11.6, ±5%) | YDotNet 1.2× | 14.2 / 10.2 | equal, v1 bytes identical |
| `encode-v2` | 14.2 (13.8–16.5, ±9%) | 9.8 (9.6–10.0, ±1%) | YDotNet 1.5× | 15.2 / 12.0 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.2, ±38%) | 0.3 (0.2–0.3, ±1%) | ycs 2.6× | 0.1 / 0.2 | equal, v1 bytes identical |
| `encode-diff` | 6.8 (6.7–9.4, ±2%) | 4.7 (4.4–4.9, ±3%) | YDotNet 1.5× | 9.4 / 4.9 | equal, v1 bytes identical |
| `map-set-distinct` | 73.2 (72.1–75.6, ±1%) | 224 (219–235, ±5%) | ycs 3.1× | 85.9 / 283 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 48.3 (45.2–58.9, ±13%) | 204 (193–219, ±3%) | ycs 4.2× | 58.4 / 264 | equal, v1 bytes identical |
| `map-set-same` | 112 (108–116, ±3%) | 222 (218–259, ±1%) | ycs 2.0× | 127 / 240 | equal, v1 bytes identical |
| `array-push` | 45.0 (44.0–51.8, ±5%) | 247 (245–251, ±0%) | ycs 5.5× | 48.7 / 265 | equal, v1 bytes identical |
| `array-push-1txn` | 19.1 (18.8–19.7, ±1%) | 66661 (65828–67515, ±1%) | ycs 3485.7× | 22.4 / 66661 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 3.6 (3.5–3.9, ±0%) | 2244 (2229–2323, ±1%) | ycs 620.5× | 4.6 / 2264 | equal, v1 bytes identical |
| `array-insert-random` | 200 (195–211, ±1%) | 18860 (17435–20582, ±3%) | ycs 94.5× | 214 / 18515 | equal, v1 bytes identical |
| `array-delete-random` | 205 (193–231, ±6%) | 23935 (23815–26052, ±9%) | ycs 116.8× | 204 / 23935 | equal, v1 bytes identical |
| `read-text` | 0.4 (0.3–0.4, ±12%) | 45.6 (44.1–48.9, ±2%) | ycs 121.8× | 0.4 / 45.0 | equal, v1 bytes identical |
| `read-array` | 6.0 (5.9–7.3, ±14%) | 702 (609–721, ±1%) | ycs 116.6× | 6.0 / 674 | equal, v1 bytes identical |
| `read-map` | 35.4 (34.1–37.4, ±2%) | 1149 (1115–1189, ±2%) | ycs 32.5× | 34.7 / 1178 | equal, v1 bytes identical |
| `undo-type` | 25.2 (24.8–26.0, ±1%) | 176 (174–260, ±7%) | ycs 7.0× | 61.6 / 180 | equal, v1 bytes identical |
| `undo-all` | 34.0 (33.6–40.8, ±14%) | 349 (321–385, ±0%) | ycs 10.3× | 50.4 / 368 | equal, v1 bytes identical |
| `redo-all` | 41.5 (40.6–45.2, ±1%) | 83.0 (82.4–91.0, ±1%) | ycs 2.0× | 46.8 / 85.2 | equal, v1 bytes identical |
| `observe-count` | 93.1 (92.2–94.5, ±1%) | 3626 (3592–3721, ±1%) | ycs 38.9× | 112 / 3714 | equal, v1 bytes identical |
| `observe-delta` | 149 (146–214, ±5%) | 5608 (5528–5786, ±1%) | ycs 37.7× | 166 / 5669 | equal, v1 bytes identical |

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
