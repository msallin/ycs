Run 2026-11-15 10:52 UTC: 3 passes, each workload in its own process per library and pass, 3 warm-up + 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs B0C9A1DE44B7103F.

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 32859788 kB RAM, Ubuntu 26.04 LTS, kernel 7.0.0.43, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 1.89 2.09 2.19 3/715 2991688.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 25.0 (22.3–67.4, ±164%) | 3081 (2967–3197, ±1%) | ycs 123.2× | 252 / 3046 | equal, v1 bytes identical |
| `text-append-utf8` | 22.2 (21.3–52.1, ±72%) | 1563 (1482–1767, ±5%) | ycs 70.3× | 236 / 1585 | equal, v1 bytes identical |
| `text-append-1txn` | 58053 (53458–60796, ±8%) | 18154 (17775–18471, ±2%) | YDotNet 3.2× | 58053 / 18154 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 3319 (3084–3470, ±5%) | 1084 (1029–1112, ±4%) | YDotNet 3.1× | 3716 / 1070 | equal, v1 bytes identical |
| `txn-empty` | 30.2 (29.7–33.7, ±7%) | 13.8 (13.3–14.2, ±4%) | YDotNet 2.2× | 43.3 / 18.0 | equal, v1 bytes identical |
| `text-random` | 25.9 (22.9–80.4, ±196%) | 1512 (1417–1626, ±2%) | ycs 58.4× | 225 / 1514 | equal, v1 bytes identical |
| `text-random-100k` | 1892 (1517–2276, ±20%) | 175337 (146416–202685, ±16%) | ycs 92.7× | 1892 / 175337 | equal, v1 bytes identical |
| `trace-replay` | 260 (249–284, ±6%) | 8053 (7293–8783, ±7%) | ycs 31.0× | 731 / 7832 | equal, v1 bytes identical |
| `concurrent-20` | 68.5 (61.3–94.8, ±2%) | 65.8 (60.5–68.5, ±6%) | YDotNet 1.0× | 430 / 81.0 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 70.4 (62.3–143, ±17%) | 38.8 (38.2–42.0, ±7%) | YDotNet 1.8× | 415 / 46.3 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 70.2 (63.7–146, ±7%) | 39.9 (37.3–57.0, ±13%) | YDotNet 1.8× | 401 / 50.3 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 72.9 (67.1–107, ±6%) | 40.3 (38.9–43.1, ±6%) | YDotNet 1.8× | 380 / 49.7 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 71.0 (68.2–110, ±3%) | 40.6 (39.7–43.3, ±5%) | YDotNet 1.7× | 388 / 50.7 | equal, v1 bytes identical |
| `apply-updates/ycs` | 237 (228–265, ±4%) | 261 (244–305, ±6%) | ycs 1.1× | 1038 / 265 | equal, v1 bytes identical |
| `apply-updates/yrs` | 234 (225–319, ±9%) | 256 (242–262, ±0%) | ycs 1.1× | 1057 / 258 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 136 (126–148, ±5%) | 1164 (1121–1245, ±8%) | ycs 8.6× | 844 / 1200 | equal, v1 bytes identical |
| `encode-v1` | 45.6 (12.4–57.5, ±8%) | 6.3 (6.1–6.9, ±5%) | YDotNet 7.2× | 44.2 / 7.5 | equal, v1 bytes identical |
| `encode-v2` | 23.4 (13.6–56.6, ±48%) | 7.7 (7.1–10.4, ±7%) | YDotNet 3.1× | 44.9 / 9.0 | equal, v1 bytes identical |
| `encode-sv` | 0.3 (0.2–0.3, ±9%) | 0.2 (0.2–0.3, ±7%) | YDotNet 1.1× | 4.0 / 1.2 | equal, v1 bytes identical |
| `encode-diff` | 16.8 (15.1–29.0, ±17%) | 3.3 (3.2–3.6, ±5%) | YDotNet 5.1× | 32.5 / 4.4 | equal, v1 bytes identical |
| `map-set-distinct` | 48.7 (41.0–58.0, ±7%) | 125 (107–187, ±10%) | ycs 2.6× | 283 / 185 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 36.5 (27.5–123, ±13%) | 106 (90.8–141, ±11%) | ycs 2.9× | 157 / 187 | equal, v1 bytes identical |
| `map-set-same` | 50.3 (39.9–67.1, ±17%) | 103 (95.5–109, ±7%) | ycs 2.1× | 293 / 128 | equal, v1 bytes identical |
| `array-push` | 40.7 (35.9–47.0, ±11%) | 92.5 (80.8–106, ±12%) | ycs 2.3× | 299 / 136 | equal, v1 bytes identical |
| `array-push-1txn` | **killed at 8229 MiB resident (limit 8192 MiB) after 36.1 s** | 50769 (47708–57764, ±14%) | not counted | - / 50769 | a run produced no content |
| `array-push-1txn-20k` | 1164 (967–1437, ±5%) | 1641 (1561–1736, ±4%) | ycs 1.4× | 2114 / 1619 | equal, v1 bytes identical |
| `array-insert-random` | 198 (178–410, ±81%) | 18023 (13758–33397, ±27%) | ycs 91.2× | 552 / 20544 | equal, v1 bytes identical |
| `array-delete-random` | 62220 (60720–66908, ±8%) | 25511 (24529–31255, ±23%) | YDotNet 2.4× | 62220 / 25511 | equal, v1 bytes identical |
| `read-text` | 0.3 (0.2–0.5, ±32%) | 41.9 (38.6–55.1, ±8%) | ycs 136.7× | 4.0 / 47.1 | equal, v1 bytes identical |
| `read-array` | 32.2 (4.5–41.5, ±86%) | 65.3 (62.6–74.9, ±6%) | ycs 2.0× | 36.4 / 116 | equal, v1 bytes identical |
| `read-map` | 16.5 (16.0–22.5, ±2%) | 341 (334–359, ±5%) | ycs 20.6× | 112 / 666 | equal, v1 bytes identical |
| `undo-type` | 51.7 (46.9–97.1, ±25%) | 134 (132–166, ±2%) | ycs 2.6× | 104 / 144 | equal, v1 bytes identical |
| `undo-all` | 30.5 (26.9–39.6, ±22%) | 206 (196–242, ±3%) | ycs 6.7× | 109 / 202 | equal, v1 bytes identical |
| `redo-all` | 75.7 (69.6–92.2, ±7%) | 52.1 (50.1–55.1, ±3%) | YDotNet 1.5× | 168 / 55.4 | equal, v1 bytes identical |
| `observe-count` | 28.1 (27.1–129, ±114%) | 3069 (2954–3371, ±4%) | ycs 109.0× | 279 / 3123 | equal, v1 bytes identical |
| `observe-delta` | 48.9 (45.4–57.2, ±12%) | 4619 (4502–4813, ±3%) | ycs 94.4× | 337 / 4556 | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 19.3 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + 0.4 = 0.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 19.3 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + 0.3 = 0.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 21.7 | 8.2 | 4.3 + 1.5 = 5.7 | 1.8 + 2.6 = 4.3 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 5.4 | 1.9 | 0.9 + 1.1 = 2.0 | 0.3 + 0.7 = 0.9 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 4.3 | 2.2 | 5.6 + 0.0 = 5.7 | 0.4 + 4.0 = 4.4 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 21.1 | 12.0 | 28.6 + 1.6 = 30.2 | 3.3 + 19.2 = 22.5 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
| `trace-replay` | 84.8 | 28.5 | 3.0 + 0.0 = 3.0 | -15.8 + 2.3 = -13.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `concurrent-20` | 31.5 | 0.1 | 30.5 + 0.0 = 30.5 | 0.1 + 25.3 = 25.4 | 69,168 / 69,168 | 58,101 / 58,101 |
| `apply-state-v1/ycs` | 39.5 | 0.0 | 4.6 + 0.0 = 4.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v1/yrs` | 39.5 | 0.0 | 4.6 + 0.0 = 4.7 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/ycs` | 41.5 | 0.0 | 4.5 + 0.0 = 4.5 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-state-v2/yrs` | 41.5 | 0.0 | 4.5 + 0.0 = 4.5 | 0.4 + 2.2 = 2.6 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/ycs` | 67.2 | 9.9 | 18.6 + 0.0 = 18.6 | 14.8 + 2.2 = 17.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates/yrs` | 67.2 | 9.9 | 18.6 + 0.0 = 18.6 | 14.8 + 2.2 = 17.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `apply-updates-1txn/yrs` | 52.0 | 0.0 | 20.8 + 0.0 = 20.8 | 14.8 + 6.2 = 21.0 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v1` | 2.8 | 2.8 | 3.4 + 0.0 = 3.4 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-v2` | 1.5 | 1.5 | 4.0 + -0.0 = 4.0 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-sv` | 0.0 | 0.1 | 4.4 + 0.0 = 4.4 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `encode-diff` | 1.4 | 1.4 | 3.9 + 0.0 = 3.9 | 0.2 + 2.2 = 2.4 | 289,132 / 289,132 | 159,927 / 159,927 |
| `map-set-distinct` | 52.8 | 21.4 | 40.5 + 0.0 = 40.5 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 41.8 | 17.6 | 40.5 + 0.0 = 40.5 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 53.4 | 21.4 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 47.0 | 35.9 | 9.4 + 0.0 = 9.5 | 5.2 + 3.0 = 8.2 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | | | | | | |
| `array-push-1txn-20k` | 1535.9 | 6.4 | 1.8 + 1.2 = 3.0 | 0.7 + 0.1 = 0.9 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 23.5 | 17.9 | 19.7 + 0.0 = 19.7 | 2.6 + 15.5 = 18.1 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 38.3 | 5.5 | 20.6 + 3.2 = 23.8 | 2.8 + 12.1 = 14.9 | 734,672 / 734,672 | 432,303 / 432,303 |
| `read-text` | 0.2 | 20.0 | 4.3 + 0.0 = 4.3 | 0.5 + 2.2 = 2.7 | 289,132 / 289,132 | 159,927 / 159,927 |
| `read-array` | 0.0 | 106.8 | 4.3 + 0.0 = 4.3 | 0.0 + 3.0 = 3.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `read-map` | 0.0 | 175.5 | 35.7 + 0.0 = 35.7 | 0.0 + 28.0 = 28.0 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `undo-type` | 21.5 | 2.3 | 11.7 + 0.0 = 11.7 | 0.3 + 5.3 = 5.6 | 20,018 / 20,018 | 20,033 / 20,033 |
| `undo-all` | 30.3 | 0.0 | 11.8 + 0.0 = 11.8 | 0.2 + 7.6 = 7.7 | 20,027 / 20,027 | 20,042 / 20,042 |
| `redo-all` | 36.1 | 0.0 | 21.4 + 0.0 = 21.4 | 0.3 + 15.3 = 15.6 | 490,475 / 490,475 | 40,073 / 40,073 |
| `observe-count` | 39.1 | 57.2 | 1.5 + 0.0 = 1.5 | 0.5 + 1.0 = 1.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `observe-delta` | 84.9 | 130.5 | 1.5 + 0.0 = 1.5 | 0.5 + 0.2 = 0.7 | 100,018 / 100,018 | 100,033 / 100,033 |

