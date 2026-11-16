# Ycs findings from the sync server

What the sync server's tests (`docs/plan-server.md`) found in Ycs itself. None is fixed in `src/Ycs` yet; each has a
test that reproduces it without the server. Found on 2026-11-16.

| # | Finding | Test | Yjs 13.6.33 | Proposed fix |
| --- | --- | --- | --- | --- |
| 1 | A complete set of updates, applied in some orders, stays partly pending: the walk in `IntegrationPlan` follows the structs of the client a struct waits for and records nothing for that struct, so the retry check never fires (`interop/yjs-pending/README.md`) | `tests/Ycs.Tests/PendingRetryParityTests.cs` (pins parity with Yjs) | same (yjs/yjs#824; comment text in `interop/yjs-pending/upstream.md`) | one line before the walk continues (`StructStore.cs`, before line 925 on 2026-11-16): record the missing client's state, as Yjs's line 322 does at the end of the walk. Then rename the parity tests: Ycs completes where Yjs sticks |
| 2 | Waiting structs can reach twice `MaxPendingStructs` (an update that lets some integrate may bring more); after that, any update carrying the same waiting structs again is refused although it adds nothing, and every client's step 2 carries them: an honest client is locked out on every reconnect | `tests/Ycs.Server.Tests/HostileYcsTests.cs` `ApplyUpdate_WaitingStructsOverTheLimitSentBack_AreAccepted` (ignored) | no limit | count only waiting structs that are not duplicates, or compare against the larger of the limit and what already waits |
| 3 | Nested types deeper than `MaxDepth` that were waiting are dropped without an error when what they wait for arrives: no `PartlyApplied`, nothing left waiting; clients without the limit integrate them and stay ahead | `HostileYcsTests.ApplyUpdate_WaitingTypesDeeperThanTheLimitResolved_AreRefusedOrKept` (ignored) | no depth limit | refuse (or keep waiting) instead of dropping |
| 4 | An item whose right origin precedes its origin (crafted; Yjs never writes one): present clients and a joiner see different texts | `tests/Ycs.Server.Tests/HostileSyncTests.cs` `Receive_ItemWhoseRightOriginPrecedesItsOrigin_AJoinerSeesAnotherText` | same | refuse such items on decode |
| 5 | Two structs under one id, the first waiting on an unknown client (crafted): the structs behind it stay waiting in the room, a joiner is ahead for good | `HostileSyncTests.Receive_TwoStructsUnderOneId_AJoinerIsAheadOfTheRoom` | same | refuse overlapping ids within an update |

4 and 5 need a crafted update; 1 happens with honest clients (2 in 60,000 random orders), and real clients recover on
their next reconnect. 2 and 3 need a hostile client or very large documents (the server's room limits: `Room.DocumentLimits`).
