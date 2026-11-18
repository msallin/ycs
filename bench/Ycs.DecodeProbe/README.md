# Ycs.DecodeProbe

Not a test: a differential probe of the decoders. It feeds the same inputs to the public entry points of two builds of
ycs (an old commit and the working tree) and prints every outcome, so that `cmp` of the two outputs proves that what is
accepted and what is rejected, and the exception type and message of each rejection, did not change in a refactoring of
the decoders. The source uses public API only (no `InternalsVisibleTo`, no `#if OLD`), so it compiles against any tree
that has `YDoc.ApplyUpdate(ReadOnlySpan<byte>, ...)`, and survives a rewrite of the internal decoders unchanged.

## Run

```bash
bench/Ycs.DecodeProbe/run.sh <old commit> [mode ...]
```

It extracts `src/` of the old commit with `git archive`, copies the working tree's `src/` (uncommitted changes
included), puts the probe's sources over both, builds both into a scratch directory, runs both on the fixtures of the
working tree and `cmp`s. Exit 0 and `IDENTICAL: ## summary ...` when the outputs are the same; otherwise the first
differences, the exit code 1 and the outputs kept. `SCRATCH=<dir>` chooses the scratch directory (a few hundred MB
with `KEEP=1`; the builds' `bin`/`obj` are deleted at the end), `VERBOSE=1` prints every single outcome on its own line:
diff the two files for the first input that differs. A run is deterministic (fixed seeds, outputs in input order, whatever
the schedule), takes about 2 minutes per side on 8 cores, 4 minutes in all.

By hand: `dotnet run -c Release --project bench/Ycs.DecodeProbe -- . [mode ...] [--verbose]`; the first argument is the
repository root (the fixtures are read from it).

## What it covers

Modes (all by default): `corpus`, `hostile`, `yjs`, `fuzz-v2`, `fuzz-gen`.

Every input is run through every operation below, as given, as every prefix (length 0 to n-1) and with every
single-byte mutation at every position (to 0xFF, 0x00, 0x80, 0xC0, 0xED, 0xF0, 0x7F and byte + 1, those that change the byte):

* an update (v1 and v2): `YDoc.ApplyUpdate(span)` on a fresh document, with `UpdateLimits` tight, and over the document
  that already holds the unmutated update; `TryApplyUpdate`; each with the outcome (exception type, message, `PartlyApplied`)
  and a hash of the document's state afterwards (v1, v2 and state vector); `YUpdate.Decode` (every struct field and the
  delete set), `Describe`, `ParseMeta`, `EncodeStateVector`, `Convert` to the other format, `Diff` against the empty
  state vector and against the unmutated update's, `Merge` of two and of one update; `Snapshot.ContainsUpdate`;
  `SyncProtocol.ReadSyncMessage` with the update as a step 1 (the reply), a step 2 and an update message, and
  as a raw message;
* a snapshot (`Snapshot.Decode`, v1 and v2 reading, `Encode`, `Equals`);
* a state vector (`YDoc.EncodeStateAsUpdate(stateVector)`, `YUpdate.Diff`, a sync step 1);
* a relative position (`RelativePosition.Decode`, `ToJsonString`, `Encode`).

Inputs:

| Mode | Inputs |
| --- | --- |
| `corpus` | the 110 cases of `interop/corpus/cases` (written by Yjs 13.6): each update and the merged state, v1 and v2, with the snapshot, the state vector and three relative positions of the document each builds |
| `hostile` | the pinned cases of `bench/Ycs.Compare/Hostile.cs` (read from the source file), their base states too |
| `yjs` | `yjs-cases.tsv`: updates Yjs wrote for split surrogate pairs, BOMs, keys with astral characters (regenerate: `node yjs-cases.mjs > yjs-cases.tsv`), with the same sweep |
| `fuzz-v2` | 20,000 documents with fuzzed strings (lone and split surrogates, pairs, two- and three-byte characters, a BOM; text items and map keys), 25 % as v1, the rest as v2, each valid and with 4 random mutations (truncation, overwritten bytes, an inserted byte, a removed byte) |
| `fuzz-gen` | the structure-aware generator of `tests/Ycs.Properties`, seeds 1 to 400, every update as v1 and as v2, each valid and with 12 random mutations |

The output has one line per sweep (name, kind, format, bytes, variants, FNV-1a hash of all its outcomes), and at the end a
histogram of the outcome classes (`ok`, or the exception type and message) per operation. The reader's `Remaining` after a
failure is not observable through the public API: it is covered by the unit tests of the decoders.

## Record

2026-11-18, `run.sh 9537fc3` on 9537fc3's tree (identical by construction: it proves the probe runs, is deterministic and
needs the same time on both sides): 51,974 inputs and 24,828,096 operation outcomes, identical. `corpus` is 29,016 sweeps
(2,139,550 inputs with their prefixes and mutations), `hostile` 16 sweeps (4,816), `yjs` 162 sweeps (49,164), `fuzz-v2`
20,000 documents (100,000 inputs), `fuzz-gen` about 2,800 updates (about 36,000 inputs). 105 s and 121 s of the two
runs on 8 cores, 3 min 48 s with the builds. The output is 8 MB per side.

Verified to catch (2026-11-18, against 9537fc3, each change planted alone and reverted): the message of `Lib0Reader.InvalidUtf8`
changed ("UTF-8" to "UTF8"): DIFFERENT, all five modes; the struct-count check of `StructCursor.ReadSectionHeader`
(`EncodingUtils.cs`) one early (`>` to `>=`, same message): DIFFERENT, all five modes (it needs the tight limits of `applyTight`).
