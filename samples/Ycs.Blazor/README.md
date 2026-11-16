# Ycs.Blazor

A Blazor WebAssembly page that edits a text with Yjs clients through the sync server, Ycs running in the browser
(on `samples/Ycs.Client`).

```bash
dotnet publish samples/Ycs.Blazor -c Release -o out/blazor
dotnet run --project samples/Ycs.Server -- --webroot out/blazor/wwwroot
```

Open `http://localhost:5000/#my-room`: the fragment is the room, `?server=ws://…/ws` another server. More:
[docs/networking.md](../../docs/networking.md#the-blazor-client). `interop/blazor/run.sh` checks it in Chromium against
a y-websocket client.
