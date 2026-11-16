# Ycs

Ycs is a .NET implementation of [Yjs](https://github.com/yjs/yjs), the CRDT for shared editing. A Ycs document and a
Yjs document exchange the same bytes, so a .NET server, desktop app or Blazor WebAssembly page can edit the same
text, map or XML tree as Yjs clients in browsers, and every copy converges without a central lock. Ycs is pure C#:
no native library and no reflection, so it runs under Native AOT and in WebAssembly.

```csharp
var doc = new YDoc();
doc.GetText("content").Insert(0, "Hello");
byte[] update = doc.EncodeStateAsUpdate();   // what a Yjs peer applies with Y.applyUpdate
```

## Pages

| Page | What is in it |
| --- | --- |
| [Getting started](getting-started.md) | install, documents and shared types, syncing two documents, events, undo, saving |
| [Networking](networking.md) | the y-protocols sync in C#, the sample sync server, the .NET client, Blazor, the demo page |
| [Compatibility](compatibility.md) | tested Yjs versions, wire formats, what is not supported, performance, Ycs or YDotNet |
| [Untrusted input and threads](untrusted-input.md) | what a hostile update can cost, the limits, errors, thread safety |
| [API reference](api.md) | the API compared with Yjs and YDotNet, and every contract in detail |

## For contributors

The code is on [GitHub](https://github.com/msallin/ycs). `dotnet test tests/Ycs.Tests` runs the main suite without
Node; [testing.md](https://github.com/msallin/ycs/blob/modernize/docs/testing.md) says what each suite checks and how to
run the interop checks against Yjs. The design notes and code walkthroughs are the other files in
[`docs/`](https://github.com/msallin/ycs/tree/modernize/docs).
