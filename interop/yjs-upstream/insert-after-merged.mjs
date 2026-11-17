// YXmlFragment.insertAfter with a reference node that GC merged away. Run: npm i yjs@13.6.33 && node insert-after-merged.mjs
import * as Y from 'yjs'
const a = new Y.Doc(); a.clientID = 1
const f = a.getXmlFragment('f')
const mk = n => new Y.XmlElement(n)
const e = [mk('first'), mk('held'), mk('right')]
f.insert(0, e)
const held = e[1]
const peer = new Y.Doc(); Y.applyUpdate(peer, Y.encodeStateAsUpdate(a))
a.on('update', u => Y.applyUpdate(peer, u))
f.delete(0, 2) // first and held are deleted; GC merges them
f.insertAfter(held, [mk('x')])
const names = d => d.getXmlFragment('f').toArray().map(n => n.nodeName).join()
console.log('local ', names(a))
console.log('remote', names(peer))
