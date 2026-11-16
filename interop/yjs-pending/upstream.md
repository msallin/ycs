Draft comment for yjs/yjs#824 (not posted).

---

Yjs 13 has the same problem as the first case here: pending structs whose dependencies have arrived, never retried. It does not need a clock gap. A sync server's convergence test found it with 7 updates from 4 clients. This is the smallest case, with yjs 13.6.33:

```js
import * as Y from 'yjs'

const m = new Y.Doc(); m.clientID = 1
const c = new Y.Doc(); c.clientID = 2 // must be the higher id
const u = []
const rec = (d, f) => { const sv = Y.encodeStateVector(d); f(); u.push(Y.encodeStateAsUpdate(d, sv)) }
rec(m, () => m.getArray('a').insert(0, ['x'])) // u[0] = 1:0
rec(m, () => m.getArray('a').insert(1, ['y'])) // u[1] = 1:1
Y.applyUpdate(c, Y.encodeStateAsUpdate(m))
rec(c, () => c.getArray('a').insert(2, [new Y.Map()])) // u[2] = 2:0, origin 1:1
Y.applyUpdate(m, Y.encodeStateAsUpdate(c))
rec(m, () => m.getArray('a').get(2).set('k', 1)) // u[3] = 1:2, parent 2:0
rec(m, () => m.getText('t').insert(0, 'z')) // u[4] = 1:3

const ydoc = new Y.Doc()
for (const i of [4, 2, 0, 1, 3]) Y.applyUpdate(ydoc, u[i])
console.log(JSON.stringify(ydoc.getArray('a').toJSON()), ydoc.getText('t').toString()) // ["x","y"] "", expected ["x","y",{"k":1}] "z"
console.log(ydoc.store.pendingStructs.missing) // Map(2) { 1 => 2, 2 => 0 }, expected null
```

Expected: `["x","y",{"k":1}]` and `"z"`, with nothing pending (this is what applying them in order `[0, 1, 2, 3, 4]` gives).
Actual: `["x","y"]` and `""`, with `missing` set to `{ 1 => 2, 2 => 0 }`. The doc stays like this until an update with client 2's struct `2:0` is applied again, or a peer resends it after a reconnect.

Cause (`src/utils/encoding.js`, 13.6.33): when `u[0]` arrives, the retry applies the pending `{2:0, 1:3}` again. Here `2:0` waits for `1:1`. The update has structs of client 1, so `integrateStructs` follows them to `1:3` (line 325) without recording what `2:0` waits for. `1:3` waits for `1:2`, so `missing` is set to `1 => 2` (line 308). When `u[1]` brings client 1 to clock 2, the retry check `clock < getState(store, client)` (line 400) is `2 < 2`, so nothing is retried. `u[3]` (`1:2`) then waits for `2:0` and goes into the pending update (line 413), so neither one can be applied.

In v14 (`@y/y` 14.0.0-rc.30) this case resolves, because structs are integrated past the gap. The cause is the same as your first case, though. When `integrateStructs` follows the structs of the client that the current struct waits for, it records nothing for that struct (v14 line 194). In your case the walk ends in the `stack.some(...)` branch (line 189), which records `2 => 0`. #827 rewrites this for v14 only, so v13 keeps the problem. The smallest fix we found is one line, in the `else` branch before the walk continues, in both versions: `updateMissingSv(missing, getState(store, missing))` in v13, and `updateMissingSv(missing, store.getClock(missing))` in v14. With that line, our random runs no longer stick in either version, your first case included. The cost is an occasional extra retry.

How often: we applied the updates of 3,000 seeded sessions with 2 or 3 clients and 6 updates each, in 20 random orders per session. Of those 60,000 runs, 13.6.33 stuck in 2 and 14.0.0-rc.30 in 108. Broader sessions (3 to 5 clients, 1,500 runs) stuck in neither version.
