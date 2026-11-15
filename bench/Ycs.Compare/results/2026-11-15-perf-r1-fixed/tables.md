Run 2026-11-15 17:56 UTC: 3 passes, each workload in its own process per library and pass, warm-up runs until the last 3 are within 10% of each other (at least 3 runs and 2 s, at most 200 runs or 30 s), then 5 timed runs per process (a heavy workload: 1 run, no warm-up); inputs B0C9A1DE44B7103F.

Machine: AMD EPYC-Genoa Processor, 8 logical cores, 32859788 kB RAM, Ubuntu 26.04 LTS, kernel 7.0.0.43, .NET 11.0.0, YDotNet 0.7.0+c14ed5174ba69959ff7ba22a256bec3e455be8c7. Load average at start: 6.26 5.00 4.80 2/1947 3523459.

YDotNet was not run again except for `text-append`, `map-set-distinct`, `apply-state-v1/ycs`, `encode-v1`: its other measurements, marked (carried), and the content and states the checks read are those of results/2026-11-15.

Time: median of all timed runs in ms (min–max of the runs; ± the spread of the per-pass medians around their median); which is faster, by the ratio of the medians; the median over the passes of the first (cold, not yet JIT-optimized) run in each process.

| workload | ycs | YDotNet | faster | first run ycs / YDotNet | checks |
| --- | ---: | ---: | --- | ---: | --- |
| `text-append` | 17.7 (16.8–18.1, ±5%) | 3118 (2981–3328, ±4%) | ycs 176.6× | 191 / 3212 | equal, v1 bytes identical |
| `text-append-utf8` | 18.1 (16.9–59.0, ±156%) | 1563 (1482–1767, ±5%) (carried) | ycs 86.2× | 202 / 1585 | equal, v1 bytes identical |
| `text-append-1txn` | 6.0 (5.5–10.2, ±7%) | 18154 (17775–18471, ±2%) (carried) | ycs 3014.6× | 81.3 / 18154 | equal, v1 bytes identical |
| `text-append-1txn-25k` | 1.5 (1.4–1.8, ±4%) | 1084 (1029–1112, ±4%) (carried) | ycs 699.9× | 40.4 / 1070 | equal, v1 bytes identical |
| `txn-empty` | 5.4 (5.3–5.9, ±7%) | 13.8 (13.3–14.2, ±4%) (carried) | ycs 2.5× | 50.0 / 18.0 | equal, v1 bytes identical |
| `text-random` | 25.1 (23.1–29.9, ±8%) | 1512 (1417–1626, ±2%) (carried) | ycs 60.3× | 230 / 1514 | equal, v1 bytes identical |
| `text-random-100k` | 1400 (1084–2257, ±61%) | 175337 (146416–202685, ±16%) (carried) | ycs 125.2× | 1400 / 175337 | equal, v1 bytes identical |
| `trace-replay` | 252 (227–367, ±32%) | 8053 (7293–8783, ±7%) (carried) | ycs 32.0× | 636 / 7832 | equal, v1 bytes identical |
| `concurrent-20` | 61.1 (58.2–78.7, ±2%) | 65.8 (60.5–68.5, ±6%) (carried) | ycs 1.1× | 385 / 81.0 | equal, v1 bytes differ |
| `apply-state-v1/ycs` | 59.2 (45.8–73.3, ±2%) | 39.3 (38.0–53.3, ±9%) | YDotNet 1.5× | 365 / 47.8 | equal, v1 bytes identical |
| `apply-state-v1/yrs` | 56.8 (45.9–58.0, ±1%) | 39.9 (37.3–57.0, ±13%) (carried) | YDotNet 1.4× | 354 / 50.3 | equal, v1 bytes identical |
| `apply-state-v2/ycs` | 53.7 (49.9–70.4, ±3%) | 40.3 (38.9–43.1, ±6%) (carried) | YDotNet 1.3× | 321 / 49.7 | equal, v1 bytes identical |
| `apply-state-v2/yrs` | 53.0 (49.6–60.5, ±2%) | 40.6 (39.7–43.3, ±5%) (carried) | YDotNet 1.3× | 350 / 50.7 | equal, v1 bytes identical |
| `apply-updates/ycs` | 244 (239–274, ±8%) | 261 (244–305, ±6%) (carried) | ycs 1.1× | 1031 / 265 | equal, v1 bytes identical |
| `apply-updates/yrs` | 246 (242–266, ±1%) | 256 (242–262, ±0%) (carried) | ycs 1.0× | 1050 / 258 | equal, v1 bytes identical |
| `apply-updates-1txn/yrs` | 139 (132–154, ±8%) | 1164 (1121–1245, ±8%) (carried) | ycs 8.4× | 771 / 1200 | equal, v1 bytes identical |
| `encode-v1` | 4.8 (4.6–4.9, ±3%) | 6.4 (6.1–6.8, ±0%) | ycs 1.3× | 47.0 / 7.1 | equal, v1 bytes identical |
| `encode-v2` | 6.4 (6.3–6.9, ±4%) | 7.7 (7.1–10.4, ±7%) (carried) | ycs 1.2× | 45.3 / 9.0 | equal, v1 bytes identical |
| `encode-sv` | 0.1 (0.1–0.1, ±12%) | 0.2 (0.2–0.3, ±7%) (carried) | ycs 3.3× | 4.0 / 1.2 | equal, v1 bytes identical |
| `encode-diff` | 2.7 (2.6–2.9, ±3%) | 3.3 (3.2–3.6, ±5%) (carried) | ycs 1.2× | 30.3 / 4.4 | equal, v1 bytes identical |
| `map-set-distinct` | 38.7 (34.4–44.8, ±3%) | 119 (109–151, ±7%) | ycs 3.1× | 227 / 179 | equal, v1 bytes identical |
| `map-set-distinct-1txn` | 32.5 (25.4–48.2, ±5%) | 106 (90.8–141, ±11%) (carried) | ycs 3.3× | 155 / 187 | equal, v1 bytes identical |
| `map-set-same` | 35.2 (31.1–36.6, ±12%) | 103 (95.5–109, ±7%) (carried) | ycs 2.9× | 299 / 128 | equal, v1 bytes identical |
| `array-push` | 19.0 (18.0–25.1, ±4%) | 92.5 (80.8–106, ±12%) (carried) | ycs 4.9× | 173 / 136 | equal, v1 bytes identical |
| `array-push-1txn` | 7.5 (6.8–12.1, ±2%) | 50769 (47708–57764, ±14%) (carried) | ycs 6805.9× | 59.4 / 50769 | equal, v1 bytes identical |
| `array-push-1txn-20k` | 1.4 (1.3–1.5, ±5%) | 1641 (1561–1736, ±4%) (carried) | ycs 1178.3× | 21.8 / 1619 | equal, v1 bytes identical |
| `array-insert-random` | 167 (128–301, ±62%) | 18023 (13758–33397, ±27%) (carried) | ycs 107.8× | 512 / 20544 | equal, v1 bytes identical |
| `array-delete-random` | 62173 (53768–63053, ±14%) | 25511 (24529–31255, ±23%) (carried) | YDotNet 2.4× | 62173 / 25511 | equal, v1 bytes identical |
| `read-text` | 0.4 (0.2–0.5, ±34%) | 41.9 (38.6–55.1, ±8%) (carried) | ycs 104.0× | 4.5 / 47.1 | equal, v1 bytes identical |
| `read-array` | 4.7 (4.6–5.7, ±2%) | 65.3 (62.6–74.9, ±6%) (carried) | ycs 13.9× | 35.3 / 116 | equal, v1 bytes identical |
| `read-map` | 14.8 (14.4–16.8, ±4%) | 341 (334–359, ±5%) (carried) | ycs 23.0× | 122 / 666 | equal, v1 bytes identical |
| `undo-type` | 21.9 (20.7–30.8, ±4%) | 134 (132–166, ±2%) (carried) | ycs 6.1× | 117 / 144 | equal, v1 bytes identical |
| `undo-all` | 34.1 (27.6–44.3, ±20%) | 206 (196–242, ±3%) (carried) | ycs 6.0× | 122 / 202 | equal, v1 bytes identical |
| `redo-all` | 76.0 (69.3–79.8, ±7%) | 52.1 (50.1–55.1, ±3%) (carried) | YDotNet 1.5× | 186 / 55.4 | equal, v1 bytes identical |
| `observe-count` | 36.8 (30.1–41.9, ±14%) | 3069 (2954–3371, ±4%) (carried) | ycs 83.3× | 309 / 3123 | equal, v1 bytes identical |
| `observe-delta` | 56.5 (50.4–66.9, ±19%) | 4619 (4502–4813, ±3%) (carried) | ycs 81.8× | 347 / 4556 | equal, v1 bytes identical |

