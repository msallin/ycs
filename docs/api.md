# API reference

The public API of Ycs and the contracts a caller relies on. Ycs keeps the concepts and names of
[Yjs](https://github.com/yjs/yjs) (13.6), so the Yjs documentation explains the model; this page is about the C# shape.
Everything is in the namespace `Ycs`. The exact list of public members is `tests/Ycs.Tests/PublicApi.txt`.

- **Indexes** are `int`. For text they count UTF-16 code units, as in Yjs and in .NET strings.
- **Values** are `YValue`s ([YValue](#yvalue)).
- **Transactions** are implicit: `doc.Transact(tr => ..., origin)` groups changes ([Transactions](#transactions)).
- **Update format**: v1 by default, `UpdateFormat.V2` as a `format` argument wherever bytes go in or out.
- **Subscriptions** return `IDisposable`; the document raises .NET events.
- **Threads**: a document is not thread-safe ([Thread safety](#thread-safety)).

Contents: [Documents](#documents-and-options), [YValue](#yvalue), [Shared types](#shared-types),
[Events](#events), [Transactions](#transactions), [Encoding and updates](#encoding-and-updates),
[Positions](#positions), [Undo](#undo), [Sync protocol](#sync-protocol),
[Item views and decoded structs](#item-views-and-decoded-structs),
[Limits and untrusted input](#limits-and-untrusted-input), [Thread safety](#thread-safety).

## Documents and options

`YDoc` holds the shared types of one document. `doc.GetText(name)`, `GetMap`, `GetArray`, `GetXmlFragment`,
`GetXmlText` return the root type of that name, created on first use; peers that use the same name
and type share it. The default name is the empty string.

`YDocOptions` is a sealed record with `init` properties (`with` copies one):

| Property | Meaning |
| --- | --- |
| `GarbageCollection` | collect deleted content; set it to false for a document whose `Snapshot`s you read |
| `GarbageCollectionFilter` | `Func<ItemView, bool>`: true lets a deleted item be collected |
| `Limits` | the `UpdateLimits` for every update this document applies ([Limits](#limits-and-untrusted-input)) |
| `ClientId` | the client id; checked when it is set |
| `Guid`, `CollectionId` | the document's identity as a subdocument |
| `AutoLoad`, `ShouldLoad` | subdocument loading |
| `Meta` | a `YValue` of your own that the subdocument carries |

A document keeps what it was made with and shows it as read-only properties (`GarbageCollection`, `Limits`, `ClientId`,
`Guid`, `CollectionId`, `AutoLoad`, `ShouldLoad`, `Meta`). Without a `Guid` the document makes a random one when it is
first read, so two documents made from one options object differ.

```csharp
var doc = new YDoc(new YDocOptions { GarbageCollection = false });
```

Other members of `YDoc`: `HasPending` (structs wait for others the document lacks), `CreateSnapshot()`,
`CreateDocFromSnapshot(snapshot, newDoc?)`, the update and encoding methods ([Encoding and updates](#encoding-and-updates)),
the events ([Events](#events)), and for subdocuments `GetSubdocGuids()`, `Load()` and `Destroy()` with the `Destroyed`
event. A document is a value too: `YValue` converts from a `YDoc`, and inserting it makes a subdocument; `SubdocsChanged`
raises a `SubdocsEventArgs(Loaded, Added, Removed)` of read-only sets.

`PermanentUserData(doc)` maps client ids to users of that document: `SetUserMapping(clientId, user, filter?)`,
`GetUserByClientId`, `GetUserByDeletedId`; `Doc` is the document.

## YValue

`YValue` is the value of every shared type: a `readonly struct`, a closed set of kinds (`YValueKind`): `Null`, `Boolean`,
`Number`, `BigInteger`, `String`, `Bytes`, `Array`, `Object`, `SharedType` and `Document`. Plain scalars convert
implicitly, so `map["theme"] = "dark"` and `array.Add(1, "two")` read as they should.

| To make one | |
| --- | --- |
| implicit conversion | from `bool`, `int`, `long`, `double`, `BigInteger`, `string`, `byte[]`, a `YType` (a shared type) or a `YDoc` |
| `YValue.Null`, `YValue.True`, `YValue.False` | the constants |
| `YValue.Array(items)`, `YValue.Object(members)`, `YValue.Bytes(bytes)` | containers and binary data from spans |
| `YValue.FromObject(object?)` | from the CLR values `null`, `bool`, `int`, `long`, `double`, `BigInteger`, `string`, `byte[]`, lists, string-keyed dictionaries, and shared types; anything else is rejected |

To read one: `Kind`, `IsNull`, `GetBoolean`, `GetInt32`, `GetInt64`, `GetDouble`, `GetBigInteger`, `GetString`,
`GetBytes` (these throw when the kind does not fit) and the `TryGet...` form of each (`TryGetBoolean`, `TryGetInt32`,
`TryGetInt64`, `TryGetDouble`, `TryGetBigInteger`, `TryGetString`, `TryGetBytes`); explicit conversions to the same
scalar types; `TryGetSharedType<T>(out T? value)` (`YType` for any shared type) and `TryGetDocument`; for arrays `Length`, the
`int` indexer and `EnumerateArray()`; for objects `MemberCount`, the `string` indexer, `TryGetMember(name, out value)`
and `EnumerateObject()`; and `ToObject()`, the CLR value back.

A number inserted encodes as Yjs encodes a JavaScript number (an integer within 31 bits as a varint, otherwise a float32
or a float64); one read back is a `YValue` number whatever its wire form. A shared type is a value too:
`array[0].TryGetSharedType<YMap>(out var map)`.

## Shared types

`YType` is the base of every shared type: `Doc`, `Parent`, `ToJson()` and `ObserveDeep(handler)`. A type made with `new`
is not in a document yet: its content can be read, changed and then inserted into an array or map (or set as a value).

### YArray

`YArray` is an `IReadOnlyList<YValue>`: `Count`, the indexer (an `ArgumentOutOfRangeException` for an index out of range),
`TryGetValue(index, out value)`, `Add(params ReadOnlySpan<YValue>)` (Yjs' `push`), `Insert(index, params
ReadOnlySpan<YValue>)`, `RemoveAt(index)`, `RemoveRange(index, count)`, `Slice(start, length)` (so `array[1..3]` works),
`IndexOf(value)`, `Contains(value)` and `Remove(value)` (the first occurrence; true when found), which compare by value, so
a shared type is found only as the same instance, `ToArray()`, `ToArray(snapshot)`, `Clone()` and `ToJson()`.
`new YArray(1, "two")` and `YArray a = [1, 2, 3]` build one that is not in a document yet. `Add` and `Insert` take the
values to add: `array.Add([1, 2])` appends two values, 1 and 2, not one nested array; for a nested array write
`array.Add(new YArray(1, 2))`.
A range that is not within the array throws before anything changes.

### YMap

`YMap` is an `IReadOnlyDictionary<string, YValue>`: `Count`, the indexer (it throws for a missing key when read;
setting is Yjs' `set`), `TryGetValue(key, out value)` and `TryGetValue(key, snapshot, out value)`, `ContainsKey`,
`Remove(key)` (false when the key was missing), `Clear()`, `Keys`, `Values`, `ToDictionary(snapshot)`, `Clone()`,
`Add(key, value)` for collection initializers (`new YMap { ["theme"] = "dark" }`) and a constructor over entries.

### YText

`YText` (and `YXmlText`, which derives from it): `Length`, `Insert(index, string, attributes?)`, `InsertEmbed(index, YValue,
attributes?)`, `Remove(index, length)`, `Format(index, length, attributes)`, `ApplyDelta(IEnumerable<Delta>, sanitize)`,
`ToDelta()`, `ToDelta(snapshot, previous?, computeYChange?)`, `RemoveNonText()`, `CleanupFormatting()`, `ToString()`,
`CopyTo(Span<char>)` (returns the characters written; an `ArgumentException` when the span is shorter than the text, and
`Length` is always enough), `WriteTo(IBufferWriter<char>)`, the text's own attributes (`GetAttribute` is null when unset,
`GetAttributes`, `SetAttribute`, `RemoveAttribute`) and `Clone`. Formatting attributes and embeds are `YValue`s; in the
attribute dictionary of `Format` or of a `Retain`, a `YValue.Null` removes the attribute.

A `Delta` is one operation of a delta, one of the sealed cases `Delta.Insert(YValue Value)` (a string, or an embed: a
shared type or a value; an embed of null is `YValue.Null`), `Delta.InsertItems(IReadOnlyList<YValue> Items)` (what an
array's or an XML fragment's event inserted), `Delta.Retain(int Length)` and `Delta.Delete(int Length)`, with `Attributes`
(`IReadOnlyDictionary<string, YValue>?`) on every case. `ToDelta(snapshot, previous)` marks added and removed runs with a
`ychange` attribute, an object of `type` (`"added"` or `"removed"`) and, when `computeYChange` returns a `YChange(YChangeKind,
long? User)` with one, `user`. Applying an `InsertItems` to a text throws.

### XML

`YXmlFragment` (and `YXmlElement`, which derives from it) is an `IReadOnlyList<YType>` of its children: `Count`, the
indexer, `TryGetValue`, `FirstChild`, `Add`, `Insert`, `InsertAfter(reference, ...)` (all `params ReadOnlySpan<YType>`,
which throw `ArgumentException` for a child that is not a `YXmlElement`, `YXmlFragment`, `YXmlText` or `YXmlHook`),
`RemoveAt`, `RemoveRange`, `Slice`, `ToArray()`, `ToArray(snapshot)`, `CreateTreeWalker(filter?)` (a lazy
`IEnumerable<YType>` of the descendants in document order), `QuerySelector(query)` and `QuerySelectorAll(query)`. A child
that is none of those, which only another client can have written, counts in `Count` but is not a node: `TryGetValue`
returns false for it, the indexer throws `InvalidOperationException`, and enumeration, `Slice` and `ToArray` leave it out.
There is no root element: a document has `GetXmlFragment(name)` (an element needs a node name, which is not part of the
wire format of a root type), so `fragment.Add(new YXmlElement("article"))` for an element.

`YXmlElement` has `NodeName`, `NextSibling`, `PrevSibling` and attributes as `YValue`s: `GetAttribute` (null when unset),
`SetAttribute`, `RemoveAttribute`, `HasAttribute` and `GetAttributes(snapshot?)`. `YXmlText` is a `YText` with
`NextSibling` and `PrevSibling`. `YXmlHook` is a `YMap` with a `HookName`.
A node that was removed and collected still answers `NextSibling`, `PrevSibling` and `InsertAfter(node, ...)` from the place it
held; one collected together with its parent is in no list: its siblings are null and `InsertAfter` throws `ArgumentException`.

## Events

Each shared type observes its own events: `YArray.Observe(Action<YArrayEvent>)`, `YMap` (`YMapEvent`), `YText` and
`YXmlText` (`YTextEvent`), `YXmlFragment` and `YXmlElement` (`YXmlEvent`). `YType.ObserveDeep(Action<IReadOnlyList<YEvent>,
Transaction>)` gets the events of the type and everything nested in it, the outer types' first. Both return an
`IDisposable`; disposing it removes that registration (one handler registered twice is two registrations). A handler may
be typed more generally (an `Action<YEvent>` observes any type). Every handler runs even when one threw; the last
exception escapes.

An event is read-only: `Target`, `CurrentTarget`, `Transaction`, `Path` (an `IReadOnlyList<PathSegment>`: a map key, or the
index in a list when `Key` is null), `Changes` (`YChanges`: `Added`, `Deleted`, `Delta`, `Keys`), `Delta`, `Keys`
(`IReadOnlyDictionary<string, KeyChange>`: a `KeyChangeAction` and the old value, an `object?`), and `Adds(view)`,
`Deletes(view)`. The subclasses add `YMapEvent.KeysChanged`, `YTextEvent.KeysChanged` and `ChildListChanged`, and
`YXmlEvent.AttributesChanged` and `ChildListChanged`. An event is valid during the handler, its transaction's observers
and cleanup; compute what you keep there.

On `YDoc`, the events are:

| Event | Argument |
| --- | --- |
| `Updated`, `UpdatedV2` | `UpdateEventArgs(Update, Origin, Transaction)`: `Update` is a new `ReadOnlyMemory<byte>` for each event, which a handler may keep |
| `BeforeAllTransactions` | none |
| `BeforeTransaction`, `BeforeObserverCalls`, `AfterTransaction`, `AfterTransactionCleanup` | the `Transaction` |
| `AfterAllTransactions` | an `IReadOnlyList<Transaction>` |
| `SubdocsChanged` | `SubdocsEventArgs` |
| `PendingDropped` | `PendingDrop(Structs, Clients)` ([Pending structs, retried](#pending-structs-retried)) |
| `Destroyed` | none |

### Exceptions of handlers

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
when an `Updated` (or `UpdatedV2`) handler throws, the handlers after it did not get that update, and a
provider among them did not send it. Peers that miss it integrate the later updates only once it
arrives (they wait as pending), so resync them: send the full state, or answer their state vector with
`EncodeStateAsUpdate(stateVector)` (y-protocols' sync step 2). Better, do not throw from these handlers.

## Transactions

`doc.Transact(tr => ..., origin, local)` runs an `Action<Transaction>`; `Transact<TResult>(Func<Transaction, TResult>)`
returns what the function returns; `Transact<TState>(state, static (s, tr) => ...)` passes its state in, so that a
`static` lambda allocates no closure. Nested calls join the running transaction. Without `Transact`, each change is its
own transaction. `origin` is passed to every handler of the change, so a handler can tell its own changes from those it
received; `local` says whether the transaction is a local change.

A `Transaction` (sealed) is read-only but for `Meta`: `Doc`, `Origin`, `Local`, `BeforeState` and `AfterState`
(`IReadOnlyDictionary<long, long>`), `Changed` (`IReadOnlyDictionary<YType, IReadOnlySet<string?>>`: the changed map keys,
`null` for the children of a list), `ChangedParentTypes` (`IReadOnlyDictionary<YType, IReadOnlyList<YEvent>>`),
`SubdocsAdded`, `SubdocsRemoved` and `SubdocsLoaded` (`IReadOnlySet<YDoc>`), and `Meta`, a mutable
`IDictionary<string, object?>` for what observers pass each other (Yjs' `transaction.meta`). The views read the
transaction's own collections without a copy; they are valid as long as the transaction is ([Events](#events)).

Transactions are synchronous. A lambda that awaits is a compile error (CS0619: the overloads that take a `Func<..., Task>`
are `[Obsolete(error: true)]` guards), where it would otherwise be an `async void` that ends the transaction at its first
`await`. Two cases the guards cannot tell from an async lambda: a lambda that returns a `Task` (it is no async lambda, but
it is no transaction either), and a lambda that only throws, with no `return`, which the compiler can also convert to
`Func<Transaction, Task>`: cast it, `(Action<Transaction>)(tr => throw ...)`.

## Encoding and updates

`enum UpdateFormat { V1, V2 }` picks the encoding wherever bytes go in or out; v1 is the default (what every y-protocols
provider sends), v2 is denser. Inputs are spans, and a method that decodes an input needs an array internally, so it
copies the span into a buffer of the shared array pool for the call: no allocation beyond the pool's. A byte array
converts to a span implicitly.

| On | Members |
| --- | --- |
| `YDoc` | `ApplyUpdate(update, origin = null, format = V1, limits = null)` and `TryApplyUpdate` (the same, returning false for a malformed update); `EncodeStateAsUpdate(stateVector = default, format = V1)` returning `byte[]`, or with an `IBufferWriter<byte>` first; `EncodeStateVector()` (the same bytes for both formats), also into an `IBufferWriter<byte>`; `CreateSnapshot`, `CreateDocFromSnapshot` |
| `YUpdate` | works on update bytes, no document needed: `Merge(updates, format, limits)` over a `ReadOnlySpan<ReadOnlyMemory<byte>>`, `Diff(update, stateVector, ...)`, `Convert(update, from, to, ...)`, `EncodeStateVector(update, ...)`, `ParseMeta` (an `UpdateMeta`: `From` and `To` clocks of every client; clocks count the positions of the id space, not structs, so a struct of a run covers several), `Decode` (a `DecodedUpdate`, see [Item views and decoded structs](#item-views-and-decoded-structs)), `Describe` (text, for debugging) |
| `Snapshot` | `Encode(format)`, `Encode(buffer, format)` (built in an array first), `Decode(bytes, format)`, `ContainsUpdate(update, format, limits)`, `Snapshot.Empty`; equality is `Equals`, `==` and `!=` (a decoded snapshot equals the one it was encoded from) |
| `SyncProtocol` | `WriteSyncStep2` and `ReadSyncMessage` take a `format` (both ends must agree); `ReadSyncMessage` takes `origin = null` as `ApplyUpdate` does |

`Merge` of a single update returns a copy of it, unchecked, as Yjs returns it.

A `Snapshot` is a state vector and a delete set: the document at a moment. Create one with `doc.CreateSnapshot()`, read the
document as it was with the `snapshot` overloads (`YArray.ToArray(snapshot)`, `YMap.TryGetValue(key, snapshot, out value)`,
`YMap.ToDictionary(snapshot)`, `YXmlElement.GetAttributes(snapshot)`, `YXmlFragment.ToArray(snapshot)`,
`YText.ToDelta(snapshot)`) or rebuild it with `CreateDocFromSnapshot`. Snapshots only mean something on a document made
with `GarbageCollection = false`, as in Yjs.

Every API that decodes bytes throws only `YUpdateException` for malformed input ([Limits and untrusted
input](#limits-and-untrusted-input)).

## Positions

`RelativePosition` is a `readonly record struct` that keeps pointing at the same character (or the end of the same
type) while the document changes; `AbsolutePosition(Type, Index, Assoc)` is the index it resolves to at a moment. Both
are values: equal positions are equal keys of a `HashSet` or `Dictionary`. `Assoc` is an enum: `After` (0, the position
sticks to the character after it) or `Before` (-1, to the one before); a peer's integer reads as `Before` when negative,
else as `After`.

```csharp
var position = RelativePosition.FromIndex(text, 3);          // Assoc.After by default, or Assoc.Before
text.Insert(0, "abc");
if (position.TryResolve(doc, out var absolute))              // false: names a struct the document lacks or collected
{
    // absolute.Type, absolute.Index (6), absolute.Assoc
}

byte[] bytes = position.Encode();                            // Yjs' encodeRelativePosition; awareness carries it
var same = RelativePosition.Decode(bytes);                   // YUpdateException for malformed bytes; same == position
```

A position carries only what the bytes carry: the item when there is one, else the root type's name, else the id of the
type. Yjs' `createRelativePositionFromTypeIndex` sets the type or root name as well and drops it on encoding; here
`FromIndex` does not set it, so `Decode(Encode(p)) == p` and `FromJson(p.ToJsonString()) == p`.

`Encode(IBufferWriter<byte>)` writes into a buffer. `WriteJson(Utf8JsonWriter)` and `FromJson(JsonElement)` are Yjs'
`relativePositionToJSON` object (`{"item":{"client":1,"clock":0},"assoc":0}`; `type`, `tname` and `item` only where
set); `ToJsonString()` and `FromJson(string)` are the one-line form, for an awareness state. `TryResolve(doc, out position, followUndoneDeletions: false)` gives the same result on every client; the
default follows undone deletions, which suits the user who undid. `default(RelativePosition)` names nothing: it does
not resolve and does not encode.

## Undo

`new UndoManager(type)`, `new UndoManager(types)` and `new UndoManager(doc)` choose the scope: a type with its children, a
list of types, or every change of the document; each takes an optional `UndoManagerOptions`. `AddToScope` extends the
scope; adding the document makes the manager track the whole document (`TracksWholeDocument`; `Scope` lists the types
only). `Undo()` and `Redo()` return the `StackItem`, or null when there was nothing to do; `CanUndo`, `CanRedo`, `UndoStack`,
`RedoStack`, `CurrentStackItem`, `StopCapturing()`, `Clear(undoStack, redoStack)`, `AddTrackedOrigin`,
`RemoveTrackedOrigin`. A `StackItem` has a `Meta` dictionary of your own. The manager is `IDisposable`: dispose it to
unhook it from the document.

`UndoManagerOptions` is a sealed record with `init` properties:

| Property | Meaning |
| --- | --- |
| `TrackedOrigins` | the transaction origins to track; by default only the null origin, so apply a peer's update with an origin and it is never undone |
| `CaptureTimeout` | a `TimeSpan` (500 ms by default): changes within it form one step |
| `TimeProvider` | the clock of `CaptureTimeout`; a test moves it by hand |
| `CaptureTransaction` | `Func<Transaction, bool>`: false keeps a transaction out of the stack |
| `DeleteFilter` | `Func<ItemView, bool>`: false keeps an undo or redo from deleting the item |
| `IgnoreRemoteMapChanges` | true: an undo or redo overwrites a map key (or attribute) that another change set since; by default it leaves such a key alone. Also a settable property of the manager |

The events `StackItemAdded`, `StackItemUpdated` and `StackItemPopped` carry an `UndoStackEventArgs(StackItem, Operation,
ChangedParentTypes, Origin)` (`Operation` is an `UndoOperation`, `Undo` or `Redo`); `StackCleared` carries an
`UndoStackClearedEventArgs(UndoStackCleared, RedoStackCleared)`.

## Sync protocol

`SyncProtocol` is the y-protocols `sync` message format, which y-websocket and every Yjs provider puts on the wire: a
`SyncMessageType` (`SyncStep1`, `SyncStep2`, `Update`) and a length-prefixed payload. The writers append to an
`IBufferWriter<byte>` and keep nothing:

- `WriteSyncStep1(destination, doc)`: the document's state vector, which asks the peer for what the document lacks.
- `WriteSyncStep2(destination, doc, encodedStateVector, format)`: what a peer with that state vector lacks.
- `WriteUpdate(destination, update)`: one update.

`ReadSyncMessage(message, reply, doc, origin = null, format = V1)` handles one whole message (a `ReadOnlySpan<byte>` or a
`ReadOnlySequence<byte>`; bytes after it are ignored) and returns a `SyncReadResult(Type, Replied)`. A step 1 appends the answer, a step 2, to
`reply`; a step 2 or an update is applied to `doc` with `origin` and writes nothing. A truncated message, an unknown
type, or an update that is malformed or beyond a limit throws `YUpdateException`, with the document as it was. Both ends
must agree on `format`.

y-websocket puts a message type in front of every message: `SyncProtocol.MessageSync` (0) or `MessageAwareness` (1).
`WriteMessageType(destination, type)` appends it and `ReadMessageType(message, out body)` strips it. [Networking](networking.md) shows a server and a client built on it. Awareness is not part of the
library.

## Item views and decoded structs

Events and filters name a document's content by `ItemView`, and a decoded update by `DecodedStruct`. Neither
exposes the storage, which is internal.

| Type | What it is |
| --- | --- |
| `ItemView` | a `readonly struct`: a document and an id. It reads the struct that holds the id now, as if split at the id. Valid as long as the document, across splits, merges and garbage collection. `default(ItemView)` (`IsDefault`) names nothing: every member throws `InvalidOperationException` |
| `ItemId` | a `readonly record struct` `ItemId(Client, Clock)`, both `long` |
| `ItemKind` | what a struct holds: `String`, `Any`, `Json`, `Binary`, `Embed`, `Format`, `Type`, `Doc`, `Deleted`, `GC`, `Skip` (the numbers of the update format) |
| `DecodedStruct` | a sealed record of `init` properties (`Id`, `Length` and `Kind` are `required`): one struct of a decoded update, in the update's order, detached from any document |
| `DecodedUpdate` | what `YUpdate.Decode` returns: `Structs` (`IReadOnlyList<DecodedStruct>`) and `DeleteSet` (the deleted ranges of each client as `DeletedRange(Clock, Length)`) |

`ItemView` members: `Id`, `LastId`, `Length`, `Kind`, `Content` (the values, a read-only list), `Deleted`,
`Countable`, `Keep`, `LeftOrigin`, `RightOrigin`, `Left`, `Right` (`ItemView?`), `Parent`, `ParentSub`, `Redone`, `Doc`.
`Length` is a `long`. `Content` is `IReadOnlyList<object?>`, a diagnostic read of the stored values. A GC struct reads
`Parent` null, empty `Content`, `Deleted` true.

`DecodedStruct` members: `Id`, `Length` (a `long`), `Kind`, `LeftOrigin`, `RightOrigin`, `ParentName` (a root type) or
`ParentId`, `ParentSub`, and what the kind carries, in exactly one place: `Content` (Any, Json, Binary, Embed), `Text`
(String), `FormatKey` and `FormatValue` (Format), `TypeRef` and `TypeName` (Type), `DocGuid` (Doc). The others are
empty, null or -1.

`ParentName` and `ParentId` are both null for an item with an origin: the update leaves the parent out, because the
origin's item says it. An item has them only when it has neither origin.

| Where | Gets |
| --- | --- |
| `YChanges.Added`, `.Deleted` | `IReadOnlySet<ItemView>` |
| `YEvent.Adds(view)`, `Deletes(view)` | whether the event added or deleted it, also when both |
| `YDocOptions.GarbageCollectionFilter` | `Func<ItemView, bool>`: true lets a deleted item be collected |
| `UndoManagerOptions.DeleteFilter` | `Func<ItemView, bool>`: false keeps an undo or redo from deleting the item |
| `YUpdate.Decode` | `DecodedUpdate.Structs`: `IReadOnlyList<DecodedStruct>` |

```csharp
var doc = new YDoc(new YDocOptions
{
    ClientId = 1,
    // Collect deleted text; keep deleted shared types (true collects).
    GarbageCollectionFilter = item => item.Kind != ItemKind.Type,
});
var list = doc.GetArray("list");

list.Observe(e =>
{
    foreach (ItemView item in e.Changes.Added)
    {
        Console.WriteLine($"added {item.Kind}, {item.Length} at {item.Id.Client}:{item.Id.Clock}");
    }
});

list.Add("a", "b");
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

## Limits and untrusted input

<a id="untrusted-input"></a>
[Untrusted input and threads](untrusted-input.md) is the short form, with the defaults of the limits. This section is
the contract. Every API that decodes bytes — `ApplyUpdate`, `TryApplyUpdate`,
`EncodeStateAsUpdate(stateVector)`, everything in `YUpdate`, `Snapshot.Decode`,
`CreateDocFromSnapshot` (a snapshot that holds more than the document),
`Snapshot.ContainsUpdate`, `RelativePosition.Decode`, `RelativePosition.FromJson`, `SyncProtocol.ReadSyncMessage` — throws only
`YUpdateException` for malformed input, with a message that says what was wrong (never the
input's content). The snapshot reads ignore deletions of clients or clocks the document does not have, as Yjs does.

`UpdateLimits` is an immutable sealed record: `MaxStructsPerUpdate`, `MaxPendingStructs`, `MaxPendingBytes`,
`MaxIntegrationSteps`, `MaxDepth`, and the instances `UpdateLimits.Default` and `UpdateLimits.Unlimited`. A document has
`YDoc.Limits` (from `YDocOptions.Limits`); `ApplyUpdate`, `TryApplyUpdate` and every `YUpdate` function take a `limits`
argument that replaces them for one call.

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

Ycs retries a pending struct whenever any clock it waited for arrives. Yjs 13.6.32 keeps one missing clock per client in
`pending.missing` and ends with the higher one, so when a lower clock arrives last it does not retry, and a struct stays
pending although every dependency is present; Ycs integrates it, and its state vector and update events are ahead of Yjs'
(recorded as `yjs-pending-retry-deferred`, `tests/Ycs.Properties/KnownFailures.cs`).

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

## Thread safety

As in Yjs, a `YDoc` and everything in it is not thread-safe: calls into a document, reads included, must not run
concurrently, and handlers run synchronously on the thread that made the change. How a server serializes access:
[Thread safety](untrusted-input.md#thread-safety).
