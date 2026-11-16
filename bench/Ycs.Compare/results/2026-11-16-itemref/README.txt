ItemRef (layout steps 4a and 4b), 2026-11-16. ab.py output (bench/Ycs.Compare/ab.py); "base" is c263413 (modernize),
"new" the commit named; WORK is a suite --work directory (fixtures of the trace). The machine was shared: the load
average is on every line, and runs above a load of about 10 are noise (minimum and maximum far apart).
  first-4x6.txt             new 0515e7f (the layer, before any tuning): ab.py 4 6, the 14 workloads
  inline-4x6-loaded.txt     new a9bb153 (accessors inlined, a position read once per step): ab.py 4 6, load 3 to 90
  inline-observed-6x10.txt  new a9bb153: ab.py 6 10 on the observed typing, load 15 to 3
  final-4x6.txt             new 2e96e1f (the type test instead of the cast): ab.py 4 6, the 14 workloads, load 4 to 90
  final-rest-6x6-loaded.txt new 2e96e1f: ab.py 6 6, the workloads final-4x6 ran under load, again (load 80 to 10)
  final-quiet-6x6.txt       new 2e96e1f: ab.py 6 6, the observed typing, text-random and the array workloads, load 2 to 8
