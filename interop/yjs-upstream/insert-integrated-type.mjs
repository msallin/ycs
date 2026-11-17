// Inserting a shared type that is already in the document. Run: npm i yjs@13.6.33 && node insert-integrated-type.mjs
import * as Y from 'yjs'

const doc = new Y.Doc(); doc.clientID = 1
const a = doc.getArray('a'), b = doc.getArray('b')
const m = new Y.Map()
a.insert(0, [m])
try { b.insert(0, [m]) } catch (e) { console.log('threw:', e.message) }

const fresh = d => { const p = new Y.Doc(); Y.applyUpdate(p, Y.encodeStateAsUpdate(d)); return p }
m.set('k', 1)
const p = fresh(doc)
console.log('doc   a', JSON.stringify(a.toJSON()), 'b', JSON.stringify(b.toJSON()))
console.log('fresh a', JSON.stringify(p.getArray('a').toJSON()), 'b', JSON.stringify(p.getArray('b').toJSON()))
