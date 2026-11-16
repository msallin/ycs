# Ycs.Client

y-websocket's provider in C#, over `ClientWebSocket`: keeps a `YDoc` in sync with a room of a y-websocket server
(such as `samples/Ycs.Server`), reconnects with y-websocket's backoff, and keeps this client's awareness state. It runs
on .NET and in the browser (Blazor WebAssembly).

| File | What it is |
| --- | --- |
| `WebsocketProvider.cs` | the provider |
| `ClientAwareness.cs`, `Presence.cs` | this client's awareness, and the users the editor bindings announce |
| `SharedText.cs`, `TextDiff.cs` | a `Y.Text` bound to a plain textarea |
| `Wire.cs` | the y-websocket message framing |

The types are `internal`: copy the files, with `samples/Ycs.Server/Awareness/Awareness.cs` and `AwarenessChange.cs`.
Usage: [docs/networking.md](../../docs/networking.md#the-net-client). Tests: `dotnet test tests/Ycs.Client.Tests`.
