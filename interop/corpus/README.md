Yjs corpus
-------

A recorded corpus that guards Ycs' byte- and state-compatibility with real Yjs, without needing
Node at test time.

- `gen.mjs` drives real Yjs (pinned in `package.json`, 13.6.32) and writes one JSON file per
  case to `cases/`: the updates an observer received (v1 and v2, in delivery order, possibly
  shuffled and duplicated) and what Yjs ends up with: the content of each root (text, delta,
  JSON), the state vector, the delete set, `encodeStateAsUpdate`/`V2`, `encodeStateVector`, a
  state-vector diff against a prefix of the updates, and `mergeUpdates`/`V2`. Deterministic:
  seeded PRNG, fixed client ids, no clocks; re-running it gives identical files.
- `tests/Ycs.Corpus` replays every case through Ycs (see `CorpusTests.cs`), plus a seeded
  random convergence test of Ycs against itself (`ConvergenceTests.cs`) and minimal
  reproductions of each divergence found (`DivergenceTests.cs`). The only file there that
  touches the Ycs API is `YcsAdapter.cs`.
- Divergences that are bugs in Ycs are listed in `tests/Ycs.Corpus/KnownFailures.cs`: those
  checks report Inconclusive (skipped) with the reason, and fail if they start passing.

Run the tests (no Node needed):

    dotnet test tests/Ycs.Corpus

A longer convergence soak: `YCS_CONVERGENCE_SEEDS=3000 dotnet test tests/Ycs.Corpus --filter "FullyQualifiedName~Convergence"`.
A failing seed reproduces with the same seed.

Regenerate the corpus (after changing a case or bumping Yjs):

    cd interop/corpus && npm ci && node gen.mjs

Round-trip back into Yjs (Node and the .NET SDK; not part of `dotnet test`): Ycs re-encodes
every case and authors its own random sessions, and Yjs reads all of it back and checks the
state.

    interop/corpus/roundtrip.sh
