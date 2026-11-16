# Plan: a modern public API

Ycs has never shipped a package, so the public API breaks freely: no compatibility, no migration notes. The goal is an
idiomatic .NET 11 API with the latest C# features where they fit. This plan replaces `plan-api-proposals.md`.

## What is wrong today

* **Bugs**: `RelativePosition` and `Snapshot` implement `IEquatable<T>` without overriding `Equals(object)` and
  `GetHashCode` (wrong in `HashSet`/`Dictionary`); map and array values are stored and returned by reference, so a
  caller who mutates a `Dictionary`, `List` or `byte[]` after inserting or reading it changes what the document
  encodes later (replicas can diverge).
* **Names**: `ID` (an abbreviation), `AbstractType` (not abstract); one concept spelled several ways (`Count`/`Size`/
  `Length`, `Has`/`ContainsKey`, `Push`/`Add`, `Get`/indexer, V1/V2 aliases of the same method); JavaScript names
  (`Map`, `ForEach`, `Unshift`, `Entries()`, `Slice(start, end)`, `ToArray()` returning `IList`).
* **Nullability**: every public type file has `#nullable disable`; members that return null are not annotated.
* **Shapes**: `ID` a mutable struct; many unsealed classes; public mutable fields (`ID`, `Delta`, `ChangesCollection`,
  `ChangeKey`, `RelativePosition.Assoc`, `AbsolutePosition`); public setters on `YEvent`.
* **Values typed `object`**: the closed value set is checked only at run time; numbers from Yjs arrive as `int`, `long`
  or `double` depending on the encoding, so `(long)map.Get("n")` can throw.
