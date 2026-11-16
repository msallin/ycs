# Ycs.Parity

Not a test: a byte-parity harness for the update operations. It writes one line per output, `<input> <operation> <SHA-256
of the bytes, or EX:<exception type>>`, for the 110 corpus cases (`interop/corpus/cases`) and the structure-aware update
fuzzer (`tests/Ycs.Properties/UpdateFuzz.cs`, seeds 1 to N):

* `MergeUpdates`, `MergeUpdatesV2` (forward, reversed, pairwise);
* `DiffUpdate`, `DiffUpdateV2` against the state vector of every prefix, applied in order and reversed;
* the v1 to v2 and v2 to v1 conversions of every update;
* the encodings of documents with structs pending (updates applied in order, reversed, every other one, all but the
  first), as v1 and v2, with and without a state vector.

A refactoring that must not change a byte is checked by running it on the base and on the change and comparing:

```bash
dotnet build bench/Ycs.Parity -c Release -o /path/to/scratch/base      # on the base commit
dotnet /path/to/scratch/base/Ycs.Parity.dll base.txt 4000 interop/corpus/cases
dotnet build bench/Ycs.Parity -c Release -o /path/to/scratch/change    # on the change
dotnet /path/to/scratch/change/Ycs.Parity.dll change.txt 4000 interop/corpus/cases
cmp base.txt change.txt && echo identical
```

With 4,000 seeds the file has 707,144 lines and a run takes a few seconds. The output is deterministic.
