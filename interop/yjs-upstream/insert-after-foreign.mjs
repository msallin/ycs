// YXmlFragment.insertAfter with a reference node of another fragment. Run: npm i yjs@13.6.33 && node insert-after-foreign.mjs
import * as Y from 'yjs'
const a = new Y.Doc(); a.clientID = 1
const f1 = a.getXmlFragment('f1'), f2 = a.getXmlFragment('f2')
const mk = n => new Y.XmlElement(n)
const kids = [mk('p1'), mk('p2')]
f2.insert(0, kids)
f1.insert(0, [mk('q1'), mk('q2')])
const peer = new Y.Doc(); Y.applyUpdate(peer, Y.encodeStateAsUpdate(a))
a.on('update', u => Y.applyUpdate(peer, u))
f1.insertAfter(kids[0], [mk('x')]) // kids[0] belongs to f2
const names = (d, k) => d.getXmlFragment(k).toArray().map(n => n.nodeName).join()
for (const [n, d] of [['local ', a], ['remote', peer]]) console.log(n, 'f1:', names(d, 'f1'), '| f2:', names(d, 'f2'))
for (const [n, d] of [['local ', a], ['remote', peer]]) {
  const x = d.getXmlFragment('f2').get(1)
  console.log(n, 'f1.length', d.getXmlFragment('f1').length, 'f2.length', d.getXmlFragment('f2').length, 'x.parent is', x.parent === d.getXmlFragment('f1') ? 'f1' : 'f2')
}
