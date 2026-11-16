Complete updates left pending by Yjs 13
=======================================

The sync server's convergence driver found it: applied in some orders, a complete set of updates leaves part of
itself pending in Yjs 13.6.33 for good, while the creation order gives the full state. Ycs completes in every order
(see [Ycs](#ycs)); `tests/Ycs.Tests/PendingRetryTests.cs` checks that on the fixtures written here.

```
npm ci
node gen.mjs        # writes the fixtures pending-retry.json and pending-retry-random.json.gz, prints the counts
node orders.mjs     # every order of the two scenarios: how many stick
node issue-824.mjs  # the first case of yjs/yjs#824 against Yjs 13 and 14
node repro.mjs      # the driver's reproduction as it came
```

`yjs` is pinned to 13.6.33 and `@y/y` to 14.0.0-rc.30 (as `y14`). `YJS13=` and `YJS14=` name other builds, such as a
patched `dist/yjs.mjs` or a patched `src/index.js`, for `gen.mjs` (which then writes no fixture), `orders.mjs` and
`issue-824.mjs`.

## The cause

`readUpdateV2` and `integrateStructs` in `src/utils/encoding.js` of 13.6.33 work like this:

- A struct that cannot be integrated goes, with the dependency walk that led to it (`stack`), into the pending update.
  For each client, `missing` keeps one clock, the lowest that something waited for:
  - a struct after a gap of its own client records `clock - 1` (line 308);
  - a struct that waits for an origin or a parent of a client with no structs left in the update records that
    client's current state (line 322).
- A struct that waits for a client that still has structs in the update does not stop there. The walk continues with
  that client's next struct (line 325) **and records nothing**. If the walk then stops, only what the last struct
  waits for is recorded.
- After each update, the pending update is retried only if one recorded clock is now below the state (line 400,
  `clock < getState(store, client)`). Otherwise what the update could not integrate is merged into the pending update
  (line 413) without a retry.

So a pending struct whose own dependency comes before the clock its walk recorded is not retried when that
dependency arrives. If the update that fills the recorded gap itself waits for that struct, neither can ever be
applied.

## The minimal case

`gen.mjs` builds it with two clients, 1 and 2. Client 2 must have the higher id: Yjs integrates the higher client
first, and with the lower id the walk goes the other way and records the right clock.

| update | struct | waits for |
| --- | --- | --- |
| 0 | `1:0` "x" in the array | |
| 1 | `1:1` "y" | |
| 2 | `2:0`, a map after "y" | origin `1:1` |
| 3 | `1:2`, key `k` in that map | parent `2:0` |
| 4 | `1:3`, "z" in the text | `1:2` |

The order `4, 2, 0, 1, 3` (and `2, 4, 0, 1, 3`; 2 of all 120 orders):

1. `1:3` waits for `1:2`, so `missing` is `{1: 2}`.
2. `2:0` waits for `1:1`. Client 1 has no structs in this update, so `{1: 0}` is recorded, and the merge keeps the
   lower clock: `{1: 0}`.
3. `1:0` brings client 1 to clock 1, and 0 < 1 starts a retry of the pending `{2:0, 1:3}`. `2:0` waits for `1:1`, and
   the walk moves to `1:3`, which waits for `1:2`. Only `{1: 2}` is recorded.
4. `1:1` brings client 1 to clock 2. Now `2:0` could be integrated, but `2 < 2` is false, so nothing is retried.
5. `1:2` waits for its parent `2:0`, which is pending, and `{2: 0}` is added. Nothing is retried.

At the end Yjs shows `["x","y"]` and an empty text, `missing` is `{1: 2, 2: 0}`, and the state vector is `{1: 2}`.
Client 2 cannot advance, because its first struct is the pending one. Client 1 cannot advance past clock 2, because
`1:2` is pending.

The driver's 7 updates (`repro.mjs`; 48 of 5,040 orders stick) follow the same pattern with clients
3221752031 (as 2) and 77007486 (as 1), and an unrelated client that triggers the retry in step 3. One of the 7 also
deletes a struct of a fifth client that none of the 7 carries. That deletion stays in `pendingDs` in every order and
is unrelated to this behaviour.

## What un-sticks it

These are in the fixture as `healing`, observed in Yjs (Ycs does not stick, so they are not asserted for it):

| applied afterwards | minimal | driver's case |
| --- | --- | --- |
| an empty update | still stuck | still stuck |
| an update of a client not in the set | still stuck | still stuck |
| the pending update (`pendingStructs.update`) | complete | complete (the stray deletion stays) |
| update 2 again (`2:0`) | complete | complete (the stray deletion stays) |

A real client stays stuck while it only receives new updates, because none of them can advance client 1 or client 2.
It recovers when it syncs again: after a reconnect, sync step 1 sends its state vector, which lacks `2:0`, so the peer
sends `2:0` again. A server that stores the document from such a doc's `encodeStateAsUpdate` keeps everything, since
the encoding includes the pending update, and a doc loaded from it is complete.

## Yjs 14

`@y/y` 14.0.0-rc.30 does not stick on either case. It integrates structs past their client's gap, and it also retries
when the update holds structs of a client that is missing (`ss.clients.has(client)`, line 278). It has the same
omission, though: following the missing client's structs (line 194) records nothing. Its other stop
(`stack.some(...)`, line 189) records the client of the stack instead, which is the first case of yjs/yjs#824 (open,
v14 only). That case does not stick in 13: there the struct after the gap is not integrated, and the lower clock it
records is kept. #827 (open) rewrites the v14 code. Nothing addresses v13.

In the random runs below, v14 sticks more often than v13 (108 against 2), with the #824 pattern.

The fix that both versions need is one line, in the `else` branch before the walk continues: record what the struct
waits for, `updateMissingSv(missing, getState(store, missing))` in 13 (`store.getClock` in 14). With it, run through
`YJS13=`/`YJS14=`, these all come out at 0: `gen.mjs` (both versions, every run), `orders.mjs` (0 of 120 and 0 of
5,040) and `issue-824.mjs` (v14 completes). The cost is an occasional extra retry when a struct that the walk
recorded does get integrated in the same update. We did not run Yjs's own test suite with the line.

`#829` (closed: a delete set applied before the insert it follows) and `#591` (open: a map key missing until the
earlier update arrives) are about states that are only temporary. Both converge once every update has arrived, so
neither is this problem.

## Random runs

`gen.mjs` uses a fixed seed and makes two kinds of session. Every local transaction is one update, and every run
applies all updates of a session to a fresh doc.

| sessions | runs | Yjs 13.6.33 stuck | Yjs 14.0.0-rc.30 stuck |
| --- | --- | --- | --- |
| broad: 300 sessions, 3 to 5 clients, 8 to 27 steps (text, array with nested maps and arrays, map, deletions), 5 orders each (the creation order and 4 shuffles) | 1,500 | 0 | 0 |
| narrow: 3,000 sessions, 2 or 3 clients, 6 updates (append values or maps, set a key in the last map, type), 20 orders each | 60,000 | 2 (2 sessions) | 108 (44 sessions) |

The creation order never sticks. Another seed of the narrow kind with 2 clients gave 14 of 60,000 in 13 and 284 in 14.
The fixture keeps every broad session, the first 200 narrow ones, and every session where either version stuck:
544 sessions, 6,380 runs, 2 of them stuck in Yjs 13. In every run Ycs ends with nothing pending and the session's
full state.

## Ycs

Ycs records the state of the client a struct waits for when the walk follows that client's structs in the same update
(`IntegrationPlan.Plan` in `src/Ycs/Utils/StructStore.IntegrationPlan.cs`), as Yjs does at its line 322 when the walk
stops there. So a pending struct is retried when its own dependency arrives, and every order of a complete set
completes: the stuck orders of both scenarios, all 6,380 runs of the fixture, and 65,280 further random orders of its
sessions (Yjs 13.6.32's own shuffled traces stick twice; `PendingTraceTests` checks that Ycs ends complete there). A
deliberate divergence from Yjs, which fixes yjs/yjs#824's first case.
