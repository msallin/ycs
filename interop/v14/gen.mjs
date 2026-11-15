// Documents written by Yjs v14 (@y/y, pinned in package.json), for tests/Ycs.Tests/V14InteropTests.cs, which
// runs without Node. v14 is wire-compatible with v13 but has one type, Y.Node, and draws 53-bit client ids.
// Each case records v14's updates (v1, v2), its re-encoding, state vector, snapshots (v1, v2), relative
// positions with what v14 resolves them to, a diff and a merge, and how Yjs 13.6.32 reads it (lib.mjs), plus
// a concurrent change made in v14 that a Ycs peer applies before it answers (see roundtrip.mjs).
// Deterministic: every client id is set. Usage: node gen.mjs > ../../tests/Ycs.Tests/Fixtures/v14-interop.json
import * as Y from '@y/y'
import { b64, view13, stateVector, reencodes13 } from './lib.mjs'

const MAX = 2 ** 53 - 1

const doc = clientID => {
  const d = new Y.Doc()
  d.clientID = clientID
  return d
}

const sync = (a, b) => {
  Y.applyUpdate(b, Y.encodeStateAsUpdate(a, Y.encodeStateVector(b)))
  Y.applyUpdate(a, Y.encodeStateAsUpdate(b, Y.encodeStateVector(a)))
}

// Root and nested nodes, attributes, formatted text, list content and deletions, edited by two clients
// concurrently.
const nodes = (a, b) => {
  const text = a.get('text')
  text.insert(0, 'hello world')
  text.format(0, 5, { bold: true })
  text.insert(11, [{ image: 'x.png' }])
  const embedded = new Y.Node('mention')
  embedded.setAttr('user', 'u1')
  text.insert(12, [embedded])
  const map = a.get('map')
  map.setAttr('num', 42)
  map.setAttr('str', 'value')
  map.setAttr('bool', true)
  map.setAttr('null', null)
  map.setAttr('obj', { k: [1, 'two'] })
  map.setAttr('bin', new Uint8Array([1, 2, 255]))
  map.setAttr('gone', 'deleted below')
  const nested = new Y.Node('nested')
  nested.setAttr('id', 'n1')
  map.setAttr('node', nested)
  const array = a.get('array')
  array.insert(0, [1, 'two', { three: 3 }, true, null, 6.5])
  const inner = new Y.Node()
  inner.insert(0, ['i0', 'i1'])
  array.push([inner])
  const xml = a.get('xml')
  const p = new Y.Node('p')
  p.setAttr('class', 'intro')
  p.insert(0, 'plain ')
  p.insert(6, 'bold', { bold: true })
  const span = new Y.Node('span')
  span.setAttr('lang', 'en')
  span.insert(0, 'inside')
  p.insert(10, [span])
  const frag = new Y.Node()
  frag.insert(0, [7, 'x'])
  xml.insert(0, [p, frag, new Y.Node('hr')])
  sync(a, b)

  // Concurrently: b formats and types, a deletes and edits.
  b.get('text').insert(13, '!', { italic: true })
  b.get('text').format(6, 5, { italic: true })
  b.get('array').insert(2, ['from b'])
  b.get('map').setAttr('num', 43)
  a.get('text').delete(5, 1)
  a.get('array').delete(1, 1)
  a.get('map').deleteAttr('gone')
  a.get('xml').delete(2, 1)
  a.get('xml').get(0).insert(2, 'ain', { italic: true })
  sync(a, b)
}

// Clients above 2^32 and at 2^53 - 1: items, origins, deletions and map values of each.
const bigClients = (a, b) => {
  a.get('text').insert(0, 'abcdef')
  a.get('array').insert(0, [1, 2, 3])
  sync(a, b)
  b.get('text').insert(3, 'XYZ')
  b.get('text').delete(0, 1)
  a.get('text').delete(4, 2)
  a.get('text').format(0, 2, { bold: true })
  b.get('array').delete(1, 1)
  b.get('array').insert(0, ['b'])
  a.get('map').setAttr('k', 'a')
  b.get('map').setAttr('k', 'b')
  b.get('map').setAttr('other', MAX)
  sync(a, b)
}

