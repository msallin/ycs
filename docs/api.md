# API: Ycs, Yjs and YDotNet

This page compares the public API of Ycs with [Yjs](https://github.com/yjs/yjs) (13.6) and with
[YDotNet](https://github.com/y-crdt/ydotnet), the .NET binding to the Rust implementation
[yrs](https://github.com/y-crdt/y-crdt), and states the contracts a caller relies on. Ycs keeps the Yjs concepts and
names; everything is in the namespace `Ycs`.

## How the three differ

| Concern | Yjs | YDotNet | Ycs |
| --- | --- | --- | --- |
| Transactions | implicit; `doc.transact(fn, origin)` groups changes | explicit: every call takes a `Transaction` from `doc.WriteTransaction()`, which must be committed or disposed | implicit, as in Yjs: `doc.Transact(tr => ..., origin)` groups changes |
| Indexes | `number` (UTF-16 code units for text) | `uint` | `int` (UTF-16 code units, as in Yjs) |
| Values | any JSON-like value, `Uint8Array`, `bigint`, shared types | tagged `Input` / `Output` classes (`Input.Long(5)`, `output.Long`) | plain CLR values from a closed set: `null`, `bool`, `int`/`long`, `double`, `BigInteger`, `string`, `byte[]`, lists, string-keyed dictionaries, shared types |
| Update format | v1 (`applyUpdate`) by default, v2 (`applyUpdateV2`) | `ApplyV1`/`ApplyV2`, `StateDiffV1`/`StateDiffV2` | v1 by default, `UpdateFormat.V2` as a `format` argument (`ApplyUpdate`, `EncodeStateAsUpdate`, `YUpdate`, `Snapshot`, `SyncProtocol`) |
| Subscriptions | `observe(f)` / `unobserve(f)`, `doc.on('update', f)` | `IDisposable Observe(Action<T>)` | typed `Observe(Action<TEvent>)` and `ObserveDeep` returning `IDisposable`; .NET events on `YDoc` (`Updated`, `UpdatedV2`, `SubdocsChanged`, the transaction events) |
| Positions that survive edits | `RelativePosition` | `StickyIndex` | `RelativePosition` / `AbsolutePosition` |
| Undo | `UndoManager(scope, { trackedOrigins, captureTimeout })` | `UndoManager(doc, branch, options)` | `UndoManager(scope, new UndoManagerOptions { TrackedOrigins, CaptureTimeout })` |
| Threading | single-threaded | the native document is locked per transaction | the caller's job, as in Yjs ([Thread safety](#thread-safety)) |

YDotNet's explicit transactions and tagged values come from the Rust borrow model and the native
boundary; they would make Ycs harder to use without making it safer, so Ycs keeps the Yjs shape.

## Events

Each shared type observes its own events: `YArray.Observe(Action<YArrayEvent>)`, `YMap` (`YMapEvent`), `YText` and
`YXmlText` (`YTextEvent`), `YXmlFragment` and `YXmlElement` (`YXmlEvent`). `YType.ObserveDeep(Action<IReadOnlyList<YEvent>,
Transaction>)` gets the events of the type and everything nested in it, the outer types' first. Both return an
`IDisposable`; disposing it removes that registration (one handler registered twice is two registrations). A handler may
be typed more generally (an `Action<YEvent>` observes any type). Every handler runs even when one threw; the last
exception escapes.

An event is read-only: `Target`, `CurrentTarget`, `Transaction`, `Path` (an `IReadOnlyList<PathSegment>`: a map key, or the
index in a list when `Key` is null), `Changes` (`YChanges`: `Added`, `Deleted`, `Delta`, `Keys`), `Delta`, `Keys`
(`IReadOnlyDictionary<string, KeyChange>`: a `KeyChangeAction` and the old value, still an `object?`), and `Adds(view)`,
`Deletes(view)`. It is valid during the handler, its transaction's observers and cleanup; compute what you keep there.

### Text and deltas

`YText` (and `YXmlText`, which derives from it): `Length`, `Insert(index, string, attributes?)`, `InsertEmbed(index, YValue,
attributes?)`, `Remove(index, length)`, `Format(index, length, attributes)`, `ApplyDelta(IEnumerable<Delta>, sanitize)`,
`ToDelta()`, `ToDelta(snapshot, previous?, computeYChange?)`, `RemoveNonText()`, `CleanupFormatting()`, `ToString()`,
`CopyTo(Span<char>)` (the characters written; an `ArgumentException` when the span is shorter than the text, and `Length` is
always enough) and `WriteTo(IBufferWriter<char>)`, the text's own attributes (`GetAttribute` is null when unset,
`GetAttributes`, `SetAttribute`, `RemoveAttribute`) and `Clone`. Formatting attributes and embeds are `YValue`s; in an
attribute dictionary of `Format` or of a `Retain`, a `YValue.Null` removes the attribute.

A `Delta` is one operation of a delta, one of the sealed cases `Delta.Insert(YValue Value)` (a string, or an embed: a shared
type or a value; an embed of null is `YValue.Null`), `Delta.InsertItems(IReadOnlyList<YValue> Items)` (what an array's or an
XML fragment's event inserted), `Delta.Retain(int Length)` and `Delta.Delete(int Length)`, with `Attributes`
(`IReadOnlyDictionary<string, YValue>?`) on every case. `ToDelta(snapshot, previous)` marks added and removed runs with a
`ychange` attribute, an object of `type` (`"added"` or `"removed"`) and, when `computeYChange` returns a `YChange(YChangeKind,
long? User)` with one, `user`. Applying an `InsertItems` to a text throws.

On `YDoc`, `Updated` and `UpdatedV2` raise an `UpdateEventArgs(Update, Origin, Transaction)`: `Update` is a new
`ReadOnlyMemory<byte>` for each event, which a handler may keep. `SubdocsChanged` raises a `SubdocsEventArgs(Loaded,
Added, Removed)` of read-only sets and `AfterAllTransactions` an `IReadOnlyList<Transaction>`.

## Items in events and filters

Events and filters name a document's content by `ItemView`, and a decoded update by `DecodedStruct`. Neither
exposes the storage, which is internal.

| Type | What it is |
| --- | --- |
| `ItemView` | a `readonly struct`: a document and an id. It reads the struct that holds the id now, as if split at the id. Valid as long as the document, across splits, merges and garbage collection. `default(ItemView)` (`IsDefault`) names nothing: every member throws `InvalidOperationException` |
| `ItemKind` | what a struct holds: `String`, `Any`, `Json`, `Binary`, `Embed`, `Format`, `Type`, `Doc`, `Deleted`, `GC`, `Skip` (the numbers of the update format) |
| `DecodedStruct` | a `sealed record` of `init` properties (`Id`, `Length` and `Kind` are `required`): one struct of a decoded update, in the update's order, detached from any document |

`ItemView` members: `Id`, `LastId`, `Length`, `Kind`, `Content` (the values, a read-only list), `Deleted`,
`Countable`, `Keep`, `LeftOrigin`, `RightOrigin`, `Left`, `Right` (`ItemView?`), `Parent`, `ParentSub`, `Redone`,
`Doc` (null for a view of a decoded struct that has no document). `Length` is a `long`. `Content` is `IReadOnlyList<object?>`, a diagnostic read of the stored values. A GC struct reads `Parent` null, empty `Content`, `Deleted` true.

`DecodedStruct` members: `Id`, `Length` (a `long`), `Kind`, `LeftOrigin`, `RightOrigin`, `ParentName` (a root type) or
`ParentId`, `ParentSub`, and what the kind carries, in exactly one place: `Content` (Any, Json, Binary, Embed), `Text`
(String), `FormatKey` and `FormatValue` (Format), `TypeRef` and `TypeName` (Type), `DocGuid` (Doc). The others are
empty, null or -1.

| Where | Gets |
| --- | --- |
| `YChanges.Added`, `.Deleted` | `IReadOnlySet<ItemView>` |
| `YEvent.Adds(view)`, `Deletes(view)` | whether the event added or deleted it, also when both |
| `YDocOptions.GcFilter` | `Predicate<ItemView>`: true lets a deleted item be collected |
| `UndoManagerOptions.DeleteFilter` | `Func<ItemView, bool>`: false keeps an undo or redo from deleting the item |
| `YUpdate.Decode` | `DecodedUpdate.Structs`: `IReadOnlyList<DecodedStruct>` |

```csharp
using Ycs;

var doc = new YDoc(new YDocOptions
{
    ClientId = 1,
    // Collect deleted text; keep deleted shared types (true collects).
    GcFilter = item => item.Kind != ItemKind.Type,
});
var list = doc.GetArray("list");

list.Observe(e =>
{
    foreach (ItemView item in e.Changes.Added)
    {
        Console.WriteLine($"added {item.Kind}, {item.Length} at {item.Id.Client}:{item.Id.Clock}");
    }
});

list.Add(["a", "b"]);
// added Any, 2 at 1:0

foreach (DecodedStruct s in YUpdate.Decode(doc.EncodeStateAsUpdate(), format: UpdateFormat.V1).Structs)
{
    Console.WriteLine($"{s.Kind} {s.Id.Client}:{s.Id.Clock} in {s.ParentName}: {string.Join(",", s.Content)}");
}
// Any 1:0 in list: a,b
```

**Views after a merge.** Inside a transaction and its observers a view reads what Yjs' item reads. After the
cleanup merges items a, b and c into one, a view of b reads "bc", where Yjs' orphaned item reads "b". So views kept from
`Changes.Added` and read after the transaction give "abc", "bc", "c".

## Positions

`RelativePosition` is a `readonly record struct` that keeps pointing at the same character (or the end of the same
type) while the document changes; `AbsolutePosition` is the index it resolves to at a moment. Both are values: equal
positions are equal keys of a `HashSet` or `Dictionary`.

```csharp
var position = RelativePosition.FromIndex(text, 3);          // assoc 0: the character after, -1: before
text.Insert(0, "abc");
if (position.TryResolve(doc, out var absolute))              // false: names a struct the document lacks or collected
{
    // absolute.Type, absolute.Index (6), absolute.Assoc
}

byte[] bytes = position.Encode();                            // Yjs' encodeRelativePosition; awareness carries it
var same = RelativePosition.Decode(bytes);                   // YUpdateException for malformed bytes
```

`Encode(IBufferWriter<byte>)` writes into a buffer. `WriteJson(Utf8JsonWriter)` and `FromJson(JsonElement)` are Yjs'
`relativePositionToJSON` object (`{"item":{"client":1,"clock":0},"assoc":0}`; `type`, `tname` and `item` only where
set). `TryResolve(doc, out position, followUndoneDeletions: false)` gives the same result on every client; the
default follows undone deletions, which suits the user who undid. `default(RelativePosition)` names nothing: it does
not resolve and does not encode.

## Encoding

`enum UpdateFormat { V1, V2 }` picks the encoding wherever bytes go in or out; v1 is the default (what every y-protocols
provider sends), v2 is denser. Inputs are spans, and a method that decodes an input needs an array internally, so it
copies the span into a buffer of the shared array pool for the call: no allocation beyond the pool's. A byte array
converts to a span implicitly.

| On | Members |
| --- | --- |
| `YDoc` | `ApplyUpdate(update, origin = null, format = V1, limits = null)` and `TryApplyUpdate` (the same, returning false for a malformed update); `EncodeStateAsUpdate(stateVector = default, format = V1)` returning `byte[]`, or with an `IBufferWriter<byte>` first; `EncodeStateVector()` (the same bytes for both formats), also into an `IBufferWriter<byte>`; `CreateSnapshot`, `CreateDocFromSnapshot` |
| `YUpdate` | `Merge(updates, format, limits)` over a `ReadOnlySpan<ReadOnlyMemory<byte>>`, `Diff(update, stateVector, ...)`, `Convert(update, from, to, ...)`, `EncodeStateVector(update, ...)`, `ParseMeta` (an `UpdateMeta` record: `From` and `To` clocks of every client), `Decode` (a `DecodedUpdate`: the structs and the delete set as `DeletedRange(Clock, Length)`), `Describe` (text, for debugging). No document is needed. |
| `Snapshot` | `Encode(format)`, `Encode(buffer, format)` (built in an array first), `Decode(bytes, format)`, `ContainsUpdate(update, format, limits)`; equality is `Equals`, and `Snapshot.Equals(a, b)` for null-safe |
| `SyncProtocol` | `WriteSyncStep2` and `ReadSyncMessage` take a `format` (both ends must agree) |

`Merge` of a single update returns a copy of it, unchecked, as Yjs returns it.

## Untrusted input

[Untrusted input and threads](untrusted-input.md) is the short form, with the defaults of the limits. This section is
the contract. Every API that decodes bytes — `ApplyUpdate`, `TryApplyUpdate`,
`EncodeStateAsUpdate(stateVector)`, everything in `YUpdate`, `Snapshot.Decode`,
`CreateDocFromSnapshot` (a snapshot that holds more than the document),
`Snapshot.ContainsUpdate`, `RelativePosition.Decode`, `RelativePosition.FromJson`, `SyncProtocol.Read…` — throws only
`YUpdateException` for malformed input, with a message that says what was wrong (never the
input's content). The snapshot reads (`ToArraySnapshot`, `GetSnapshot`, `GetAllSnapshot`,
`ToDelta(snapshot)`) ignore deletions of clients or clocks the document does not have, as Yjs does.

**Atomicity.** `ApplyUpdate` decodes the whole update (structs and delete set) before it touches
the document, then plans the integration against the document without changing it (which structs
integrate in which order, which wait for others), and only then carries it out. A failure in
either step leaves the document exactly as it was: same text, state vector and encoding, no events
(an empty transaction may be observed when the failure is found in the planning step); the
exceptions are the conflict-resolution budget below, structs of the update that waited for
pending structs that are dropped (see [pending structs](#pending-structs-retried)), and an item with a
parent whose first clocks the document holds as a GC struct (no peer writes one; Yjs throws a
`TypeError` at the same point), which is rejected when it is reached, with what came before it applied. What is checked
before anything changes:

* every value is in range: client ids up to 2^53 - 1 (as lib0 reads them), and **clocks**: every struct, delete range and
  state-vector entry ends at clock 2^31 - 1 or before (about two billion operations per client id),
  so no clock arithmetic overflows;
* no struct of length 0, no client listed twice, no reference to a later struct of the item's own
  client, no unknown content or type;
* no item whose right origin precedes its origin is refused: **not checked, as in Yjs.** Yjs never writes one and applies
  a crafted one, where the item lands between the halves of a struct split at the right origin; a client that reads
  the whole state in one update places it elsewhere, so a document and a client that joins it show different texts.
  A check on the struct that holds both origins is bypassed by the merge of two structs (the document's own state
  then writes them as one, and every fresh document refuses it) and by an item integrated from an offset, so such a check
  would make the state unloadable. A check on document order would be needed; until then the divergence stays
  (`docs/findings-server.md`, 4);
* no struct that waits for structs the document lacks while the document has a struct under its id already (a part
  of an item is there, so everything the item refers to was). Yjs keeps it waiting, and the structs of its client that
  follow with it, also once the other struct under that id integrated; a client that joins reads the document's
  state, where they no longer wait, and is ahead of the document for good. An update cannot hold two structs under one
  id (a client is listed once, and its structs follow one another by clock), so the two come in updates
  of their own, one waiting. A deliberate divergence: the update that makes the waiting one retry drops it
  (see [pending structs](#pending-structs-retried), reported as partly applied), and the one planned with such a
  struct is rejected, the document unchanged;
* nesting: 'any' and JSON values, and shared types inside each other, at most
  `UpdateLimits.MaxDepth` levels deep (3,000 by default, each counted on its own: about as deep as Yjs'
  recursive walks go before the JavaScript stack runs out, so no Yjs peer writes a deeper document; a
  local insert beyond the document's limit throws `ArgumentException` / `InvalidOperationException`).
  What an update can make Ycs walk, it walks without recursion: decoding and encoding values (binary
  and JSON), deleting and garbage-collecting nested types, `ToJson`; so a hostile update of any depth
  is rejected, and with `UpdateLimits.Unlimited` a document of 100,000 levels is applied, read,
  deleted and encoded, on a thread with a 256 KiB stack. `ToString` of nested XML, `Clone` and the
  UndoManager's redo of nested deleted types recurse once per level of types and throw
  `InsufficientExecutionStackException` where the stack runs out (at 3,000 levels a 1 MiB stack is
  enough for all but `Clone`);
* size: a collection is never allocated larger than the remaining input can fill, and an update holds
  at most `UpdateLimits.MaxStructsPerUpdate` structs (131,072 by default), in either format: the v2
  format run-length encodes its columns (Yjs writes 20,000 deleted items in 42 bytes; 131,072 deleted
  items are 42 bytes too), and a v1 struct can be as small as 4 bytes. The limit is six times Yjs'
  example and can be set to anything above zero. The same limit, as their `limits` argument, bounds
  each input of the `YUpdate` functions; `Convert` from v2 to v1, `Decode` and
  `Describe` of a v2 update expand a v2 update to the size of its structs (131,072 structs are about 0.8 MiB
  of v1, 30 MiB decoded, and a description of 11 million characters);
* garbage collection: in a document that collects garbage, an update whose cleanup would collect a deleted type
  in which a key holds two live items is rejected (Yjs integrates it and then throws "Unexpected case" from
  Yjs' `Item.gc`). Yjs never writes a key's value of several live items; a crafted update can, and splitting such a
  value (by a deleted range, or an origin, inside it) leaves its first part live next to the key's value. When the
  split and the deletion come in one update, where its items land among a key's items is estimated as YATA orders
  them, and a rare crafted update that Yjs applies is rejected too (1 in 20,000 of the structure-aware fuzzer's
  scripts). Without garbage collection both keep the second item, unseen;
* pending data: what waits for structs the document does not have yet holds at most
  `UpdateLimits.MaxPendingStructs` structs (131,072) and `UpdateLimits.MaxPendingBytes` bytes (16 MiB):
  the structs as their v2 encoding counts them, the deletions as their v1 encoding does. The struct
  count bounds what a struct costs whatever its encoding; the bytes bound the content (text, values,
  keys), which both encodings carry as it is. An update that would grow either beyond its limit is
  rejected (a duplicate is merged away first, so an update that only carries again what waits, as a client's
  state sent back, is accepted even where what waits is over a limit). An update that lets pending structs integrate may add
  up to the limits on top of what waits, which is then read again and integrated, wholly or in part;
  so what waits, and what that re-read decodes, never exceeds twice the limits. A peer can hold what waits at
  that bound for good; deletions that wait count once, a range that what waits covers already adds nothing;

What the defaults allow, measured (`dotnet run -c Release --project bench/Ycs.Benchmarks -- limits`;
.NET 11, x64; the updates are valid ones made through the API, so an honest update of that many
structs costs the same):

| One update of 131,072 structs of | v1 / v2 bytes | allocated | kept by the document |
| --- | --- | --- | --- |
| characters, one item each | 0.9 MB / 131 KB | 35 MiB | 30 MiB |
| deleted items | 0.8 MB / 42 B | 38 MiB | 26 MiB |
| shared types (`YText`, `YArray`, `YMap`; `YXmlElement`) | 0.8 MB / 36 B; 1 MB / 131 KB | 88 to 105 MiB | 61 to 73 MiB |
| subdocuments | 5.8 MB / 5 MB | 184 MiB | 139 MiB |
| shared types, pending | 0.8 MB / 35 B | 60 MiB | (encoded, 35 B) |
| an update that lets those integrate and brings 131,072 more that wait (the largest re-read) | 45 B | 332 MiB | 73 MiB |
| GC structs, pending | 23 B | 8 MiB | (encoded) |

So a small update makes a document allocate at most about 340 MiB and keep about 140 MiB, and
`EncodeStateAsUpdate` with 131,072 shared types pending allocates about 120 MiB per call (every
joining peer). Halving the limits halves these.

<a id="pending-structs-kept"></a>**Pending structs, kept.** Yjs keeps what waits as one encoded update and merges
every new remainder into it (`pending.update = mergeUpdatesV2([pending.update, rest])`), which costs the size of
everything pending per update: merged so, 8,000 updates waiting for a missing client take 4.4 s; Ycs takes 38
ms (`dotnet run -c Release --project bench/Ycs.Benchmarks -- perf`, "pending"). Ycs merges a remainder into what waits
at once only when it shares a clock with a pending GC struct or item; the others are kept as they come and merged
once, when they are retried or the state is encoded. That gives Yjs' bytes in every order: where structs overlap, the
merge is not associative (the reader written first keeps writing its contiguous structs, so 1:2-6, then 1:1, then
1:1-4 keep 1:2-6 whole in Yjs and would split it in one merge of all three), so those are merged as Yjs merges them.
For updates that share no clock, one merge equals Yjs' fold: no struct is sliced or skipped, the gaps become the same
Skip structs, items never merge (Yjs' `Item.mergeWith` asks for `this.right === right`, which is null for an item read from
an update), and a run of adjacent GC structs is written as it is up to the first two of it that come from different
updates and joined from there on, which the fold finds in the same place, however the updates are grouped. Checked
against Yjs 13.6.32 in 69,224 orders of 3 to 5 overlapping updates and 200 sets of 64 to 263 (`tests/Ycs.Tests`
replays 1,128 of them, `interop/js/pending-orders.mjs`).

<a id="pending-structs-retried"></a>**Pending structs, retried.** An update that brings what pending structs (from earlier updates)
waited for retries them in its own transaction, after it was carried out, as Yjs does: they are read
again and planned and checked as an update of their own (nesting, garbage collection). When that
plan fails, which happens before anything of them changes the document, they are dropped, and the
update stands: the document is as if they had never arrived (deletions that waited with them keep
waiting). If structs of the update itself waited for the dropped ones, they are dropped with them and
`ApplyUpdate` throws `YUpdateException` with `PartlyApplied` set: the update was cut short. Otherwise the update stood
whole, and `ApplyUpdate` returns normally (it must not blame the sender: whoever forged the waiting structs is not the
peer whose update let them retry); `YDoc.PendingDropped` is raised, inside the transaction, with the number of structs
and the clients they were of, so that a server knows that what it admitted to wait, and handed to joining clients as
waiting structs, is gone: clients with other limits integrate it. They are not kept: kept, every later update that satisfies them would retry and
fail again, and a fresh document could not apply the document's state, which includes what is
pending. The conflict budget counts the retry's steps too; running out there is `PartlyApplied` (below):
what the retry integrated stays, the rest of what waited is dropped. So whatever `ApplyUpdate` throws, or reports,
the document holds a causally complete part of what it was given, whose `EncodeStateAsUpdate` (v1 or
v2) applies to a fresh document, with the same state vector when nothing is pending
(`tests/Ycs.Fuzz` checks this after every input, with the representation invariants of its item lists).

**Trusted input.** A document's own state can hold far more structs than the defaults allow (a million characters
typed one at a time are a million structs). Apply it with `UpdateLimits.Unlimited`, or limits of your own, for that
call ([example](untrusted-input.md#trusted-input)); the limits of an update from an untrusted peer stay the document's.

**Conflict resolution.** Where several clients inserted at the same place, an item passes the
others' items between its origins (Yjs' conflict loop): n items of one client after n of another
take n^2 steps, in Yjs too, and a v2 update of 28 bytes can describe them. Ycs walks those items
without allocating (Yjs fills two sets per item: 40,000 after 40,000 take 97 s and 111 GiB that way, 4.8 s
and 11 MiB in Ycs) and bounds the walk by `UpdateLimits.MaxIntegrationSteps` per update (16,777,216 = 2^24
by default: about 0.1 s, 1.5 s at most where the items' origins branch). Honest updates take far
fewer steps: the Yjs corpus at most 102 per update, the unit tests 18,301, 64 peers that each typed
1,000 characters offline at the same place 33,888 per update when they meet; the largest honest
shape, one peer typing forward where another typed backwards at the same index, takes n·m steps,
and the default admits 4,096 by 4,096. This is the one limit found while the update is integrated:
**beyond it, the update is applied in part.** `ApplyUpdate` throws `YUpdateException` with
`PartlyApplied` set (`TryApplyUpdate` returns false); the structs integrated before stay (a causally
complete part of the update, as if the peer had sent less), observers and `Updated` see them, and the
rest of the structs and the update's deletions are dropped. The document is valid, and converges
with any peer that has the same part; a peer that sends the rest again is refused again.

After these checks the integration does not fail on input alone; an exception from it other than
the one above would be a bug in Ycs and cannot be rolled back. What can still happen is
`OutOfMemoryException`: the limits bound one update (see the table above), not a document, which
grows with every update it accepts, and not a process that holds many documents. Structurally odd but
well-formed updates (an item whose two origins are in different parents, say) are integrated as Yjs
integrates them. Observers run after the update was applied; an exception from one propagates to the
caller of `ApplyUpdate` after the other observers ran, and the document stays usable, also after an
exception of a document handler (see [Exceptions of handlers](#exceptions-of-handlers)).

## Exceptions of handlers

An exception of an observer or a document handler is never swallowed: it escapes from the outermost
`Transact` (or `ApplyUpdate`, or the call that made the change) once the cleanup of the batch of
transactions is finished. The rules:

- **Every step of the cleanup runs**, also after a handler threw: the observers, the deep observers,
  `AfterTransaction`, the formatting cleanup (Yjs' `callAll`), then the garbage collection and merging
  of structs, `AfterTransactionCleanup`, `Updated`, `UpdatedV2`, the subdocument bookkeeping, `SubdocsChanged`,
  destroying the removed subdocuments, the cleanup of the transactions queued behind (those observers
  started), and `AfterAllTransactions`.
- **Several exceptions: the last one escapes** (in the order above; one of `AfterAllTransactions` last of
  all), as Yjs' `callAll` and `finally` blocks let the last one through. The others are dropped.
- **Within one event**, a throwing handler skips that event's later handlers (Yjs' `emit`, and a .NET
  multicast delegate): they miss that one call. A throwing `BeforeObserverCalls` handler skips every
  observer, deep observer, `AfterTransaction` handler and the formatting cleanup of that transaction (as
  Yjs). A throwing `BeforeAllTransactions` handler skips `BeforeTransaction` of that transaction.
- **A throwing `BeforeAllTransactions` or `BeforeTransaction` handler** stops the transaction before its
  function runs: the function is not called (for `ApplyUpdate`, the update is not applied), nothing
  changes, the transaction is cleaned up as an empty one (`AfterTransaction`, `AfterTransactionCleanup`,
  `AfterAllTransactions` fire; no observer and no update event), and the exception escapes.
- An exception of an observer or a handler of the cleanup replaces one of `Transact`'s function (as Yjs).

**This differs from Yjs on purpose.** Yjs 13.6 (and 14) emits `afterTransactionCleanup`, `update`,
`updateV2` and `subdocs` unprotected, before it resets its queue of transactions, and `beforeAllTransactions`
and `beforeTransaction` before its `try`. A handler that throws there leaves the Yjs document broken for
good, silently: the transaction stays open or the queue is never cleaned up again, so later changes apply
but no observer and no update event fires, nothing is collected or merged, and the queue grows
([yjs#723](https://github.com/yjs/yjs/issues/723), [yjs#799](https://github.com/yjs/yjs/issues/799)).
The open pull requests [yjs#802](https://github.com/yjs/yjs/pull/802) and
[yjs#803](https://github.com/yjs/yjs/pull/803) fix it the same way: finish the cleanup, still rethrow;
Ycs follows #803 (which also skips `BeforeTransaction` after a throwing `BeforeAllTransactions`).
`ThrowingObserverTests` replays what Yjs 13.6.32 does in each case (`interop/js/throwing-observers.mjs`)
and states, for these six, "Yjs: document broken; Ycs: recovers"; `HandlerRecoveryTests` checks the
recovery.

**What the application must still do.** Recovery keeps the document usable, not the update delivered:
when an `Update` (or `UpdateV2`) handler throws, the handlers after it did not get that update, and a
provider among them did not send it. Peers that miss it integrate the later updates only once it
arrives (they wait as pending), so resync them: send the full state, or answer their state vector with
`EncodeStateAsUpdate(stateVector)` (y-protocols' sync step 2). Better, do not throw from these handlers.

## Thread safety

As in Yjs, a `YDoc` and everything in it is not thread-safe: calls into a document, reads included, must not run
concurrently, and handlers run synchronously on the thread that made the change. How a server serializes access:
[Thread safety](untrusted-input.md#thread-safety).
