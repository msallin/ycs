Ycs
-------

Ycs is a .NET implementation of [Yjs](https://github.com/yjs/yjs), the CRDT for shared editing. A Ycs document and a
Yjs document exchange the same bytes, so a .NET server, desktop app or Blazor WebAssembly page can edit the same
text, map or XML tree as Yjs clients in browsers, and every copy converges without a central lock. Ycs is pure C#:
no native library and no reflection, so it runs under Native AOT and in WebAssembly.

This is a modernized fork of [yjs/ycs](https://github.com/yjs/ycs), at parity with Yjs 13.6.

Documentation: [msallin.github.io/ycs](https://msallin.github.io/ycs/) (the same pages are in [`docs/`](docs/index.md)).

## Install

Ycs is not on NuGet. It needs the .NET 11 SDK. Clone it (or add it as a git submodule) and reference the project:

```xml
<ProjectReference Include="path/to/ycs/src/Ycs/Ycs.csproj" />
```

Or build a package for a local feed: `dotnet pack src/Ycs -c Release -o ./packages` (package `Ycs`, version 0.0.1).

## Example

```csharp
using Ycs;

var alice = new YDoc();
var bob = new YDoc();

// Each local change raises Updated with the bytes to send to the other replicas.
alice.Updated += (_, e) => { if (e.Origin != bob) bob.ApplyUpdate(e.Update.ToArray(), alice); };
bob.Updated += (_, e) => { if (e.Origin != alice) alice.ApplyUpdate(e.Update.ToArray(), bob); };

alice.GetText("content").Insert(0, "Hello");
bob.GetText("content").Insert(5, ", world");

Console.WriteLine(alice.GetText("content")); // Hello, world
Console.WriteLine(bob.GetText("content"));   // Hello, world
```

## At a glance

| | |
| --- | --- |
| Shared types | `YText`, `YMap`, `YArray`, `YXmlFragment`, `YXmlElement`, `YXmlText`, `YXmlHook`, subdocuments |
| Also | undo and redo, snapshots, relative positions, merging and diffing updates without a document |
| Wire formats | Yjs update formats v1 (the default) and v2, y-protocols sync; the same bytes as Yjs |
| Tested against | Yjs 13.6.32 and 13.6.33; Yjs 14.0.0-16 and `@y/y` 14.0.0-rc.30 |
| Untrusted input | a malformed or hostile update throws `YUpdateException` and leaves the document unchanged; its cost is bounded by `UpdateLimits` |
| Threads | a document is not thread-safe, as in Yjs: one lock or one channel per document |
| Runtime | .NET 11; Native AOT and trimming without warnings; Blazor WebAssembly |
| Speed | the automerge-paper trace (260k edits) replays in about 0.4 s |

## Documentation

- [Getting started](docs/getting-started.md): documents, shared types, syncing, events, undo, saving.
- [Networking](docs/networking.md): the y-protocols sync, the sample sync server, .NET and browser clients.
- [Compatibility](docs/compatibility.md): Yjs versions, wire formats, what is not supported, performance.
- [Untrusted input and threads](docs/untrusted-input.md): limits, errors, thread safety.
- [API reference](docs/api.md): the API compared with Yjs and YDotNet, and every contract in detail.

## Contributing

`dotnet test tests/Ycs.Tests` runs the main suite without Node. [`docs/testing.md`](docs/testing.md) says what each
suite checks and how to run the interop checks against Yjs. The design notes and walkthroughs are in
[`docs/`](docs/).

## License

MIT, see [LICENSE](LICENSE).