const cases = [
  { name: 'nodes', clients: [11, 12, 13], build: nodes },
  { name: 'clients-above-2^32', clients: [2 ** 32 + 1, 2 ** 32 + 2 ** 31 + 5, 2 ** 40], build: bigClients },
  { name: 'clients-at-2^53-1', clients: [MAX, MAX - 1, MAX - 2], build: bigClients },
  { name: 'clients-mixed', clients: [MAX, 3, 2 ** 32], build: bigClients }
]

const roots = {
  nodes: [{ name: 'text', kind: 'text' }, { name: 'map', kind: 'map' }, { name: 'array', kind: 'array' }, { name: 'xml', kind: 'xml' }],
  default: [{ name: 'text', kind: 'text' }, { name: 'array', kind: 'array' }, { name: 'map', kind: 'map' }]
}

const out = []
for (const c of cases) {
  const [a, b] = c.clients.slice(0, 2).map(doc)
  // Record a's updates (one per transaction), for the merge.
  const updates = []
  a.on('update', u => updates.push(u))
  const prefixSv = Y.encodeStateVector(a)
  c.build(a, b)
  const r = roots[c.name] ?? roots.default
  const v1 = Y.encodeStateAsUpdate(a)
  const v2 = Y.encodeStateAsUpdateV2(a)
  const svAfterFirst = Y.encodeStateVector(Y.createDocFromUpdate(updates[0]))

  // Relative positions in every sequence root, at the start, inside and the end, both associations.
  const positions = []
  for (const { name, kind } of r) {
    if (kind === 'map') continue
    const type = a.get(name)
    for (const assoc of [0, -1]) {
      for (const index of [0, 1, Math.floor(type.length / 2), type.length]) {
        const rpos = Y.createRelativePositionFromTypeIndex(type, index, assoc)
        const abs = Y.createAbsolutePositionFromRelativePosition(rpos, a)
        positions.push({ root: name, index, assoc, encoded: b64(Y.encodeRelativePosition(rpos)), resolved: abs.index })
      }
    }
  }

  // A change a Ycs peer has not seen: a third v14 client makes it, after the state above.
  const c14 = doc(c.clients[2])
  Y.applyUpdate(c14, v1)
  const concurrent = []
  c14.on('update', u => concurrent.push(u))
  c14.get('text').insert(1, 'v14 ')
  c14.get('array').insert(0, ['v14'])
  c14.get('map').setAttr('v14', MAX)

  const sameIn13 = JSON.stringify(view13(v1, r)) === JSON.stringify(view13(v2, r, true))
  if (!sameIn13) throw new Error(`${c.name}: Yjs 13 reads the v1 and v2 updates differently`)
  out.push({
    name: c.name,
    clients: c.clients,
    roots: r,
    updateV1: b64(v1),
    updateV2: b64(v2),
    updatesV1: updates.map(b64),
    stateVector: b64(Y.encodeStateVector(a)),
    stateVectorDecoded: stateVector(Y.decodeStateVector(Y.encodeStateVector(a))),
    snapshotV1: b64(Y.encodeSnapshot(Y.snapshot(a))),
    snapshotV2: b64(Y.encodeSnapshotV2(Y.snapshot(a))),
    diff: { stateVector: b64(svAfterFirst), v1: b64(Y.diffUpdate(v1, svAfterFirst)), v2: b64(Y.diffUpdateV2(v2, svAfterFirst)), fromDoc: b64(Y.encodeStateAsUpdate(a, svAfterFirst)) },
    emptyStateVector: b64(prefixSv),
    merged: { v1: b64(Y.mergeUpdates(updates)), v2: b64(Y.mergeUpdatesV2(updates.map(u => Y.convertUpdateFormatV1ToV2(u)))) },
    positions,
    concurrent: { v1: b64(Y.mergeUpdates(concurrent)), stateVector: b64(Y.encodeStateVector(c14)) },
    v13: view13(v1, r),
    yjs13Reencodes: reencodes13(v1, v2)
  })
}

console.log(JSON.stringify({ yjs: '@y/y ' + (await import('@y/y/package.json', { with: { type: 'json' } })).default.version, cases: out }, null, 1))
