Run 2026-11-19 10:23 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs E0F2E5BC71B79C7B. Children: Native AOT (Ycs.Compare).

Machine: AMD EPYC-Genoa Processor, 4 logical cores, 16376028 kB RAM, Ubuntu 24.04.5 LTS, kernel 6.8.0.124, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 1.25 1.13 1.10 2/377 788154.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 54.7 (53.4–56.9, ±0%) | 3601 (3555–3731, ±3%) | ycs 65.8× | 55.6 / 3626 | equal, v1 bytes identical |
| `text-append-utf8` | 53.4 (53.0–56.3, ±0%) | 1896 (1846–1968, ±2%) | ycs 35.5× | 55.5 / 1889 | equal, v1 bytes identical |
| `text-append-1txn` | 24.8 (24.2–25.8, ±2%) | 25919 (25771–26404, ±2%) | ycs 1043.6× | 26.0 / 25919 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 6.3 (6.2–6.5, ±1%) | 1416 (1402–1480, ±2%) | ycs 224.7× | 6.4 / 1439 | equal, v1 bytes identical |
| `text-append-1txn-observed` | 293 (286–318, ±1%) | 26577 (26284–26748, ±1%) | ycs 90.6× | 307 / 26577 | equal, v1 bytes identical |
| `text-append-1txn-observed-200k` | 584 (577–598, ±2%) | 122593 (122363–124323, ±1%) | ycs 209.9× | 603 / 122593 | equal, v1 bytes identical |
| `text-append-1txn-observed-400k` | 1221 (1187–1300, ±0%) | 514329 (513262–515310, ±0%) | ycs 421.2× | 1237 / 514329 | equal, v1 bytes identical |
| `text-append-1txn-observed-25k` | 72.5 (70.0–136, ±15%) | 1422 (1371–1502, ±4%) | ycs 19.6× | 74.7 / 1421 | equal, v1 bytes identical |
| `txn-empty` | 12.4 (11.8–14.0, ±8%) | 12.4 (11.9–13.7, ±3%) | ycs 1.0× | 12.8 / 18.2 | equal, v1 bytes identical |
| `text-random` | 46.4 (44.9–50.7, ±3%) | 1782 (1770–1814, ±1%) | ycs 38.4× | 49.9 / 1867 | equal, v1 bytes identical |
| `text-random-100k` | 893 (803–1011, ±13%) | 187208 (180741–209687, ±12%) | ycs 209.7× | 893 / 187208 | equal, v1 bytes identical |
| `trace-replay` | 486 (474–502, ±1%) | 12296 (11976–12502, ±1%) | ycs 25.3× | 542 / 11887 | equal, v1 bytes identical |
| `concurrent-20` | 122 (120–125, ±1%) | 80.8 (79.2–91.8, ±1%) | YDotNet 1.5× | 137 / 101 | equal, v1 bytes differ |
| `many-clients-200` | 2.5 (2.4–2.5, ±2%) | 2.3 (2.2–2.6, ±8%) | YDotNet 1.1× | 2.5 / 2.7 | equal, v1 bytes differ |
| `many-clients-1000` | 10.4 (10.2–10.6, ±1%) | 25.4 (23.9–35.3, ±2%) | ycs 2.4× | 10.2 / 26.1 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 99.7 (95.5–109, ±6%) | 49.4 (48.6–50.3, ±1%) | YDotNet 2.0× | 110 / 53.0 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 99.0 (95.4–106, ±1%) | 49.0 (48.5–51.1, ±2%) | YDotNet 2.0× | 109 / 54.4 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 101 (97.6–120, ±2%) | 51.4 (50.3–53.5, ±1%) | YDotNet 2.0× | 112 / 54.7 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 101 (99.7–106, ±0%) | 50.7 (50.0–52.8, ±1%) | YDotNet 2.0× | 111 / 54.6 | equal, v1 bytes identical |
| `apply-state-x100/ycs` | 1199 (1144–1275, ±2%) | 766 (735–790, ±3%) | YDotNet 1.6× | 1297 / 915 | equal, v1 bytes identical |
| `apply-updates/ycs` | 477 (470–534, ±4%) | 366 (361–391, ±6%) | YDotNet 1.3× | 492 / 369 | equal, v1 bytes identical |
| `apply-updates/yrs` | 478 (469–523, ±6%) | 368 (363–396, ±1%) | YDotNet 1.3× | 487 / 377 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 291 (286–301, ±2%) | 1761 (1744–1777, ±0%) | ycs 6.0× | 327 / 1833 | equal, v1 bytes identical |
| `encode-v1` | 13.3 (12.0–13.8, ±2%) | 10.9 (10.7–11.2, ±1%) | YDotNet 1.2× | 13.5 / 10.0 | equal, v1 bytes identical |
| `encode-v2` | 14.9 (14.8–15.8, ±1%) | 10.2 (9.9–13.2, ±1%) | YDotNet 1.5× | 14.5 / 12.1 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.1, ±2%) | 0.3 (0.2–0.3, ±9%) | ycs 2.7× | 0.1 / 0.3 | equal, v1 bytes identical |
| `encode-diff` | 7.3 (6.9–7.7, ±2%) | 4.8 (4.6–5.3, ±2%) | YDotNet 1.5× | 7.7 / 4.7 | equal, v1 bytes identical |
| `map-set-distinct` | 81.2 (78.9–84.4, ±2%) | 225 (218–315, ±20%) | ycs 2.8× | 91.8 / 286 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 51.9 (49.5–59.9, ±1%) | 205 (200–230, ±1%) | ycs 4.0× | 63.1 / 275 | equal, v1 bytes identical |
| `map-set-same` | 127 (125–130, ±1%) | 225 (213–234, ±3%) | ycs 1.8× | 134 / 244 | equal, v1 bytes identical |
| `array-push` | 53.9 (51.9–55.9, ±4%) | 249 (247–278, ±0%) | ycs 4.6× | 57.9 / 271 | equal, v1 bytes identical |
| `array-push-1txn` | 22.1 (21.7–22.8, ±1%) | 67577 (67269–67687, ±0%) | ycs 3061.2× | 24.7 / 67577 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 4.2 (4.1–4.4, ±3%) | 2266 (2210–2339, ±1%) | ycs 538.0× | 5.2 / 2247 | equal, v1 bytes identical |
| `array-insert-random` | 201 (194–231, ±5%) | 20658 (17851–27439, ±20%) | ycs 102.8× | 231 / 19872 | equal, v1 bytes identical |
| `array-delete-random` | 211 (200–243, ±6%) | 26440 (26027–26630, ±2%) | ycs 125.4× | 225 / 26440 | equal, v1 bytes identical |
| `read-text` | 0.4 (0.3–0.5, ±12%) | 46.3 (44.8–49.4, ±4%) | ycs 128.4× | 0.4 / 45.0 | equal, v1 bytes identical |
| `read-array` | 5.3 (5.1–5.6, ±4%) | 715 (617–752, ±1%) | ycs 136.0× | 5.6 / 674 | equal, v1 bytes identical |
| `read-map` | 36.5 (35.4–42.4, ±8%) | 1190 (1150–1204, ±2%) | ycs 32.6× | 36.0 / 1194 | equal, v1 bytes identical |
| `undo-type` | 30.6 (28.0–44.2, ±9%) | 180 (176–204, ±2%) | ycs 5.9× | 64.4 / 182 | equal, v1 bytes identical |
| `undo-all` | 37.6 (36.6–40.2, ±4%) | 364 (317–384, ±3%) | ycs 9.7× | 54.1 / 344 | equal, v1 bytes identical |
| `redo-all` | 44.6 (43.6–48.7, ±3%) | 83.9 (82.6–97.9, ±0%) | ycs 1.9× | 50.1 / 86.1 | equal, v1 bytes identical |
| `observe-count` | 112 (109–118, ±3%) | 3616 (3597–3713, ±0%) | ycs 32.3× | 126 / 3656 | equal, v1 bytes identical |
| `observe-delta` | 169 (166–174, ±2%) | 5642 (5556–5749, ±0%) | ycs 33.4× | 185 / 5758 | equal, v1 bytes identical |

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
| `concurrent-20` | 14.2 | 0.1 | 14.2 + 0.0 = 14.2 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `many-clients-200` | 0.4 | 0.0 | 0.6 + 0.0 = 0.6 | 0.1 + 0.5 = 0.6 | 34,878 / 34,878 | 20,223 / 20,223 |
| `many-clients-1000` | 1.3 | 0.0 | 2.1 + 0.0 = 2.1 | 0.2 + 1.9 = 2.1 | 149,755 / 149,755 | 93,984 / 93,984 |
| `apply-state-v1/ycs` | 38.1 | 0.0 | 2.9 + 0.0 = 2.9 | 0.3 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 38.1 | 0.0 | 2.9 + 0.0 = 2.9 | 0.3 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 38.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 38.1 | 0.0 | 2.8 + 0.0 = 2.8 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-x100/ycs` | 479.7 | 0.0 | 253.0 + 0.0 = 253.0 | 29.9 + 229.2 = 259.0 | 31,264,816 / 31,264,816 | 15,989,045 / 15,989,045 |
| `apply-updates/ycs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.6 + 2.2 = 16.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 11.9 | 9.9 | 17.2 + 0.0 = 17.2 | 14.6 + 2.2 = 16.8 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 18.2 | 0.0 | 27.6 + 0.0 = 27.6 | 14.6 + 6.0 = 20.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 1.6 + 0.0 = 1.6 | 0.0 + 2.2 = 2.2 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 2.3 + 0.0 = 2.3 | 0.0 + 2.2 = 2.2 | 289,132 / 289,132 | 159,927 / 159,927 |
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
| `read-text` | 0.2 | 20.0 | 2.9 + 0.0 = 2.9 | 0.3 + 2.2 = 2.5 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 229.0 | 4.0 + 0.0 = 4.0 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 359.0 | 20.1 + 0.0 = 20.1 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 16.4 | 4.6 | 11.5 + 0.0 = 11.5 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 24.8 | 0.0 | 12.1 + 0.0 = 12.1 | 0.2 + 7.6 = 7.7 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 30.6 | 0.0 | 17.3 + 0.0 = 17.3 | 0.3 + 15.3 = 15.5 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 17.8 | 66.4 | 1.0 + 0.0 = 1.0 | 0.3 + 0.1 = 0.4 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 73.5 | 159.6 | 1.0 + 0.0 = 1.0 | 0.3 + 0.1 = 0.4 | 100,018 / 100,018 | 100,033 / 100,033 |