Malformed and hostile updates, each applied in a child process per library (30 s and 4 GiB resident limit): the outcome, whether the document is still readable and encodable afterwards, whether its state is unchanged, the time, and the child's peak resident set.

| case | bytes | ycs | YDotNet |
| --- | ---: | --- | --- |
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 4 ms, 265 MiB | rejected: EndOfStream; unchanged; 12 ms, 267 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 3 ms, 265 MiB | rejected: EndOfStream; unchanged; 8 ms, 267 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 4 ms, 265 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 4 ms, 265 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 6 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 20 ms, 266 MiB | rejected: EndOfStream; unchanged; 8 ms, 267 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 16 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 18 ms, 271 MiB | applied; readable; 10 ms, 267 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 6 ms, 265 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (3015792) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 4 ms, 265 MiB | rejected: EndOfStream; unchanged; 7 ms, 267 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 5 ms, 265 MiB | applied; readable; 10 ms, 267 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 6 ms, 265 MiB | rejected: EndOfStream; unchanged; 12 ms, 267 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 8 ms, 265 MiB | applied; readable; 10 ms, 267 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 5 ms, 265 MiB | rejected: EndOfStream; unchanged; 11 ms, 267 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 5 ms, 265 MiB | applied; readable; 58 ms, 329 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 6 ms, 265 MiB | applied; readable; 190 ms, 377 MiB |
| `conflict-2000` | 10,006 | applied; readable; 118 ms, 267 MiB | applied; readable; 214 ms, 268 MiB |
