// Concurrent edits of nested maps and arrays on three replicas, run in real Yjs and recorded for
// tests/Ycs.Tests/EventTraceTests.cs: after every step that touches replica 1 (a local transaction
// of 1-4 operations, or an update from another replica), its JSON, the size of every type, its
// state bytes, and every observer call in order (an observer on every type, a deep observer on
// every third), with path, keys, delta and the sizes of added and deleted.
//
//   cd interop/corpus && npm ci && node events.mjs ../../tests/Ycs.Tests/Fixtures/event-traces.json.gz 300 1
//
// Deterministic: seeded PRNG, fixed client ids. Appends at the end of a list are insert(length),
// not push (Yjs' push appends after deleted trailing items, see the types walkthrough).
import * as Y from 'yjs'
import fs from 'fs'
import zlib from 'zlib'
const [,, outPath, nTraces, seed0] = process.argv
const noPush = true
const hex = u => Buffer.from(u).toString('hex')
const canon = v => {
  if (v === undefined) return null
  if (v === null || typeof v !== 'object') return v
  if (v instanceof Y.AbstractType) return '<type>'
  if (Array.isArray(v)) return v.map(canon)
  const o = {}; for (const k of Object.keys(v).sort()) o[k] = canon(v[k]); return o
}
let s = 0
const rnd = () => { s = (s * 1103515245 + 12345) & 0x7fffffff; return s / 0x7fffffff }
const ri = n => Math.floor(rnd() * n)
const pick = a => a[ri(a.length)]
const prims = [1, 2, 'a', 'b', null, true, { x: 1 }, [1, 2]]
const mkeys = ['k1', 'k2', 'k3']
const valSpec = depth => {
  const r = rnd()
  if (r < 0.7 || depth > 3) return { p: pick(prims) }
  if (r < 0.85) return rnd() < 0.5 ? { t: 'map' } : { t: 'map', init: { [pick(mkeys)]: pick(prims) } }
  return rnd() < 0.5 ? { t: 'array' } : { t: 'array', init: [pick(prims), pick(prims)] }
}
const mkVal = v => {
  if ('p' in v) return v.p === null ? null : JSON.parse(JSON.stringify(v.p))
  if (v.t === 'map') return v.init ? new Y.Map(Object.entries(v.init)) : new Y.Map()
  return v.init ? Y.Array.from(JSON.parse(JSON.stringify(v.init))) : new Y.Array()
}
const walk = d => {
  const res = []
  const rec = (t, path) => {
    res.push({ t, path })
    if (t instanceof Y.Map) t.forEach((v, k) => { if (v instanceof Y.AbstractType) rec(v, path.concat([k])) })
    else t.toArray().forEach((v, i) => { if (v instanceof Y.AbstractType) rec(v, path.concat([i])) })
  }
  rec(d.getMap('m'), ['m']); rec(d.getArray('a'), ['a'])
  return res
}
const genOp = d => {
  const { t, path } = pick(walk(d))
  const depth = path.length
  if (t instanceof Y.Map) {
    return rnd() < 0.7 ? ['mset', path, pick(mkeys), valSpec(depth)] : ['mdel', path, pick(mkeys)]
  }
  const len = t.length
  const r = rnd()
  const vals = () => { const n = 1 + ri(3); const a = []; for (let i = 0; i < n; i++) a.push(valSpec(depth)); return a }
  if (r < 0.45 || len === 0) return ['ains', path, ri(len + 1), vals()]
  if (r < 0.75) { const i = ri(len); return ['adel', path, i, 1 + ri(Math.min(3, len - i))] }
  return noPush ? ['ains', path, len, vals()] : ['apush', path, vals()]
}
const resolve = (d, path) => { let t = path[0] === 'm' ? d.getMap('m') : d.getArray('a'); for (let i = 1; i < path.length; i++) t = t.get(path[i]); return t }
const applyOp = (d, op) => {
  const t = resolve(d, op[1])
  switch (op[0]) {
    case 'mset': t.set(op[2], mkVal(op[3])); break
    case 'mdel': t.delete(op[2]); break
    case 'ains': t.insert(op[2], op[3].map(mkVal)); break
    case 'adel': t.delete(op[2], op[3]); break
    case 'apush': t.push(op[2].map(mkVal)); break
  }
}
const traces = []
for (let n = 0; n < +nTraces; n++) {
  s = (+seed0 || 1) * 7919 + n * 104729
  const docs = [1, 2, 3].map(id => { const d = new Y.Doc(); d.clientID = id; return d })
  const [O, P, Q] = docs
  let log = []
  const seen = new Map(); let nextId = 0
  const evDesc = e => ({ path: e.path, keys: [...e.keys.entries()].map(([k, v]) => [k, v.action, canon(v.oldValue)]), delta: canon(e.changes.delta), added: e.changes.added.size, deleted: e.changes.deleted.size })
  const register = () => {
    const fresh = walk(O).filter(x => !seen.has(x.t)).map(x => ({ t: x.t, key: JSON.stringify(x.path) })).sort((a, b) => a.key < b.key ? -1 : a.key > b.key ? 1 : 0)
    for (const { t } of fresh) { const id = nextId++; seen.set(t, id); t.observe(e => log.push(['obs', id, evDesc(e)])); if (id % 3 === 0) t.observeDeep(evs => log.push(['deep', id, evs.map(evDesc)])) }
  }
  register()
  const steps = []
  const snap = () => ({ m: canon(O.getMap('m').toJSON()), a: canon(O.getArray('a').toJSON()), sizes: walk(O).map(x => x.t instanceof Y.Map ? x.t.size : x.t.length), state: hex(Y.encodeStateAsUpdate(O)), log })
  const nSteps = 15 + ri(40)
  for (let i = 0; i < nSteps; i++) {
    const r = rnd()
    if (r < 0.35) {
      const ops = []; log = []
      O.transact(() => { const k = 1 + ri(4); for (let j = 0; j < k; j++) { const op = genOp(O); ops.push(op); applyOp(O, op) } })
      steps.push({ kind: 'local', ops, expect: snap() }); register()
    } else if (r < 0.7) {
      const d = pick([P, Q])
      d.transact(() => { const k = 1 + ri(3); for (let j = 0; j < k; j++) applyOp(d, genOp(d)) })
    } else {
      const from = pick(docs); const to = pick(docs)
      if (from === to) continue
      const u = Y.encodeStateAsUpdate(from, Y.encodeStateVector(to))
      if (to === O) { log = []; Y.applyUpdate(O, u); steps.push({ kind: 'apply', update: hex(u), expect: snap() }); register() } else Y.applyUpdate(to, u)
    }
  }
  traces.push({ n, steps })
}
fs.writeFileSync(outPath, zlib.gzipSync(JSON.stringify(traces), { level: 9 }))
