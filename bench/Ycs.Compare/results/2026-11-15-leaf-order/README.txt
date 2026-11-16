ab.py output (bench/Ycs.Compare/ab.py); "base" and "new" are the ycs child of two builds, each in a directory of its own;
WORK is a suite --work directory (fixtures of the 2026-11-15 suite).
  fast.txt                   base c9f673a, new 42f0687: ab.py 4 6 apply-updates/ycs trace-replay apply-state-v1/ycs apply-state-v2/ycs concurrent-20 map-set-distinct text-random
  heavy.txt                  base c9f673a, new 42f0687: ab.py 2 3 array-delete-random array-insert-random text-random-100k text-append-1txn-observed-25k text-append-1txn-observed text-append-1txn-observed-200k text-append-1txn-observed-400k
  against-53a35de.txt        base 53a35de, new 42f0687: ab.py 4 6 apply-updates/ycs trace-replay
  array-insert-random-again.txt  base c9f673a, new 42f0687: ab.py 4 4 array-insert-random
  capacity-128-vs-64.txt     base a52934c, new a52934c with LeafCapacityForTests = 64: ab.py 3 6 apply-updates/ycs trace-replay, then ab.py 2 3 array-delete-random text-append-1txn-observed-400k text-random-100k
  capacity-128-vs-256.txt    the same with 256
