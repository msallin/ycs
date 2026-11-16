Comment for yjs/yjs#824 (verified 2026-11-16 against yjs 13.6.33; the fix line checked on a patched copy: 0 of 120 orders stick, 2 without).

---

Yjs 13 has the same problem as the first case here (pending structs whose dependencies have arrived, never retried), without a clock gap. Smallest case, yjs 13.6.33:

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
console.log(JSON.stringify(ydoc.getArray('a').toJSON()), ydoc.getText('t').toString())
console.log(ydoc.store.pendingStructs?.missing)
```

Expected (what order `[0, 1, 2, 3, 4]` gives): `["x","y",{"k":1}] z`, nothing pending.
Actual: `["x","y"]` and an empty text, `missing` = `{ 1 => 2, 2 => 0 }`, until `2:0` is applied again.

Cause (`src/utils/encoding.js`): when `integrateStructs` follows the structs of the client the current struct waits for (line 325), it records nothing for that struct; only the end of the walk sets `missing` (`1 => 2`, line 308). Once `1:1` arrives the retry check `clock < getState(...)` (line 400) is `2 < 2`, so nothing retries, and `1:2` (which needs the pending `2:0`) is merged into the pending update (line 413). v14 has the same omission (line 194 of 14.0.0-rc.30); this case happens to resolve there, yours does not. #827 changes v14 only.

One line before the walk continues fixes both cases for us: `updateMissingSv(missing, getState(store, missing))` in v13, `updateMissingSv(missing, store.getClock(missing))` in v14. With it, none of the 120 orders of the case above sticks (2 do without), nor any of our random runs.

Frequency: in 60,000 runs (2 to 3 clients, 6 updates, random orders) 13.6.33 stuck 2 times, 14.0.0-rc.30 108 times; never in creation order. Found by a convergence test of a y-websocket-compatible server on Ycs, the C# port.
