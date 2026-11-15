ab.py output (bench/Ycs.Compare/ab.py), the ycs child of b7f02f3 ("base") and of the commit that adds the break counts
per block ("new"), built in directories of their own; WORK is a suite --work directory.
  observed-before.txt  ONLY=base ab.py 2 3 text-append-1txn-observed text-append-1txn-observed-200k text-append-1txn-observed-400k
  observed-after.txt   ONLY=new  ab.py 2 3 (the same, and -25k)
  apply-trace.txt      ab.py 4 6 apply-updates/ycs trace-replay
