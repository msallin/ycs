// Out-of-order delivery, run in real Yjs and recorded for tests/Ycs.Tests/PendingTraceTests.cs:
// three clients edit a text, a map and nested arrays (garbage collection on); every transaction's
// update is delivered to a fresh receiver in a shuffled order, so structs and deletions wait for
// what they depend on. After every delivery the receiver's text, map JSON and
// encodeStateAsUpdate (v1 and v2, which include what is pending) are recorded.
//
//   cd interop/corpus && npm ci && node pending.mjs ../../tests/Ycs.Tests/Fixtures/pending-traces.json.gz 200 1
//
// Deterministic: seeded PRNG, fixed client ids.
import * as Y from 'yjs'
import fs from 'fs'
import zlib from 'zlib'
const [,, outPath, nTraces, seed0] = process.argv
const hex = u => Buffer.from(u).toString('hex')
let s = 0
const rnd = () => { s = (s * 1103515245 + 12345) & 0x7fffffff; return s / 0x7fffffff }
const ri = n => Math.floor(rnd() * n)
const pick = a => a[ri(a.length)]
const edit = d => {
  const t = d.getText('t'); const m = d.getMap('m')
  const r = rnd()
  if (r < 0.4 || t.length === 0) t.insert(ri(t.length + 1), pick(['a', 'bc', 'def', 'x']))
  else if (r < 0.6) { const i = ri(t.length); t.delete(i, 1 + ri(Math.min(3, t.length - i))) }
  else if (r < 0.75) m.set(pick(['k1', 'k2', 'k3']), pick([1, 'v', null, [1, 2]]))
  else if (r < 0.85) m.delete(pick(['k1', 'k2', 'k3']))
  else if (r < 0.93) { const a = new Y.Array(); a.insert(0, [1, 2, 3]); m.set(pick(['n1', 'n2']), a) }
  else { const a = m.get('n1'); if (a instanceof Y.Array && a.length > 0) a.delete(0, 1); else m.delete('n2') }
}
const traces = []
for (let n = 0; n < +nTraces; n++) {
  s = (+seed0 || 1) * 7919 + n * 104729
  const docs = [1, 2, 3].map(id => { const d = new Y.Doc(); d.clientID = id; return d })
  const updates = []
  docs.forEach(d => d.on('update', u => updates.push(u)))
  const nSteps = 5 + ri(20)
  for (let i = 0; i < nSteps; i++) {
    const d = pick(docs)
    if (rnd() < 0.3) { const from = pick(docs); if (from !== d) Y.applyUpdate(d, Y.encodeStateAsUpdate(from, Y.encodeStateVector(d)), 'sync') }
    else d.transact(() => { const k = 1 + ri(3); for (let j = 0; j < k; j++) edit(d) })
  }
  const order = updates.map((u, i) => [rnd(), u]).sort((a, b) => a[0] - b[0]).map(x => x[1])
  const receiver = new Y.Doc(); receiver.clientID = 99
  const steps = order.map(u => {
    Y.applyUpdate(receiver, u)
    return { update: hex(u), text: receiver.getText('t').toString(), map: JSON.stringify(receiver.getMap('m').toJSON()), v1: hex(Y.encodeStateAsUpdate(receiver)), v2: hex(Y.encodeStateAsUpdateV2(receiver)) }
  })
  traces.push({ n, steps })
}
fs.writeFileSync(outPath, zlib.gzipSync(JSON.stringify(traces), { level: 9 }))
