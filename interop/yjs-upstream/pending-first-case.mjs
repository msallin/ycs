// Pending structs are not retried (yjs/yjs#824). Run: npm i yjs@13.6.33 && node pending-first-case.mjs
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
console.log(JSON.stringify(ydoc.getArray('a').toJSON()), JSON.stringify(ydoc.getText('t').toString()))
console.log(ydoc.store.pendingStructs?.missing)
