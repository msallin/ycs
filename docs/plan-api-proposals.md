# API proposals, not done

A working document: changes to the public API that would be improvements but change many call sites or the wire
behaviour, so they wait for a discussion with the maintainers.

- `SyncProtocol` still reads and writes `Stream`s. A span/`IBufferWriter<byte>` version, or an
  `IAsyncEnumerable` over a `PipeReader`, would fit modern servers better.
- Several public types expose mutable public fields (`ID.Client`, `Delta.Insert`,
  `RelativePosition.Assoc`, `StackItem.Meta`, …). Properties would be safer; it is a breaking change.
- `RelativePosition` is a class of 80 bytes (two `ID?` fields of 24 bytes each). A `readonly struct`
  would cost nothing per position, but `Assoc` is a settable field, `CompareRelativePositions` and
  `AbsolutePosition.TryCreateFromRelativePosition` accept `null`, and callers keep positions in
  collections by reference, so it is a breaking change.
- `YMap.Get` and `TryGet` return `object`. Typed accessors (`TryGet<T>(key, out T value)`) would
  remove casts without introducing YDotNet's `Output` wrapper.
- Nullable annotations cover the encoding layer only; the public types are not annotated yet.
- Clocks up to 2^53 - 1, as Yjs reads them (Ycs rejects clocks beyond 2^31 - 1, see api.md, Untrusted input). `ID.Clock` is a
  `long` already; what is `int` is a struct's and a content's `Length` (public on `ItemView` and `DecodedStruct`) and the offsets derived from clocks (nine `(int)` narrowings in `StructStore`, `EncodingUtils`,
  `AbsolutePosition`; `SplitItem`, `Integrate`, `Write` and `Splice` take an `int` offset). Moving them to `long`
  changes the public API, adds 4 bytes to every struct, and needs every narrowing checked; a GC or deleted run longer
  than 2^31 - 1 clocks is the only content that would need it (strings and lists cannot be that long).
