// The repros of tests/Ycs.Tests/RunExtensionYjsTests.cs, run in Yjs 13.6.32: what each test expects.
//
//   node blockers.mjs
import * as Y from 'yjs'
const hex = b => Buffer.from(b).toString('hex')
const sync = (from, to) => Y.applyUpdate(to, Y.encodeStateAsUpdate(from, Y.encodeStateVector(to)))
{ // B1 repro 1
  const doc = new Y.Doc(); doc.clientID = 3; const t = doc.getText('t'); t.observe(() => {})
  const peer = new Y.Doc(); peer.clientID = 2; const pt = peer.getText('t')
  t.insert(0, 'a'); sync(doc, peer); pt.delete(0, 1)
  doc.transact(() => { t.insert(1, 'b'); t.insert(2, 'c'); sync(peer, doc); t.insert(2, 'd') })
  console.log('B1.1', t.toString(), hex(Y.encodeStateAsUpdate(doc)))
}
{ // B1 repro 1 unobserved
  const doc = new Y.Doc(); doc.clientID = 3; const t = doc.getText('t')
  const peer = new Y.Doc(); peer.clientID = 2; const pt = peer.getText('t')
  t.insert(0, 'a'); sync(doc, peer); pt.delete(0, 1)
  doc.transact(() => { t.insert(1, 'b'); t.insert(2, 'c'); sync(peer, doc); t.insert(2, 'd') })
  console.log('B1.1u', t.toString(), hex(Y.encodeStateAsUpdate(doc)))
}
{ // B1 repro 2
  const doc = new Y.Doc(); doc.clientID = 1; const t = doc.getText('t'); t.observe(() => {}); const m = doc.getMap('m')
  doc.transact(() => { t.insert(0, 'a'); t.insert(1, 'b'); t.delete(1, 1); t.insert(1, 'c'); t.insert(2, 'd'); t.delete(2, 1) })
  const peer = new Y.Doc(); peer.clientID = 2; sync(doc, peer)
  const pt = peer.getText('t'); peer.getMap('m').set('k', 1); pt.insert(2, 'e'); pt.delete(0, 1)
  m.observe(() => t.insert(2, 'f'))
  sync(peer, doc)
  console.log('B1.2', t.toString(), hex(Y.encodeStateAsUpdate(doc)))
}
{ // B1 array example (unobserved, client 3)
  const doc = new Y.Doc(); doc.clientID = 3; const a = doc.getArray('t')
  const peer = new Y.Doc(); peer.clientID = 2; const pa = peer.getArray('t')
  a.insert(0, [0]); a.delete(0, 1); a.insert(0, [1]); sync(doc, peer); pa.delete(0, 1)
  doc.transact(() => { a.insert(1, [2]); a.insert(2, [3]); sync(peer, doc); a.insert(1, [4]) })
  console.log('B1.3', JSON.stringify(a.toArray()), hex(Y.encodeStateAsUpdate(doc)))
}
const gcCase = (name, filter, body) => {
  const log = []
  const doc = new Y.Doc({ gcFilter: item => { log.push(`${item.id.clock}+${item.length}`); return filter(item) } }); doc.clientID = 1
  const ups = []
  doc.on('update', u => ups.push(hex(u)))
  body(doc)
  console.log(name, log.join(','), ups.join(','), hex(Y.encodeStateAsUpdate(doc)))
}
gcCase('B2.1', i => i.id.clock !== 1, doc => { const t = doc.getText('t'); doc.transact(() => { t.insert(0, 'a'); t.insert(1, 'b'); t.delete(0, 2) }) })
gcCase('B2.2', i => i.id.clock !== 1, doc => { const t = doc.getText('t'); t.insert(0, 'a'); doc.transact(() => { t.insert(1, 'b'); t.delete(0, 2) }) })
gcCase('B2.3', i => i.id.clock % 2 === 0, doc => { const a = doc.getArray('a'); doc.transact(() => { a.push([1]); a.push([2]); a.push([3]); a.delete(0, 3) }) })
{ // B3 array
  const doc = new Y.Doc({ gc: false }); doc.clientID = 1; const a = doc.getArray('a'); a.push([1, 2])
  let r; doc.transact(() => { a.push([3]); const sm = Y.snapshot(doc); a.push([4]); a.push([5]); r = Y.typeListToArraySnapshot(a, sm) })
  console.log('B3.1', JSON.stringify(r))
}
{ // B3 text
  const log = []
  const doc = new Y.Doc({ gc: false }); doc.clientID = 1; const t = doc.getText('t'); t.insert(0, 'ab')
  const s1 = Y.snapshot(doc)
  const cy = (type, id) => { log.push(`${type} ${id.client}:${id.clock}`); return { type, user: 'u' + id.client } }
  let d, d2
  doc.transact(() => { t.insert(2, 'c'); t.insert(3, 'd'); t.insert(4, 'e'); t.insert(5, 'f'); t.delete(1, 2); const s2 = Y.snapshot(doc); d = t.toDelta(s2, s1, cy); d2 = t.toDelta(s2, s1) })
  console.log('B3.2', log.join(','), JSON.stringify(d), JSON.stringify(d2))
}
{ // B4 inner
  const log = []
  const doc = new Y.Doc(); doc.clientID = 1; const x = doc.getText('x'), y = doc.getText('y')
  let fired = false
  x.observe(e => { if (fired) return; fired = true; y.insert(y.length, 'c'); y.observe(e2 => log.push(JSON.stringify(e2.delta))) })
  doc.transact(() => { x.insert(0, 'x'); y.insert(0, 'b') })
  console.log('B4.1', log.join(' | '), hex(Y.encodeStateAsUpdate(doc)))
}
{ // B4 inner via afterTransaction ChangedParentTypes
  const log = []
  const doc = new Y.Doc(); doc.clientID = 1; const x = doc.getText('x'), y = doc.getText('y')
  let fired = false
  x.observe(e => { if (fired) return; fired = true; y.insert(y.length, 'c'); doc.on('afterTransaction', tr => { const es = tr.changedParentTypes.get(y); log.push(es ? es.map(e => JSON.stringify(e.delta)).join(';') : '-') }) })
  doc.transact(() => { x.insert(0, 'x'); y.insert(0, 'b') })
  console.log('B4.1b', log.join(' | '))
}
{ // B4 outer
  const doc = new Y.Doc(); doc.clientID = 1; const x = doc.getText('x'), y = doc.getArray('y'); let fired = false; const log = []
  x.observe(() => { if (fired) return; fired = true; y.push([2]); y.observe(e => log.push(JSON.stringify(e.changes.delta) + ' added ' + [...e.changes.added].map(i => i.id.clock + '+' + i.length).sort().join(','))) })
  doc.transact(() => { x.insert(0, 'x'); y.push([1]) })
  console.log('B4.2', log.join(' | '))
}
