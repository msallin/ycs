# Ycs

Ycs is a .NET implementation of [Yjs](https://github.com/yjs/yjs), the CRDT for shared editing. A Ycs document and a Yjs
document exchange the same bytes, so a .NET server, desktop app or Blazor WebAssembly page can edit the same text, map or
XML tree as Yjs clients in browsers, and every copy converges without a central lock. Ycs is pure C#: no native library
and no reflection, so it runs under Native AOT and in WebAssembly.

## Features

- **Shared types:** `YText` (with formatting and embeds), `YMap`, `YArray`, `YXmlFragment`, `YXmlElement`, `YXmlText`,
  `YXmlHook`, nested types and subdocuments.
- **Yjs compatibility:** update formats v1 and v2 and the y-protocols sync protocol, byte for byte. Tested against Yjs
  13.6.32 and 13.6.33, and against Yjs 14 (`@y/y`) on the wire.
- **Also:** undo and redo, snapshots, relative positions, and merging, diffing and converting updates without a document.
- **Untrusted input:** a malformed or hostile update throws `YUpdateException` and leaves the document unchanged; what one
  update may cost is bounded by `UpdateLimits`. Input spans are read in place and not kept.
- **Runtime:** .NET 11, Native AOT and trimming without warnings, Blazor WebAssembly. A document is not thread-safe, as
  in Yjs: use one lock or one channel per document.

## Example

```csharp
using Ycs;

var alice = new YDoc();
var bob = new YDoc();

// Each local change raises Updated with the bytes to send to the other replicas.
alice.Updated += (_, e) => { if (e.Origin != bob) bob.ApplyUpdate(e.Update.Span, alice); };
bob.Updated += (_, e) => { if (e.Origin != alice) alice.ApplyUpdate(e.Update.Span, bob); };

alice.GetText("content").Insert(0, "Hello");
bob.GetText("content").Insert(5, ", world");

Console.WriteLine(alice.GetText("content")); // Hello, world
Console.WriteLine(bob.GetText("content"));   // Hello, world
```

## Use it

Ycs needs the .NET 11 SDK. Clone the repository (or add it as a git submodule) and reference the project:

```xml
<ProjectReference Include="path/to/ycs/src/Ycs/Ycs.csproj" />
```

## Documentation

- [Getting started](docs/getting-started.md): documents, shared types, syncing, events, undo, saving.
- [Networking](docs/networking.md): the y-protocols sync, the sample sync server, the .NET and browser clients.
- [Compatibility](docs/compatibility.md): Yjs versions, wire formats, what is not supported, performance.
- [Untrusted input and threads](docs/untrusted-input.md): limits, errors, thread safety.
- [API reference](docs/api.md): the public API and every contract in detail.

For contributors:

- [Testing](docs/testing.md): what each test project guards, how to run it, and the `YCS_*` switches.
- [Item layout](docs/design-item-layout.md): how the store holds items (slots in a slab linked by handles, characters and values in arenas, updates decoded into staging tables).
- Walkthroughs of the code against the Yjs source: [core](docs/walkthrough-core.md),
  [shared types and events](docs/walkthrough-types.md),
  [undo, snapshots, positions and protocols](docs/walkthrough-undo-snapshots-protocols.md).

## Build and test

```bash
dotnet build Ycs.slnx
dotnet test tests/Ycs.Tests -c Release       # the main suite; no Node needed
```

The other projects (`tests/Ycs.Corpus`, `Ycs.Fuzz`, `Ycs.Properties`, `Ycs.Budgets`, `Ycs.Server.Tests`,
`Ycs.Client.Tests`) and the interop checks against real Yjs (Node 24) are described in [docs/testing.md](docs/testing.md).
`dotnet run --project samples/Ycs.Server` starts a sync server with a demo editor on `http://localhost:5000/`.

## License

MIT, see [LICENSE](LICENSE).
