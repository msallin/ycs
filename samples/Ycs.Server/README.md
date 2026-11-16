# Ycs.Server

A Yjs sync server on Ycs: one document per room, over WebSocket (y-websocket's wire, `/ws/{room}`) and SignalR
(`/hub?room={room}`), with y-protocols' awareness. It also serves a demo editor.

```bash
dotnet run --project samples/Ycs.Server
```

Open `http://localhost:5000/` for the demo page. Clients, settings and what the server does not do (persistence,
authentication): [docs/networking.md](../../docs/networking.md#the-sync-server).

| Folder | What is in it |
| --- | --- |
| `Rooms/` | a room (one `YDoc`, one lock), the registry, a session (the protocol, without a transport) |
| `Awareness/` | the port of y-protocols' awareness |
| `Transports/` | the WebSocket endpoint and the SignalR hub |
| `wwwroot/` | the demo page; its source is in `demo/` |

Tests: `dotnet test tests/Ycs.Server.Tests -c Release`, and against real Yjs clients
[`interop/server`](../../interop/server/README.md).
