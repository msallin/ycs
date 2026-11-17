Run 2026-11-17 23:20 UTC; bench/Ycs.Layout small at the end of the modernisation (7d994f4 with the change log allocated on first use); byte counts, independent of the load; load average 1.33 1.29 0.77


| document | documents | structs each | today, B each | designed, fixed pages | designed, geometric pages | geometric / today |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| empty, a root text | 1,000 | 0 | 1,344 | 1,344 | 1,344 | 1.00 |
| text "hello" (one insert) | 1,000 | 1 | 3,305 | 194,336 | 3,296 | 1.00 |
| text, 20 characters typed at random positions, 2 deleted | 1,000 | 20 | 7,096 | 194,560 | 4,480 | 0.63 |
| text, 200 characters typed at random positions, 20 deleted | 1,000 | 198 | 27,496 | 196,352 | 19,712 | 0.72 |
| text, 1,000 characters typed at random positions, 100 deleted | 200 | 999 | 94,536 | 202,496 | 75,056 | 0.79 |
| text, 5,000 characters typed at random positions, 500 deleted | 40 | 5,000 | 430,809 | 505,601 | 390,529 | 0.91 |
| text, 20,000 characters typed at random positions, 2,000 deleted | 10 | 19,999 | 1,686,626 | 1,623,813 | 1,557,941 | 0.92 |
| empty, a root map | 1,000 | 0 | 1,208 | 1,208 | 1,208 | 1.00 |
| map, 20 keys (an integer each) | 1,000 | 20 | 8,067 | 195,840 | 5,760 | 0.71 |
| map, 1,000 keys | 100 | 1,000 | 186,122 | 265,376 | 150,304 | 0.81 |

Many clients (20,000, one map entry each, in one document), per client: today 605 B, designed 351 B (fixed pages), 348 B (geometric pages).
all checks passed
