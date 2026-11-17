Layout 8.13 (the performance pass), 2026-11-17. ab.py 4 6 (bench/Ycs.Compare/ab.py); "base" is modernize 044bb73 (items as objects)
unless a file says otherwise, "new" the build named by the file. The load average is on every line.
  ab-4x6-before.txt            044bb73 against modernize 359f0a3, no change (load 1.2 to 4.8: the machine was shared)
  ab-4x6-write-and-read-array.txt  359f0a3 (base) against 359f0a3 + the first two commits (encode-v1, read-array, array-push)
  ab-4x6-marker-walk.txt       359f0a3 (base) against the branch after the marker-walk commits (the observed and the random workloads)
  ab-4x6-final.txt             044bb73 against the branch head (load 0.75 at the start, 1.7 to 4.0 on the lines)
The in-process harness that found the marker-walk cost (a copy of the typing loops, timed with Stopwatch, not committed)
is described in the README's "Step 8 and 9" section.