Memory: MiB allocated on the managed heap per timed run (median; YDotNet's native allocations are not in it), MiB held by the finished document after a full GC (managed + native C heap; ±1 MiB is noise), and the bytes of the final state as each library encodes it.

| workload | alloc ycs | alloc YDotNet | held ycs (managed + native) | held YDotNet (managed + native) | state v1 ycs / yrs | state v2 ycs / yrs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `text-append` | 0.2 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + 0.4 = 0.6 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-utf8` | 0.2 | 11.4 | 1.3 + 0.0 = 1.3 | 0.2 + 0.3 = 0.5 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn` | 0.2 | 8.2 | 2.1 + 0.0 = 2.1 | 1.8 + 2.6 = 4.3 | 100,018 / 100,018 | 100,033 / 100,033 |
| `text-append-1txn-25k` | 0.1 | 1.9 | 0.5 + 0.0 = 0.5 | 0.3 + 0.7 = 0.9 | 25,018 / 25,018 | 25,033 / 25,033 |
| `txn-empty` | 0.0 | 3.8 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 2 / 2 | 13 / 13 |
| `text-random` | 3.9 | 2.2 | 5.2 + 0.0 = 5.2 | 0.4 + 4.0 = 4.4 | 298,994 / 298,994 | 111,571 / 111,571 |
| `text-random-100k` | 18.8 | 12.0 | 26.3 + 2.0 = 28.4 | 3.3 + 19.2 = 22.5 | 1,605,864 / 1,605,864 | 646,240 / 646,240 |
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
| `map-set-distinct` | 36.8 | 21.3 | 38.2 + 0.0 = 38.2 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-distinct-1txn` | 39.5 | 17.6 | 38.2 + 0.0 = 38.2 | 2.9 + 28.0 = 30.9 | 1,980,644 / 1,980,644 | 1,680,665 / 1,680,665 |
| `map-set-same` | 29.0 | 21.4 | 0.0 + 0.0 = 0.0 | 0.0 + 0.0 = 0.0 | 43 / 43 | 52 / 52 |
| `array-push` | 10.4 | 35.9 | 9.4 + 0.0 = 9.4 | 5.2 + 3.0 = 8.2 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn` | 10.4 | 32.9 | 9.4 + 0.0 = 9.4 | 5.2 + 4.9 = 10.0 | 391,763 / 391,763 | 391,774 / 391,774 |
| `array-push-1txn-20k` | 2.2 | 6.4 | 1.7 + 0.0 = 1.7 | 0.7 + 0.1 = 0.9 | 71,763 / 71,763 | 71,774 / 71,774 |
| `array-insert-random` | 17.0 | 17.9 | 18.6 + 1.0 = 19.5 | 2.6 + 15.5 = 18.1 | 920,413 / 920,413 | 449,411 / 449,411 |
| `array-delete-random` | 32.4 | 5.5 | 19.4 + 3.3 = 22.8 | 2.8 + 12.1 = 14.9 | 734,672 / 734,672 | 432,303 / 432,303 |
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
| `alloc-struct-count` | 6 | rejected: YUpdateException; unchanged; 4 ms, 239 MiB | rejected: EndOfStream; unchanged; 12 ms, 240 MiB |
| `alloc-struct-count-below-heap-limit` | 5 | rejected: YUpdateException; unchanged; 3 ms, 238 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `alloc-any-length` | 11 | rejected: YUpdateException; unchanged; 4 ms, 238 MiB | rejected: EndOfStream; unchanged; 9 ms, 240 MiB |
| `alloc-json-length` | 12 | rejected: YUpdateException; unchanged; 4 ms, 238 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `nonatomic-truncated-delete-set` | 35 | rejected: YUpdateException; unchanged; 6 ms, 239 MiB | rejected: EndOfStream; unchanged; 13 ms, 240 MiB |
| `nonatomic-truncated-v2` (v2) | 26 | rejected: YUpdateException; unchanged; 24 ms, 239 MiB | rejected: EndOfStream; unchanged; 9 ms, 240 MiB |
| `nonatomic-malformed-delete-set` | 12 | rejected: YUpdateException; unchanged; 17 ms, 239 MiB | rejected: EndOfStream; unchanged; 13 ms, 241 MiB |
| `nonatomic-negative-delete-length` | 13 | rejected: YUpdateException; unchanged; 22 ms, 245 MiB | applied; readable; 11 ms, 240 MiB |
| `nonatomic-find-index-out-of-range` | 54 | rejected: YUpdateException; unchanged; 6 ms, 239 MiB | **crash: died with signal 6 (SIGABRT): thread '<unnamed>' (3540687) panicked at yrs/src/block_store.rs:53:30:** |
| `nonatomic-zero-length-struct` | 10 | rejected: YUpdateException; unchanged; 5 ms, 238 MiB | rejected: EndOfStream; unchanged; 8 ms, 240 MiB |
| `nonatomic-find-index-unexpected` | 11 | rejected: YUpdateException; unchanged; 5 ms, 238 MiB | applied; readable; 10 ms, 240 MiB |
| `nonatomic-clean-end-unexpected` (v2) | 68 | rejected: YUpdateException; unchanged; 6 ms, 239 MiB | rejected: EndOfStream; unchanged; 13 ms, 241 MiB |
| `nonatomic-any-splice-out-of-range` (v2) | 141 | rejected: YUpdateException; unchanged; 9 ms, 239 MiB | applied; readable; 11 ms, 240 MiB |
| `truncated-state` | 507 | rejected: YUpdateException; unchanged; 6 ms, 239 MiB | rejected: EndOfStream; unchanged; 12 ms, 241 MiB |
| `gc-bomb-pending-2M` (v2) | 24 | rejected: YUpdateException; unchanged; 5 ms, 238 MiB | applied; readable; 61 ms, 302 MiB |
| `deleted-items-524288` (v2) | 42 | rejected: YUpdateException; unchanged; 7 ms, 239 MiB | applied; readable; 191 ms, 350 MiB |
| `conflict-2000` | 10,006 | applied; readable; 116 ms, 240 MiB | applied; readable; 230 ms, 241 MiB |
