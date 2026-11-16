Sync server interop
-------

Unmodified Yjs clients (`y-websocket`'s `WebsocketProvider` over `ws`, in Node) against `samples/Ycs.Server` and
against the reference server, `@y/websocket-server`. `npm ci` once here (the scripts do it when `node_modules` is
missing); `servers.sh` holds the build and start of both servers on free ports, shared by the scripts.

## smoke

`./smoke.sh` builds the Ycs server, starts it and runs `smoke.mjs`: two providers sync, a text typed in one appears in
the other, an awareness state crosses, and a destroyed or dropped (`ws.terminate()`) client's state is removed within
2 s. A few seconds.

## converge

`./converge.sh [seeds] [first seed] [converge.mjs options]` builds the Ycs server, starts it and the reference server,
and runs `converge.mjs` with the same seeds against each (`TARGETS=ycs` or `TARGETS=node` for one). It exits non-zero
when any seed fails on any server. The default, 32 seeds from 1, takes about 15 s per server, 40 s with the build.

From the seed (mulberry32, no dependency), 6 clients (`--clients`) in one or two rooms (`--rooms`) take 40 to 120
steps (`--steps`); up to two of them join after the start. A step is one of:

- an edit in a transaction, or a transaction of 2 to 5 edits: `Y.Text` insert (some with `bold` or `italic`), delete,
  format or unformat a range; `Y.Map` set (scalars, objects, arrays, a nested `Y.Map` or `Y.Array`) or delete a key;
  `Y.Array` insert (some nested types) or delete; the same edits in a nested map or array;
- `provider.disconnect()` (the client keeps editing offline) or `provider.connect()`;
- an abrupt drop: `ws.terminate()` on the provider's socket (the provider reconnects after its backoff);
- a client leaves (once connected and synced, so what it made has reached the server) and a fresh doc joins in its
  place, or a late client joins;
- an awareness change; a wait of up to 40 ms. Between steps the driver yields, and sometimes waits up to 15 ms, so
  messages are in flight while clients edit.

Then every client reconnects and the driver waits for quiescence (all synced, no message for 300 ms, 20 s at most)
and checks each room:

1. every client has the same state vector and content (the delta of the text, the JSON of the map and the array,
   keys sorted) as every other;
2. the oracle (below) has that state vector and content;
3. every connected client sees exactly the other connected clients' last awareness states, and none of the clients
   that left, within 2 s;
4. a fresh client joining the room gets the same state vector and content.

On a failure it prints the server, the seed, its steps, clients and rooms, the step it failed at, the first
difference, and the replay commands; for a server that lost one update in ten:

```
FAIL ycs seed 1: 40 steps, 7 clients, 1 room(s), failed at step 40
    clients differ in ycs-s1-...-r0: c0 vs c2
    /array/4: 781 != 110
    replay: TARGETS=ycs interop/server/converge.sh 1 1
        or: node interop/server/converge.mjs --url ws://127.0.0.1:40067/ws --first 1 --seeds 1 --parallel 1 --verbose
```

`--verbose` logs the joins, leaves, disconnects and drops. The seed fixes the choices; positions and lengths depend on
what has arrived, so a race may need a few replays (`--seeds 1` in a loop, or `--parallel 8` for more load). To
minimise, pass smaller `--clients`, `--rooms` and `--steps`. `CONVERGE_DUMP=<dir>` writes the updates of a room whose
oracle differs.

Seeds run 4 at a time (`--parallel`), each in rooms of its own. A soak: `./converge.sh 500` (under 3 min per server),
or a longer one with a different first seed.

### The oracle

Every update a client made locally (`doc.on('update')` with an origin other than its provider), including those of
clients that left, applied in a shuffled order to a plain Yjs doc. Yjs 13.6.33 leaves some complete sets of updates
pending in some orders: it retries its pending structs only when an update raises a missing clock, and an update that
fills a gap while itself waiting is merged into them without a retry (7 updates of 4 clients are enough). The oracle
then applies the set a second time. On a difference the driver also says whether the order in which the updates were
made gives the clients' state.

The oracle catches what the client comparison cannot: an update lost by every client alike (the clients would agree
with each other). In the runs so far it caught only the Yjs ordering quirk above; the clients never agreed on a state
that lacked an update.

### Awareness after a reconnect

A provider that disconnects (or whose connection the server closes) is removed from the others' awareness at its
current clock, and on reconnecting it re-sends its state at that same clock, which y-protocols ignores until the
client's 15 s renewal. Both servers behave so. The driver does not chase it: after quiescence every client changes its
awareness once (`final: true`), and only then does check 3 run.

## Regression seeds

Seeds that failed on the Ycs server and passed on the reference; each must pass now.

| seed | options | what failed | replay |
| --- | --- | --- | --- |
| none yet | | | |
