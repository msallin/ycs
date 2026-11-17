# Walkthrough of the shared types and their events against Yjs 13.6.32

How the shared types and their events map to Yjs 13.6.32: `types/YType.js`, `YArray.js`, `YMap.js`, `YText.js`, `YXml*.js`,
`utils/YEvent.js`, `utils/EventHandler.js` and the observer part of `utils/Transaction.js`, against `src/Ycs/Types/**`,
`Utils/YEvent*.cs` and the observer part of `Utils/Transaction.cs`. The core is in
[walkthrough-core.md](walkthrough-core.md). For the public shape of the types see [api.md](api.md).

`AbstractType` in the code is a global alias of the public class `YType`. A type holds its items as `ItemHandle`s into its
document's slab (`YType._startHeld`, `_itemHeld`, the `_map` values), not as references to item objects; `ItemRef` is what
code reads and writes them through ([design-item-layout.md](design-item-layout.md)).

Verdicts: **identical** (the same control flow), **equivalent** (different code, no observable difference), **differs**
(see [Differences from Yjs](#differences-from-yjs)), **not ported**, **port-only**.

## Function by function

### YType.js

| JS | C# | Verdict |
| --- | --- | --- |
| `ArraySearchMarker`, `refreshMarkerTimestamp`, `overwriteMarker` | `YArrayBase.ArraySearchMarker`, `Update` | equivalent (a per-collection counter: only the order inside one collection is ever compared) |
| `markPosition` | `ArraySearchMarkerCollection.MarkPosition` | identical (the tie rule of `reduce` is kept) |
| `findMarker` | `YArrayBase.FindMarker` | identical. The walk to the left over items that continue each other by clock is Yjs' (where Yjs leaves markers stale, the item a marker sits on decides where an edit lands), through the run-head index of `ClientStructs` instead of item by item; it ends at the same item, also over a run extended in place |
| `updateMarkerChanges` | `ArraySearchMarkerCollection.UpdateMarkerChanges` | identical; a marker an insert moves off a deleted or uncountable item lands on Yjs' item: the transaction's runs extended in place are split back first, and the caller takes the item left of its insert again (`LeftOf`) |
| marker move in `Item#mergeWith` | `YArrayBase.OnMerge` | identical (it runs before the length is added, as in JS) |
| `getTypeChildren` | none | not ported (an internal helper) |
| `callTypeObservers` | `YType.CallTypeObservers` | identical, but only called when observed |
| `YType` constructor, `_item`, `_map`, `_start`, `_length`, `doc` | the fields of `YType` | equivalent (`_mapCount` and the depth are port-only) |
| `parent` | `Parent` | identical |
| `_integrate` | `Integrate` | equivalent (also sets the depth and the map count) |
| `_copy`, `clone`, `_write` | `InternalCopy`, `InternalClone`, `Write` | identical |
| `_first` | `_First` | identical |
| `_callObserver` (marker reset on a remote change) | `YArrayBase.CallObserver` | identical |
| `observe`, `unobserve`, `observeDeep`, `unobserveDeep` | `Observe` on each type, `ObserveDeep`; each returns an `IDisposable` (`YType.Subscription`) | differs: subscriptions |
| `toJSON` | `ToJson` | identical |
| `typeListSlice` | `YArrayBase.InternalSlice` | differs: ranges |
| `typeListToArray` | `YArray.ToArray`, `YXmlFragment.ToArray` | equivalent (the fragment's skips only deleted items, not uncountable ones; none occur) |
| `typeListToArraySnapshot` | `ToArray(snapshot)` (`YArrayBase.ContentAtSnapshot`) | identical |
| `typeListForEach`, `typeListMap`, `typeListCreateIterator` | `YArrayBase.EnumerateRaw`, `YArray.EnumerateValues`, the enumerators | identical, but for what the caller's code does between two values: Yjs' orphaned item (merged away by a cleanup) continues with its old right neighbor; Ycs holds the item's id and continues from the struct that holds it now, which is the same unless the list changed right of it since, and ends when the item's type was collected |
| `typeListForEachSnapshot` | `ContentAtSnapshot` | identical |
| `typeListGet` | the `YArray` indexer and `TryGetValue` (`TryGetRawAt`) | identical |
| `typeListInsertGenericsAfter` | `YArrayBase.InsertGenericsAfterReturningLast` | equivalent |
| `typeListInsertGenerics` | `YArrayBase.InsertGenerics` | equivalent (an index beyond the length throws, as in Yjs) |
| `typeListPushGenerics` | `YArrayBase.PushGenerics` | identical (appends after the last item, deleted ones included; the walk starts from the item the last push made, not from the highest marker) |
| `typeListDelete` | `YArrayBase.Delete` | identical |
| `typeMapDelete`, `typeMapSet` | `TypeMapDelete`, `TypeMapSet` | identical (the value is validated by `AnyEncoding.Validate`) |
| `typeMapGet`, `typeMapHas` | `TryTypeMapGet`, `YMap.TryGetValue`, `ContainsKey` | identical |
| `typeMapGetAll` | `TypeMapEnumerateValues` | identical |
| `typeMapGetSnapshot`, `typeMapGetAllSnapshot` | `YMap.TryGetValue(key, snapshot, ...)`, `ToDictionary(snapshot)` | equivalent (a collected value reads as absent) |
| `createMapIterator` | `TypeMapEnumerate` | identical |
| `warnPrematureAccess` | none | not ported (a console warning) |

### YArray.js

| JS | C# | Verdict |
| --- | --- | --- |
| constructor, `_prelimContent` | `YArray()`, `YArray(ReadOnlySpan<YValue>)`, the same field | identical |
| `_integrate` | `Integrate` | equivalent (the prelim list is cleared before the flush, for the virtual length) |
| `_copy`, `clone` | `InternalCopy`, `InternalClone`, `Clone` | equivalent |
| `length` | `Count` | equivalent (before integration it is the prelim count; Yjs returns 0 with a warning) |
| `_callObserver` | `CallObserver` | identical |
| `insert`, `push` | `Insert`, `Add` | differs: ranges (`Add` is `PushGenerics`) |
| `unshift` | `Insert(0, ...)` | identical |
| `delete` | `RemoveAt`, `RemoveRange` | identical |
| `get`, `toArray`, `map`, `forEach`, the iterator, `toJSON` | the indexer, `ToArray`, `IEnumerable<YValue>`, `ToJson` | identical |
| `slice` | `Slice` | differs: ranges |
| `_write`, `readYArray` | `Write`, `Read` | identical |
| `YArrayEvent` | `YArrayEvent` | identical |

### YMap.js

| JS | C# | Verdict |
| --- | --- | --- |
| constructor with entries | `YMap()`, `YMap(entries)` | identical |
| `_integrate`, `_copy`, `clone` | `Integrate`, `InternalCopy`, `InternalClone`, `Clone` | identical |
| `_callObserver` | `CallObserver` | identical |
| `toJSON` | `ToJson` | identical |
| `size` | `Count` (O(1), from `_mapCount`) | equivalent (the count equals `size` after every step of the traces: a concurrent set of one key, a delete of a deleted key, the collection of nested types, splits) |
| `keys`, `values`, `entries`, `forEach`, the iterator | `Keys`, `Values`, `IEnumerable<KeyValuePair<string, YValue>>` | identical |
| `delete`, `clear`, `has` | `Remove`, `Clear`, `ContainsKey` | identical |
| `set` | the indexer setter, `Add` | equivalent (returns nothing) |
| `get` | the indexer, `TryGetValue` | differs: a missing key |
| `_write`, `readYMap` | `Write`, `Read` | identical |
| `YMapEvent` (`keysChanged`) | `YMapEvent.KeysChanged` | identical |

### YText.js

| JS | C# | Verdict |
| --- | --- | --- |
| `equalAttrs`, `equalFlat` | `YText.EqualAttrs`, `EqualFlat` | equivalent for the values a decoder produces |
| `ItemTextListPosition`, `forward` | `YText.ItemTextListPosition`, `Forward` | identical |
| `findNextPosition`, `findPosition` | `FindNextPosition`, `FindPosition` | identical |
| `insertNegatedAttributes`, `updateCurrentAttributes`, `minimizeAttributeChanges`, `insertAttributes` | `InsertNegatedAttributes`, `UpdateCurrentAttributes`, `MinimizeAttributeChanges`, `InsertAttributes` | identical (Yjs' key order of an attribute map is kept, `InJavaScriptKeyOrder`) |
| `insertText` | `InsertText` | identical (an embedded shared type is content, as in Yjs) |
| `formatText` | `FormatText` | identical |
| `cleanupFormattingGap`, `cleanupContextlessFormattingGap` | `CleanupFormattingGap`, `CleanupContextlessFormattingGap` | identical (`===` is `EqualFlat`'s strict comparison, `??` is `GetAttribute`) |
| `cleanupYTextFormatting` | `CleanupFormatting` | identical |
| `cleanupYTextAfterTransaction` | `CleanupAfterTransaction` | identical (including Yjs passing the end clock as a length) |
| `deleteText` | `DeleteText` | equivalent |
| `YTextEvent` constructor, `keysChanged`, `childListChanged` | `YTextEvent`, computed lazily | identical |
| `YTextEvent#changes`, `#delta` | `YEvent.Changes`, `YTextEvent.Delta` | identical |
| `YText` constructor, `_pending` | `PendingOperation`, `AddPending`, `ReplayPending` | equivalent (see [Differences](#differences-from-yjs)) |
| `length` | `Length` | identical |
| `_integrate` | `Integrate` | equivalent |
| `_copy`, `clone` | `InternalCopy`, `InternalClone`, `Clone` | identical |
| `_callObserver`, `_hasFormatting`, `_needFormattingCleanup` | `CallObserver`, `HasFormatting` | identical |
| `toString`, `toJSON` | `ToString` (cached), `ToJson` | equivalent (the cache was checked against a fresh `CopyTo` after random edits, undo, remote updates, snapshot reads and garbage collection) |
| `applyDelta` with `sanitize` and the trailing newline rule | `ApplyDelta` | identical (Yjs' inverted `sanitize` included) |
| `toDelta` | `ToDelta`, `ComputeDelta` | identical (it opens no transaction without a snapshot; a text outside a document answers) |
| `insert`, `insertEmbed`, `delete`, `format` | `Insert`, `InsertEmbed`, `Remove`, `Format` (`InsertInTransaction`, `DeleteInTransaction`) | identical |
| `removeAttribute`, `setAttribute`, `getAttribute`, `getAttributes` | `RemoveAttribute`, `SetAttribute`, `GetAttribute`, `GetAttributes` | identical |
| `_write`, `readYText` | `Write`, `Read` | identical |
| none | `RemoveNonText`, `CopyTo`, `WriteTo` | port-only |

### YXml*.js

| JS | C# | Verdict |
| --- | --- | --- |
| `YXmlTreeWalker` (`next`) | `YXmlFragment.CreateTreeWalker`, a lazy `IEnumerable<YType>` | identical, but a walker standing in a type that was collected since the last call ends, where Yjs' orphaned node walks up through its old parent |
| `YXmlFragment` constructor, `firstChild`, `_integrate`, `_copy`, `clone`, `length` | `YXmlFragment()`, `FirstChild`, `Integrate`, `InternalCopy`, `InternalClone`, `Count` | identical |
| `createTreeWalker`, `querySelector`, `querySelectorAll` | `CreateTreeWalker`, `QuerySelector`, `QuerySelectorAll` | identical |
| `_callObserver` | `CallObserver` | identical |
| `toString`, `toJSON` | `ToString`, `ToJson` | identical (a format with a string value renders as indexed attributes, as in Yjs) |
| `insert`, `insertAfter`, `push`, `unshift`, `delete`, `get`, `toArray`, `forEach` | `Insert`, `InsertAfter`, `Add`, `Insert(0, ...)`, `RemoveAt`, `RemoveRange`, the indexer, `ToArray`, the enumerator | identical |
| `slice` | `Slice` | differs: ranges |
| `toDOM` (fragment, element, text, hook) | none | not ported |
| `_write`, `readYXmlFragment` | `Write`, `Read` | identical |
| `YXmlElement` constructor, `nextSibling`, `prevSibling`, `_integrate`, `_copy`, `clone` | `YXmlElement(nodeName)`, `NextSibling`, `PrevSibling`, `Integrate`, `InternalCopy`, `InternalClone` | identical |
| `removeAttribute`, `setAttribute`, `getAttribute`, `hasAttribute`, `getAttributes(snapshot)` | `RemoveAttribute`, `SetAttribute`, `GetAttribute`, `HasAttribute`, `GetAttributes(snapshot)` | identical |
| `YXmlText`, `YXmlHook` constructors, siblings, `_copy`, `clone`, `_write`, `readYXmlText`, `readYXmlHook` | the same names | identical |
| `YXmlEvent` | `YXmlEvent`, computed lazily | identical |

### YEvent.js, EventHandler.js, observer part of Transaction.js

| JS | C# | Verdict |
| --- | --- | --- |
| `YEvent` constructor, `target`, `currentTarget`, `transaction` | `Target`, `CurrentTarget`, `Transaction` | identical |
| `path`, `getPathTo` | `Path` (`GetPathTo`) | equivalent (not cached; Yjs resets its cache per current target) |
| `deletes`, `adds` | `Deletes`, `Adds` | identical |
| `keys` | `Keys` (`CollectKeys`) | equivalent (a collected old value reads as null) |
| `changes` (added, deleted, delta) | `Changes` (`CollectChanges`) | identical |
| `delta` | `Delta` | identical |
| "must not compute changes after the event handler fired" | `Transaction.Ended` | equivalent (the port also refuses `Delta`, `Adds` and `Deletes`) |
| `EventHandler`, `createEventHandler`, `addEventHandlerListener`, `removeEventHandlerListener` | .NET events and `IDisposable` subscriptions | differs: subscriptions |
| `removeAllEventHandlerListeners` | none | not ported (internal) |
| `callEventHandlerListeners` (`callAll`) | `YType.CallAll` | identical (every handler runs; the last exception escapes) |
| `cleanupTransactions`: observer calls in `transaction.changed` order | `CleanupTransaction`, the first loop | identical (the changed set keeps insertion order) |
| deep observers: the `_dEH` check, the filter for deleted targets, `currentTarget`, the stable sort, the call | the second loop (`CallDeepEventHandlerListeners`) | identical |
| `afterTransaction`, then the formatting cleanup | the same | identical |
| `tryGcDeleteSet`, `tryMergeDeleteSet`, `tryToMergeWithLefts`, the merge of `_mergeStructs` | `DeleteSet.TryGcDeleteSet`, `TryMergeDeleteSet`, `TryToMergeWithLefts` | equivalent (see [walkthrough-core.md](walkthrough-core.md#merging)) |
| `ContentType#delete`, `#gc`, `ContentFormat#integrate`, `ContentString#splice` and `mergeWith`, `ContentEmbed` | the same | identical or equivalent |

## Differences from Yjs

- **Ranges and missing keys throw.** A range that is not within a `YArray` or `YXmlFragment` throws
  `ArgumentOutOfRangeException` before anything changes: `Slice(2, ...)` past the end, where Yjs' `slice(2, 1)` returns `[]`,
  and an `Insert` index beyond the count, also on a type that is not in a document yet (Yjs appends there). A `YMap` indexer
  read of a missing key throws `KeyNotFoundException`; `TryGetValue` is the lookup that does not throw.
- **Subscriptions are `IDisposable`.** Disposing a subscription removes that one registration; a handler registered twice is
  two registrations (Yjs' `unobserve` removes both), and Yjs logs an error for a handler it does not know.
- **Events exist only when observed.** `IsObserved` is evaluated when a type's turn comes in the cleanup, and an event is
  built only then. `Transaction.ChangedParentTypes` is empty for a document whose only listeners are `Updated`, `UpdatedV2`
  or `BeforeObserverCalls`.
- **A pending `applyDelta(delta, sanitize: false)`** on a text that is not in a document yet keeps its option; Yjs replays it
  as `applyDelta(delta)` and loses it.
- **Not ported:** `toDOM` (there is no DOM), `getTypeChildren`, `removeAllEventHandlerListeners`, the premature-access
  warning, and `set` returning the value.

## Not compared

- `UndoManager` together with the formatting cleanup and with events (`StackItemAdded`, `StackItemPopped` and their
  `ChangedParentTypes` copies).
- XML types under random concurrent edits beyond the differential suites: they share the list, map and text code that the
  traces cover.
- Negative indices, and indices beyond the length for `YText` (Yjs appends silently).
- Markers on a root text that got formats while it was still an untyped placeholder (`text-placeholder` traces cover the
  common case).
