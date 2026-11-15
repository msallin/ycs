Yjs corpus
-------

A recorded corpus that guards Ycs' byte- and state-compatibility with real Yjs, without needing
Node at test time.

- `gen.mjs` drives real Yjs (pinned in `package.json`, 13.6.32) and writes one JSON file per
  case to `cases/`: the updates an observer received (v1 and v2, in delivery order, possibly
  shuffled and duplicated) and what Yjs ends up with: the content of each root (text, delta,
  JSON), the state vector, the delete set, `encodeStateAsUpdate`/`V2`, `encodeStateVector`, a
  state-vector diff against a prefix of the updates, `mergeUpdates`/`V2`, and the item list of
  each sequence root (ids in list order, tombstones included). Deterministic: seeded PRNG, fixed
  client ids, no clocks; re-running it gives identical files.
- The `text-format-*` cases are scripted (`Sim.ops`): besides the updates, they record what each
  client did and received, as data (`script`), and where each client ended up (`clients`).
  `ClientReplayTests.cs` replays them with a Ycs document per client, so Ycs' own formatting and
  its cleanup after remote changes (which an observer never runs on formats of its own) are
  compared with Yjs byte for byte.
- The `adversarial-*` cases are the interleavings the Lean proof of Yjs' integrate
  ([lean-yjs](https://github.com/iasakura/lean-yjs)) treats as hard: the scenario behind the
  YATA-pseudocode discrepancy, same origin with different right origins, inserts between an item
  and its origin's successors, right origins left of a competitor, a pending scan region, chains
  from four clients and a deleted competitor, each with every assignment of client ids to roles.
- `tests/Ycs.Corpus` replays every case through Ycs (see `CorpusTests.cs`), plus a seeded
  random convergence test of Ycs against itself (`ConvergenceTests.cs`) and minimal
  reproductions of each divergence found (`DivergenceTests.cs`). The only file there that
  touches the Ycs API is `YcsAdapter.cs`.
- `ItemListInvariants.cs` checks the list invariant of the Lean development (`YjsArrInvariant`:
  closed, I1-I3, sorted by the Lean order, unique ids; plus no-cross-origin and "an update only
  inserts") on every sequence, after every update of every case, of every convergence session,
  and of every delivery order of the adversarial cases (`ItemListInvariantsTests.cs`), which must
  also give Yjs' item order. `CommutativityTests.cs` applies small three-client scenarios in
  every order (all 5040 permutations of 7 inserts, or all causal orders of 12) and requires the
  same text and item order each time.
- `undo-gen.mjs` is a differential fuzz of the UndoManager: random sessions of a document with an
  UndoManager (scope of some roots or the whole document, capture timeout 0 or merging, tracked
  origins by value and by class, `deleteFilter`, `captureTransaction`, `ignoreRemoteMapChanges`,
  0 to 3 remote clients with concurrent changes; text with formatting and embeds, maps, arrays,
  nested maps and texts, XML elements, attributes and texts, several changes per transaction, undo,
  redo, `stopCapturing`, `clear` with each pair of flags, `addToScope`), recorded per step: a hash of
  the state, what undo/redo returned, both stack sizes and the UndoManager events.
  `UndoDifferentialTests.cs` replays them in Ycs (its replay touches the Ycs API directly). The
  pinned set is `undo/undo-differential.json.gz` (400 scripts of 50 steps); a larger run:

      node undo-gen.mjs 20000 60 /tmp/undo.json
      YCS_UNDO_FUZZ=/tmp/undo.json dotnet test tests/Ycs.Corpus --filter Name~Scripts_EveryStep --logger "console;verbosity=detailed"

  (`FULL=1` records whole states instead of hashes, so a divergence shows both.) That run
  (20,000 scripts of 60 steps) matches Yjs on every step. `undo/undo-gc-merge.json.gz` keeps its
  20 scripts that diverged while deleted structs merged less far to the left than in Yjs 13.6.
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
