Yjs ⇄ Ycs interop harness
-------

`./run.sh` does a full round-trip against the published Yjs packages:

1. `js/gen.mjs` builds a document with real Yjs (map with every scalar type, binary, nested
   Y.Map / Y.Array, formatted text, an embed, and a delete set) and dumps its v1 and v2 updates.
2. `Ycs.Interop` applies the v1 update with Ycs, writes into the document, and dumps what it sees
   plus its own v1/v2 updates and a state-vector diff.
3. `js/verify.mjs` checks Yjs reads all of it back, and that merging Ycs' update onto the live
   JS document keeps both sides' edits.

Two Yjs versions are installed side by side (`yjs13` → 13.6.32, `yjs14` → 14.0.0-16) because
v14 is still beta; both are exercised.

`run.sh` also auto-discovers every `js/check-*.mjs` scenario; today that is `check-xml` (the XML
shared types), `check-updates` (merge/diff/state-vector/format conversion), `check-positions`
(snapshots and relative positions) and `check-types` (events, deltas, undo/redo). Each drives its own
small console app under `Ycs.*.Interop/` and exits non-zero on any mismatch.

`run.sh` ends with `v14/roundtrip.sh`, the interop with Yjs v14 as published on npm (`@y/y`, pinned to
14.0.0-rc.30 in `v14/package.json` and its own lock, so it cannot disturb the 13.6.32 installs; ESM only,
Node >= 22). `v14/gen.mjs` writes `tests/Ycs.Tests/Fixtures/v14-interop.json` (documents with root and
nested `Y.Node`s, attributes, formatted text, list content and deletions; client ids above 2^32 and at
2^53 - 1), which `V14InteropTests` checks without Node; `v14/roundtrip.mjs` feeds Ycs' answers back into
v14. See `docs/testing.md`.

The same updates are pinned as base64 fixtures in `tests/Ycs.Tests/`, so
`dotnet test` catches format drift without Node. Re-generate them from the JSON that `run.sh`
leaves in `$TMPDIR` when bumping the tested Yjs version.

`traces/gen.mjs` records the differential traces of the shared types and their events that
`tests/Ycs.Tests/TraceReplayTests.cs` replays (see `docs/testing.md`).

`js/merge-conflicts.mjs` writes `tests/Ycs.Tests/Fixtures/merge-conflicts.json`: updates that hold a GC struct and an
item for one clock, merged and left pending in every order (which one Yjs keeps depends on the order of V8's sort).
`js/v8-sort.mjs` writes `tests/Ycs.Tests/Fixtures/v8-sort.json`: the orders V8's `Array.prototype.sort` gives for
comparisons consistent and not, which `StableSort` must reproduce. Both: `cd js && node <script> > <fixture>`.
