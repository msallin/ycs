# Comment for yjs/yjs#824 (pending structs are not retried)

Target: a comment on https://github.com/yjs/yjs/issues/824 (open, no comments yet). Verified 2026-11-17 against yjs 13.6.33 (latest `latest` tag).

---

Yjs 13 has the same problem as the first case here (pending structs whose dependencies have arrived are never retried), without a clock gap. Smallest case, yjs 13.6.33 (`pending-first-case.mjs`):

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

Expected (order `[0, 1, 2, 3, 4]` gives this): `["x","y",{"k":1}] z`, nothing pending.
Actual: `["x","y"]` and an empty text, `missing` = `{ 1 => 2, 2 => 0 }`, until `2:0` is applied again.

Cause (`src/utils/encoding.js`): when `integrateStructs` follows the structs of the client the current struct waits for (line 325), it records nothing for that struct. Only the end of the walk sets `missing` (`1 => 2`, line 308). Once `1:1` arrives, the retry check `clock < getState(...)` (line 400) is `2 < 2`, so nothing retries, and `1:2` (which needs the pending `2:0`) is merged into the pending update (line 413).

One line before the walk continues fixes it, at line 325: `updateMissingSv(missing, getState(store, missing))`. With it the case above resolves (checked on a patched copy of 13.6.33).

A second, shorter case that the same line fixes (`pending-second-case.mjs`). Three clients, A receives. Client ids: A 55967 (gc off), B 849942, C 785458.

1. C inserts "a" at 0 (C:0), B inserts "a" at 0 (B:0), C inserts "a" at 0 (C:1).
2. B receives C:0, then inserts "a" at 0 (B:1, right origin C:0).
3. C pushes a value to an array (C:2).
4. A receives, in this order: C:2 (as v2), B:1 (v1), B:0 (v2), C:0 (v2).

A has C:0, B:0 and B:1 and they depend only on each other. Expected `t = "aaa"` with only C:2 pending (it needs C:1). Actual `t = "aa"`: B:1 stays pending, because the `missing` entry for C only says "C:1" and C:0 arrived later. With the line above: `"aaa"`, only `C:2` pending.

14.0.0-16 (`yjs`) and 14.0.0-rc.30 (`@y/y`) give the right result for both cases.

Found by differential tests of Ycs, a C# port of Yjs.