* **Collections**: concrete and mutable collections in parameters, returns and event payloads.
* **Events**: two mechanisms per type (C# events and `Observe`/`Unobserve`), untyped `YEvent`, tuples as event args, a
  shared mutable `byte[]` in `Update`.
* **Encoding**: six `ApplyUpdate*`, six `EncodeStateAsUpdate*`; `UpdateOperations` and `Snapshot` take `byte[]` only;
  `LogUpdate` writes to the console; `SyncProtocol` works on `Stream` with an error callback.
* **Options**: mutable; `CaptureTimeout` an `int` of milliseconds; no `TimeProvider`.

## Decisions (2026-11-16, the planner's recommendations; the operator may revisit)

1. .NET names where a collection idiom exists (`Add`, `Insert`, `RemoveAt`, `RemoveRange`, `Remove`, `Count`, the
   indexer, `TryGetValue`, `ContainsKey`, `Clear`, `Slice(start, length)`); Yjs names where .NET has none
   (`Transact`, `Observe`, `ToDelta`, `ApplyDelta`, `Format`, `EncodeStateAsUpdate`, `CreateSnapshot`).
2. **`YValue`**: a readonly struct shaped like `JsonElement` over the closed value set (null, boolean, number, big
   integer, string, bytes, array, object, shared type, document), with JavaScript number coercion, frozen containers
   (copied on insert and on decode), `FromObject`/`ToObject` bridges; no reflection. Shaped so that it can become a
   C# `union` later.
3. An `UpdateFormat` parameter (default V1) instead of `…V2` methods; the events stay `Updated` and `UpdatedV2`.
4. Typed `IDisposable Observe(Action<TEvent>)` and `ObserveDeep` on the shared types; C# events on `YDoc` and
   `UndoManager`.
5. Transactions stay lambdas (`Action`, `Func<TResult>`, `TState` with `allows ref struct`), with a compile-time
   error for an `async` lambda; no disposable scope (observer exceptions would have to come out of `Dispose`).
6. `ID` becomes `ItemId` (`readonly record struct`), `AbstractType` becomes `YType` (closed hierarchy,
   `private protected` constructor); internally `global using` aliases until layout step 9 ends.
7. `RelativePosition` a `readonly record struct`; `AbsolutePosition` too; `TryResolve`.
8. `Updated` hands out a fresh `ReadOnlyMemory<byte>` a handler may keep.
9. `YDoc` keeps `Destroy()` (not `IDisposable`); `UndoManager` becomes `IDisposable`.
10. `Length` of `ItemView` and `DecodedStruct` is `long` now; internals stay `int` (the layout work decides).
11. Awareness stays in the samples.

## The shape (core)

* `YType` (was `AbstractType`): `Doc`, `Parent`, `ObserveDeep`, abstract `ToJson`; no type events, no `Unobserve`,
  no `Length`.
* `YArray : IReadOnlyList<YValue>` (sealed): `Count`, indexer (throws out of range), `TryGetValue`, `Add`, `Insert`
  (`params ReadOnlySpan<YValue>`), `RemoveAt`, `RemoveRange`, `Slice(start, length)`, `ToArray`, `ToArray(Snapshot)`,
  `Clone`, `Observe(Action<YArrayEvent>)`.
* `YMap : IReadOnlyDictionary<string, YValue>`: indexer (get throws, set is Yjs' set), `TryGetValue` (also at a
  snapshot), `ContainsKey`, `Remove`, `Clear`, `Keys`, `Values`, `ToDictionary(Snapshot)`, `Clone`, `Observe`.
* `YText`: `Length`, `Insert`, `InsertEmbed`, `Remove`, `Format`, `ApplyDelta`, `ToDelta` (also at snapshots),
  `RemoveNonText`, `CleanupFormatting`, `ToString`, `CopyTo(Span<char>)`, `WriteTo(IBufferWriter<char>)`,
  attributes as `YValue`, `Observe(Action<YTextEvent>)`; `YChange` a record struct.
* `Delta`: an abstract record with sealed cases `Insert(YValue)`, `InsertItems`, `Retain`, `Delete`; attributes on the
  base.
* XML: children typed `YType`; `CreateTreeWalker` an iterator; `QuerySelector`/`QuerySelectorAll`; attributes as
  `YValue`.
* Events: `YEvent` read-only (`Target`, `CurrentTarget`, `Transaction`, `Path` of `PathSegment`, `Changes`, `Delta`,
  `Keys`, `Adds`, `Deletes`) with sealed `YArrayEvent`, `YMapEvent`, `YTextEvent`, `YXmlEvent`; `YChanges`,
  `KeyChange`.
* `YDoc` (sealed): `GetText`/`GetArray`/`GetMap`/`GetXml*`; `Transact`; `ApplyUpdate`/`TryApplyUpdate` on
  `ReadOnlySpan<byte>` with `UpdateFormat` and `UpdateLimits?`; `EncodeStateAsUpdate`/`EncodeStateVector` returning
  `byte[]` or writing to `IBufferWriter<byte>`; `CreateSnapshot`, `CreateDocFromSnapshot`; events with record-struct
  args (`UpdateEventArgs`, `SubdocsEventArgs`).
* `YDocOptions`, `UndoManagerOptions`, `UpdateLimits`: sealed records with `init`; `GarbageCollectionFilter`,
  `TimeSpan CaptureTimeout`, `TimeProvider`.
* `Transaction` (sealed): read-only views; `Meta` stays a mutable dictionary.
* `Snapshot` (sealed): `Encode`/`Decode`/`ContainsUpdate`, equality fixed.
* `YUpdate` (replaces the public `UpdateOperations`): `Merge`, `Diff`, `Convert`, `EncodeStateVector`, `ParseMeta`,
  `Decode`, `Describe`; span in, `byte[]` or buffer writer out.
* `UndoManager` (sealed, `IDisposable`): `CanUndo`, `CanRedo`, typed scope, events with record-struct args.
* `SyncProtocol`: span in, `IBufferWriter<byte>` out, a `ReadOnlySequence<byte>` overload; throws instead of an error
  callback.

## Work packages

Every package: suites green, update bytes unchanged, `PublicApi.txt` regenerated (`YCS_ACCEPT_API=1`), the Native AOT
sample publishes without warnings, its public files annotated, and a rewrite script for the call sites it changes (so a
rebase re-runs the script). Internal signatures stay `object`- and `ID`/`AbstractType`-based: the conversion is in the
API layer, so layout files need no edits. At most three API packages in flight.

| pkg | concern | depends on |
| --- | --- | --- |
| A0 | pure move: the public members of the public types into `*.Api.cs` partials | — |
| A1 | closed hierarchy: `YType` (alias), `private protected`, sealed leaves, accidental members removed | A0 |
| A2 | plain data: `ItemId` (alias), records (`DeletedRange`, `UpdateMeta`, `UpdateLimits`, `KeyChange`), `StackItem` sealed, the equality fix | A0 |
| A3 | `YValue`, frozen containers, `AnyEncoding`/`YJson`/`JsValue` on them, copy on insert | — |
| A4a | `YArray`, `YMap`, `YXmlFragment`/`YXmlElement` shapes; snapshot reads on them | A1, A3 |
| A4b | `YText`, `Delta` records, `YChange`, attributes and embeds as `YValue` | A1, A3 |
| A5 | events: typed `Observe`/`ObserveDeep`, sealed read-only events, `YChanges`, event args records | A0; before 7.3b and 7.6 |
| A6 | transactions: read-only `Transaction`, the `Transact` overloads and the async guard | A5 |
| A7 | encoding: `UpdateFormat`, span entry points, `YUpdate`, `Snapshot` encode/decode | A0, A2; 9.3 (running) adapts on merge |
| A8 | options and undo: records, `TimeSpan`/`TimeProvider`, `IDisposable` undo manager, `PermanentUserData` | A2, A5 |
| A9 | positions as record structs, `TryResolve`, encode/decode/JSON | A2 |
| A10 | `SyncProtocol` on spans; the samples, their tests, `networking.md` | — |
| A11 | `ItemView`/`DecodedStruct`: annotations, `long Length`, non-positional `DecodedStruct` | A2; after 9.3 |
| A12 | docs: `api.md`, `getting-started.md`, `untrusted-input.md` | all |
| A13 | remove the remaining `#nullable disable` from public files; the aliases after layout step 9 | A1-A11 |

Order with the layout work: wave 0 A0 (beside 7.0c, 9.3); wave 1 A1, A2, A3, A10 (beside 7.0c, 7.0d); wave 2 A5, A7,
A9, A11 (beside 7.3a; 7.3b waits for A5); wave 3 A4a, A4b, A6, A8 (beside 7.3b, 7.3c); wave 4 A13, A12, then 7.4, 7.6
(after A6), 7.5, 7.7. Step 8.2 starts after the API track.
