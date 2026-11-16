# Ycs findings from the sync server

What the sync server's tests (`docs/plan-server.md`) found in Ycs itself, found and fixed on 2026-11-16; each has a
test that reproduces it without the server. How each is handled: `docs/api.md` ("What is checked", "Pending structs").

| # | Finding | Now | Test | Yjs 13.6.33 |
| --- | --- | --- | --- | --- |
| 1 | A complete set of updates, applied in some orders, stayed partly pending: the walk in `IntegrationPlan` followed the structs of the client a struct waits for and recorded nothing for that struct, so the retry never ran (`interop/yjs-pending/README.md`) | the walk records the missing client's state; Ycs completes where Yjs sticks | `tests/Ycs.Tests/PendingRetryTests.cs` | sticks (yjs/yjs#824; comment text in `interop/yjs-pending/upstream.md`) |
| 2 | Waiting structs could reach twice `MaxPendingStructs`; after that an update carrying the same waiting structs again was refused although it added nothing, so an honest client was locked out on every reconnect | duplicates of what waits are accepted | `tests/Ycs.Server.Tests/HostileYcsTests.cs` `ApplyUpdate_WaitingStructsOverTheLimitSentBack_AreAccepted` | no limit |
| 3 | Nested types deeper than `MaxDepth` that were waiting were dropped without an error when what they waited for arrived | the apply throws `YUpdateException` with `PartlyApplied`; the room broadcasts what was applied and closes the session | `HostileYcsTests.ApplyUpdate_WaitingTypesDeeperThanTheLimitResolved_AreRefusedOrKept` | no depth limit |
| 4 | An item whose right origin precedes its origin (crafted; Yjs never writes one) made present clients and a joiner see different texts | refused on decode | `tests/Ycs.Server.Tests/HostileSyncTests.cs` `Receive_ItemWhoseRightOriginPrecedesItsOrigin_ClosesItsSessionAndAJoinerSeesTheSameText` | accepted, texts differ |
| 5 | Two structs under one id, the first waiting on an unknown client (crafted), left the structs behind it waiting in the room and a joiner ahead for good | refused on decode | `HostileSyncTests.Receive_TwoStructsUnderOneId_ClosesItsSessionAndAJoinerHasWhatTheRoomHas` | accepted, joiner ahead |

1 happens with honest clients (2 in 60,000 random orders in Yjs); 2 and 3 need a hostile client or very large
documents (the server's room limits: `Room.DocumentLimits`); 4 and 5 need a crafted update.
