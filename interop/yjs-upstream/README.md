# Upstream bug reports for Yjs (drafts, nothing posted)

Drafts for github.com/yjs/yjs, checked 2026-11-17 against yjs 13.6.33 (npm `latest`), 14.0.0-16 (`yjs@beta`) and `@y/y` 14.0.0-rc.30 (v14 moved to that package; `insertAfter` is gone there). Each repro runs with `npm i yjs@13.6.33 && node <file>.mjs`. The operator posts them by hand.

| | Candidate | Verdict | Target | Draft | Repro | Status |
|---|---|---|---|---|---|---|
| A | Pending structs not retried (one missing clock kept per client) | Bug, confirmed on 13.6.33; v14 gives the right result for both cases | Comment on [#824](https://github.com/yjs/yjs/issues/824) | `A-pending-824-comment.md` | `pending-first-case.mjs`, `pending-second-case.mjs` | not posted |
| B | `insertAfter(ref, ...)` with a `ref` that GC merged away | Bug, confirmed on 13.6.33 and 14.0.0-16 | New issue | `B-insertafter-merged-ref.md` | `insert-after-merged.mjs` | not posted |
| C | `insertAfter(ref, ...)` with a `ref` of another fragment or of no document | Bug (missing parent check), confirmed on 13.6.33 and 14.0.0-16 | New issue | `C-insertafter-foreign-ref.md` | `insert-after-foreign.mjs` | not posted |
| D | Inserting an already integrated type a second time | User error (maintainer: #342, #183), but throws after the item is linked and leaves the doc differing from its encoding | Optional low-priority new issue | `D-integrated-type-insert.md` | `insert-integrated-type.mjs` | not posted |
| E | Yjs throws in reversed delivery (fuzz seeds 1215, 3458) | Not a bug report: needs input real Yjs peers do not write | none | none | none | not posted |

## Duplicates and related issues

- A: [#824](https://github.com/yjs/yjs/issues/824) is the target (v14 clock gaps; the 13.x case has the same missing record). Related, not duplicates: [#591](https://github.com/yjs/yjs/issues/591) and [#461](https://github.com/yjs/yjs/issues/461) (a Y.Map key temporarily missing while a struct is pending: that is the documented effect of a missing update, not the retry bug); [#829](https://github.com/yjs/yjs/issues/829) (closed, delete sets).
- B, C: no existing issue. `insertAfter` appears only in [#259](https://github.com/yjs/yjs/issues/259) (wishlist) and PR [#267](https://github.com/yjs/yjs/pull/267) (added it).
- D: [#342](https://github.com/yjs/yjs/issues/342) and [#183](https://github.com/yjs/yjs/issues/183), both closed as "a type can only exist once"; they are about the same TypeError and nobody reported the half-applied state.

## Notes

- A: the second case is our property soak's seed 3514 (pinned as `yjs-pending-retry-deferred`), rebuilt with plain Yjs. The one-line fix (`updateMissingSv(missing, getState(store, missing))` before `stackHead = structRefs.refs[structRefs.i++]`, `src/utils/encoding.js:325`) fixes it, as it fixes the first case. The comment replaces the one in `00bd32f:interop/yjs-pending/upstream.md`: the figures of 120 orders and 60,000 runs there were measured on the earlier version and are dropped here, and the v14 statement changed (14.0.0-16 and rc.30 resolve both cases).
- B, C: both fixes were checked on a patched copy of 13.6.33. The concurrent-insert variant of B comes from our review and is not in the script.
- E: the seeds are those of `tests/Ycs.Properties/UpdateFuzz.cs` (`UpdateFuzzer.Generate(seed)`, 5 updates each). Findings with 13.6.33 and 14.0.0-16 (both throw):
  - It is not about reversed order: seed 1215 throws reversed, 3458 in both orders, and 68 of the 8,000 generated runs (seeds 1 to 4000, with and without sorted delete sets) throw in some order.
  - It needs `gc: true`; with `gc: false` none throws.
  - The error is `Unexpected case` from `Item.gc` (`src/structs/Item.js:636`): the garbage collector meets a live item inside a deleted type. For seed 1215 three updates in the order 0, 2, 1 are enough (the third holds the type `11:9` and its child `300:3`, the second its delete set).
  - The inputs are synthetic: GC structs at the head of a client's clocks next to items that use them as parents, clocks near 2^31, origins that do not agree with their parents. A real Yjs peer does not write these (`docs/api.md` says so for Ycs), so a report would be answered with "invalid update". We could not rebuild it from real Yjs operations (a test with a type holding a two-item run, one item deleted, then the type deleted, in all 24 orders, does not throw). Not drafted.
