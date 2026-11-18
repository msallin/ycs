# Decoders as structs over the input span (2026-11-18)

`ab.py 3 4` (3 rounds of 4 runs, interleaved) of `ef6bbb9`, the tree before the decoders became structs and
`ApplyUpdate` read in place, against `53dc85f`; both built from a `git archive` into a folder of their own. The ycs side
of `apply-state-v1`, `apply-state-v2`, `apply-updates`, `trace-replay` and `encode-diff`. **The machine was not quiet**
(8 logical cores shared with other work; load average 6 to 16 while the A/B ran), so differences of a few percent are noise.

| workload | ef6bbb9 | 53dc85f | alloc MiB (both) |
| --- | ---: | ---: | ---: |
| `apply-state-v1/ycs` | 59.2 ms (min 55.5) | 57.9 ms (min 54.6) | 38.08 |
| `apply-state-v2/ycs` | 61.1 ms (min 57.3) | 54.6 ms (min 53.6) | 38.08 |
| `apply-updates/ycs` | 226.0 ms (min 218.0) | 240.1 ms (min 217.7) | 11.95 |
| `apply-updates/ycs`, repeated at load 7 | 231.6 ms (min 222.9) | 228.0 ms (min 219.7) | 11.95 |
| `trace-replay` | 251.2 ms (min 234.8) | 250.6 ms (min 243.7) | 24.86 |
| `encode-diff` | 3.2 ms | 3.2 ms | 1.39 |

Allocation and held memory are equal; the times are equal within the noise (the mins of `apply-updates` agree, the
first medians differ by the load of that round, the repeat does not).

`tests/Ycs.Budgets`, one run each, the `update.*` scenarios (bytes per operation, then µs per operation, which is
load-sensitive; every budget held):

| scenario | bytes ef6bbb9 | bytes 53dc85f | µs ef6bbb9 | µs 53dc85f |
| --- | ---: | ---: | ---: | ---: |
| `merge-10-large-v1` | 289,624 | 288,735 | 6,171 | 2,963 |
| `merge-two-states-100k-20k-v1` | 454,432 | 454,703 | 7,655 | 4,583 |
| `diff-100k-v1` | 352,977 | 352,821 | 3,495 | 3,295 |
| `convert-100k-v1` | 262,446 | 262,370 | 4,083 | 3,739 |
| `encode-state-vector-100k-v1` | 552 | 496 | 2,085 | 1,815 |
| `parse-meta-100k-v1` | 704 | 648 | 2,056 | 1,773 |
| `merge-10-large-v2` | 233,097 | 226,895 | 3,807 | 3,843 |
| `merge-two-states-100k-20k-v2` | 350,877 | 349,871 | 5,822 | 5,517 |
| `diff-100k-v2` | 263,216 | 262,800 | 4,009 | 3,661 |
| `convert-100k-v2` | 354,177 | 353,639 | 3,785 | 3,236 |
| `encode-state-vector-100k-v2` | 1,568 | 992 | 2,085 | 1,764 |
| `parse-meta-100k-v2` | 1,720 | 1,144 | 2,105 | 1,780 |

Files: `ab.txt` (the output, with the load average before and after each run and per child), `budgets-base.txt` and
`budgets-new.txt` (the outputs of the budget project, with the load before and after).
