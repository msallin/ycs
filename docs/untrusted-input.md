# Untrusted input and threads

A server that relays for browsers, or a client that applies what a server sends, applies bytes it does not control.
Ycs is built for that. This page says what is guaranteed and what is left to you; the
[API reference](api.md#limits-and-untrusted-input) has the full contract and the measurements.

## Malformed updates

Every API that decodes bytes (`ApplyUpdate`, `TryApplyUpdate`, `EncodeStateAsUpdate(stateVector)`, everything in
`YUpdate`, `Snapshot.Decode`, `RelativePosition.Decode`, `SyncProtocol.ReadSyncMessage`) throws only `YUpdateException`
for malformed input. Its message says what was wrong, never the input's content.

```csharp
try
{
    doc.ApplyUpdate(fromPeer);
}
catch (YUpdateException ex) when (!ex.PartlyApplied)
{
    // The document is exactly as it was. Drop the peer, or ask it to resync.
}
```

An update that lets waiting structs of earlier updates integrate retries them, and drops those that fail a check
(nesting, garbage collection). That is not the sender's fault: the update stands whole and `ApplyUpdate` returns
normally. The document raises `YDoc.PendingDropped` (a `PendingDrop`: the number of structs and the clients they were of), so that a
server can log it and resync the peers it handed the waiting structs to. Only when structs of the update itself waited
for the dropped ones is the update cut short (`PartlyApplied`).

`TryApplyUpdate` returns false instead of throwing, for a malformed update and for one applied in part.

`ApplyUpdate` decodes and checks the whole update before it changes the document, so a rejected update leaves it
unchanged: same content, same state vector, no observer or `Updated` event (`AfterTransaction` fires for the empty
transaction). One exception: an update whose conflict resolution exceeds its budget (below), or whose own structs
waited for waiting structs that were dropped, is applied in part, and `YUpdateException.PartlyApplied` is set. The part
applied is causally complete and the document stays valid.

## What one update may cost

A few bytes of the v2 format can describe millions of items. Yjs has no limits; Ycs bounds each update with
`UpdateLimits`:

| Limit | Default | What it bounds |
| --- | --- | --- |
| `MaxStructsPerUpdate` | 131,072 | items in one update, in either format (v2 can describe 131,072 deleted items in 42 bytes) |
| `MaxPendingStructs` | 131,072 | items waiting for others the document does not have yet |
| `MaxPendingBytes` | 16 MiB | the bytes of what waits |
| `MaxIntegrationSteps` | 2^24 | the conflict resolution of one update, about 0.1 s (up to 1.5 s); beyond it the update is applied in part |
| `MaxDepth` | 3,000 | how deeply values and shared types nest |

With the defaults, one update makes a document allocate at most about 180 MiB and keep about 135 MiB (a subdocument is the dearest
struct: any other shape keeps at most 75 MiB). Honest updates
stay far below: the largest in the Yjs corpus took 102 conflict steps. Clocks beyond 2^31 - 1 and client ids beyond
2^53 - 1 are rejected whatever the limits.

What waits is read again whenever an update advances a client that something waits on: every retry decodes,
plans and encodes all of it, so a small update (23 bytes) may cost as much as the pending limit allows. Measured
on a peer that queues m structs, each waiting on another struct of one client, and then sends that client's structs
one at a time: about 27 ns per waiting struct and 5 bytes allocated per struct and update, so 110 µs with 4,096
waiting (the sample server's limit), and about 10 ms and 10 MB with the default 131,072 (extrapolated from 2.2 ms at
32,000). Yjs pays the same retry, with no limit at all. A server that applies updates under a lock should set
`MaxPendingStructs` and `MaxPendingBytes` to what honest clients leave waiting, which is little when they send
their updates in order (y-websocket does), and treat the exceeded limit as a reason to close that connection: the
client reconnects and syncs again.

The limits bound one update, not a document. A document grows with every update it accepts; bounding that (the
number of documents, their size, the rate of updates per peer) is the application's job.

## Trusted input

Your own stored state can exceed the defaults: a million characters typed one at a time are a million items. Apply
input you trust without limits, per call:

```csharp
doc.ApplyUpdate(stored, null, limits: UpdateLimits.Unlimited);
```

`new YDocOptions { Limits = … }` sets a document's limits for every update, and `ApplyUpdate`, `TryApplyUpdate` and every `YUpdate` function
take a `limits` argument. `UpdateLimits` is immutable; one instance can be shared.

## Exceptions from your handlers

An exception thrown by an observer or an event handler is never swallowed: it comes out of the outermost
`Transact` (or `ApplyUpdate`) after every remaining step of the cleanup has run, so the document stays usable. If an
`Updated` handler throws, the handlers after it miss that update; a provider among them did not send it, so resync
those peers. Better: do not throw from these handlers. Details: [API reference](api.md#exceptions-of-handlers).

## Thread safety

As in Yjs, a `YDoc` and everything in it is not thread-safe. Calls into a document, applying updates and reading
values included, must not run at the same time. A server that shares a document between connections serializes
access with one lock or one channel per document; the sample server holds one lock per room.

Handlers run synchronously, on the thread that made the change, while that thread holds whatever lock it took.
