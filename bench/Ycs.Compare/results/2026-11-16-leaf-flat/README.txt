A flat form below one leaf (design open question 6), 2026-11-16. Not kept: see bench/Ycs.Compare/README.md, "The leaf order".
prototype.patch                 the prototype, against 9e182ef: while a client has no directory, the indexer, both FindIndex,
                                Add and Refresh read cached arrays (structs, starts, flags) of the one leaf, not the leaf object
default-capacity.txt            128-position leaves, prototype against 9e182ef; ab.py 4 6 (the first pass of apply-updates failed
                                for its case name, apply-updates/ycs: it is in the second part)
single-leaf-of-any-size.txt     the best case: LeafCapacity 1 << 20 in both builds (every client one flat leaf), apply-state and concurrent
Shared machine, 8 cores, other builds running: the load of each case is on its line (1.3 to 8.4).
