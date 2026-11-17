Layout 8.12 (measurements for steps 8 and 9), 2026-11-17. ab.py 2 3 (bench/Ycs.Compare/ab.py): "base" is modernize 044bb73 (items as
objects, built from `git archive 044bb73`), "new" the branch at 12e5ea7 (the staging read, the arenas, the 8.13 changes).
  ab-held-alloc.txt   allocation per run and held memory of every workload both builds have; the two lines that FAILED are
                      many-clients-200 and apply-state-x100/ycs on the base build, whose bench does not have them.
THE TIMINGS IN THIS FILE ARE NOT FROM A QUIET MACHINE: the load average on the lines is 4 to 15 (other agents built and tested at
the same time). Read only the allocation and held-memory columns, which are deterministic; the timings of the same workloads
are in ../2026-11-17-8-13/ (load 0.75 to 4) and a clean round is still to come.
Also in bench/Ycs.Layout/results/: 2026-11-17-8-12-small.md (the built layout) and 2026-11-17-8-12-small-044bb73.md (the object layout).
