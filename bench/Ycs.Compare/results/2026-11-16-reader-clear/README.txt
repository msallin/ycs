The reader's dropped lists cleared (UpdateReader.Release), 2026-11-16. "new" is the commit that clears them (on 48716a4);
"base" the build named. ab.py output (bench/Ycs.Compare/ab.py); WORK is a suite --work directory (fixtures of the trace).
  against-c9f673a.txt   base c9f673a (before the leaf order), new: ab.py 4 6 apply-state-v1/ycs apply-state-v2/ycs concurrent-20
  against-4f5eedb.txt   base 4f5eedb (the leaf order), new: the same
  gcsurvive.txt         dotnet run -c Release --project bench/Ycs.Benchmarks -- gcsurvive (c9f673a, 4f5eedb, new: "c9",
                        "base", "fix2"), then the review's probe (rv gcsurvive) on the trace's state, 4f5eedb and new
