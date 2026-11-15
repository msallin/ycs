// Differential traces of the shared types and their events, run in real Yjs and recorded for
// tests/Ycs.Tests/TraceReplayTests.cs. Three Yjs clients edit concurrently; every step that touches
// the observed client O (a local transaction of a few operations, or a remote update) is recorded
// with what Yjs shows afterwards. Ycs replays the same steps on one document and must show the same.
//
//   cd interop/traces && npm ci && node gen.mjs
//
// writes ../../tests/Ycs.Tests/Fixtures/trace-<set>.json.gz. Deterministic: seeded PRNG, fixed client
// ids, no clocks. State bytes are compared through the first 16 hex digits of their SHA-256.
import * as Y from 'yjs'
import { createHash } from 'node:crypto'
import { gzipSync } from 'node:zlib'
import { writeFileSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'

const here = dirname(fileURLToPath(import.meta.url))
const outDir = join(here, '..', '..', 'tests', 'Ycs.Tests', 'Fixtures')
const yjsVersion = JSON.parse(readFileSync(createRequire(import.meta.url).resolve('yjs/package.json'), 'utf8')).version

const mulberry32 = (seed) => {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6D2B79F5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

let rnd = mulberry32(1)
const ri = (n) => Math.floor(rnd() * n)
const pick = (a) => a[ri(a.length)]

const hash = (bytes) => createHash('sha256').update(bytes).digest('hex').slice(0, 16)
const b64 = (u) => Buffer.from(u).toString('base64')
const stateHash = (doc) => hash(Y.encodeStateAsUpdate(doc))

const typeName = (v) =>
  v instanceof Y.XmlText ? 'YXmlText'
    : v instanceof Y.Text ? 'YText'
      : v instanceof Y.XmlHook ? 'YXmlHook'
        : v instanceof Y.Map ? 'YMap'
          : v instanceof Y.Array ? 'YArray'
            : v instanceof Y.XmlElement ? 'YXmlElement'
              : v instanceof Y.XmlFragment ? 'YXmlFragment' : 'AbstractType'

// A value as the replay compares it: object keys sorted, a shared type as "<TypeName>", undefined as null.
const canon = (v) => {
  if (v === undefined || v === null) return null
  if (typeof v !== 'object') return v
  if (v instanceof Y.AbstractType) return `<${typeName(v)}>`
  if (v instanceof Uint8Array) return { $bytes: Buffer.from(v).toString('hex') }
  if (Array.isArray(v)) return v.map(canon)
  const o = {}
  for (const k of Object.keys(v).sort()) o[k] = canon(v[k])
  return o
}

const keysOf = (e) => [...e.keys.entries()].map(([k, v]) => [k, v.action, canon(v.oldValue)]).sort((a, b) => a[0] < b[0] ? -1 : 1)

// ------------------------------------------------------------------------------------------- text

const strs = ['a', 'bc', 'def', '\u{1F600}', 'x\u{1F601}y', '\n', 'hello world', ' ']
const attrValues = [true, true, null, null, 'red', 'blue', { href: 'u' }, [1, 2], 3]
const attrKeys = ['bold', 'italic', 'color']
const attrs = () => { const n = 1 + ri(2); const o = {}; for (let i = 0; i < n; i++) o[pick(attrKeys)] = pick(attrValues); return o }
const copy = (v) => v == null ? v : JSON.parse(JSON.stringify(v))

// Ops as data: [kind, ...args]; the replay maps each kind to the Ycs call of the same name.
const textOp = (len, { markers = false, types = true } = {}) => {
  if (markers) {
    const r = rnd()
    if (r < 0.6 || len < 5) return ['insert', ri(len + 1), pick(strs), null]
    const i = ri(len)
    return ['delete', i, 1 + ri(Math.min(6, len - i))]
  }
  const r = rnd()
  if (r < 0.33) return ['insert', ri(len + 1), pick(strs), rnd() < 0.4 ? attrs() : null]
  if (r < 0.53) {
    if (len === 0) return ['insert', 0, pick(strs), null]
    const i = ri(len)
    return ['delete', i, 1 + ri(Math.min(4, len - i))]
  }
  if (r < 0.82) {
    if (len === 0) return ['insert', 0, pick(strs), attrs()]
    const i = ri(len)
    return ['format', i, 1 + ri(Math.min(6, len - i)), attrs()]
  }
  if (r < 0.89) return ['embed', ri(len + 1), { img: ri(3) }, rnd() < 0.3 ? attrs() : null]
  if (r < 0.93 && types) return ['embedType', ri(len + 1), pick(['YMap', 'YText', 'YArray']), rnd() < 0.3 ? attrs() : null]
  if (r < 0.96) return rnd() < 0.7 ? ['setAttribute', pick(['lang', 'dir']), pick(['en', 'de', 1, null])] : ['removeAttribute', pick(['lang', 'dir'])]
  return ['insert', ri(len + 1), pick(strs), null]
}

const newType = (name) => name === 'YMap' ? new Y.Map([['k', 1]]) : name === 'YText' ? new Y.Text('t') : Y.Array.from([1])

const applyTextOp = (t, op) => {
  switch (op[0]) {
    case 'insert': return t.insert(op[1], op[2], copy(op[3]) ?? undefined)
    case 'delete': return t.delete(op[1], op[2])
    case 'format': return t.format(op[1], op[2], copy(op[3]))
    case 'embed': return t.insertEmbed(op[1], copy(op[2]), copy(op[3]) ?? undefined)
    case 'embedType': return t.insertEmbed(op[1], newType(op[2]), copy(op[3]) ?? undefined)
    case 'setAttribute': return t.setAttribute(op[1], op[2])
    case 'removeAttribute': return t.removeAttribute(op[1])
  }
}

const describeTextEvent = (e, n) => {
  // Every other trace reads changes before delta: reading changes computes the delta.
  const changes = n % 2 === 0 ? e.changes : null
  const d = { delta: canon(e.delta), keys: keysOf(e), keysChanged: [...e.keysChanged].sort() }
  const c = changes ?? e.changes
  d.changes = { delta: canon(c.delta), added: c.added.size, deleted: c.deleted.size, keys: [...c.keys.keys()].sort() }
  return d
}

/**
 * mode: plain (nobody observes), observed (an observer reads delta, changes and keys), markers
 * (long plain-text traces), placeholder (O receives formats for its root text before it asks for it).
 */
const textTrace = (n, mode) => {
  rnd = mulberry32(1000003 * (mode.length + 7) + n * 7919)
  const docs = [1, 2, 3].map(id => { const d = new Y.Doc(); d.clientID = id; return d })
  const [O, P, Q] = docs
  let tO = mode === 'placeholder' ? null : O.getText('t')
  let events = []
  const observe = () => tO.observe(e => events.push(describeTextEvent(e, n)))
  if (mode === 'observed') observe()
  const steps = []
  const expect = () => {
    if (tO === null) return { state: stateHash(O) }
    const x = mode === 'markers'
      ? { str: hash(Buffer.from(tO.toString(), 'utf8')), len: tO.length, state: stateHash(O) }
      : { str: tO.toString(), len: tO.length, delta: canon(tO.toDelta()), state: stateHash(O) }
    if (mode === 'observed' || mode === 'placeholder') x.events = events
    return x
  }
  const opts = { markers: mode === 'markers' }
  const placeholder = mode === 'placeholder'
  const nSteps = mode === 'markers' ? 400 : placeholder ? 60 + ri(60) : 15 + ri(40)
  const getAt = placeholder ? 3 + ri(10) : -1
  if (placeholder) {
    // A long formatted text, so that the markers are spread out (their reuse depends on length / 80).
    const t = P.getText('t')
    P.transact(() => {
      t.insert(0, 'abcdefghij'.repeat(10 + ri(10)))
      for (let k = 0; k < 4; k++) t.format(ri(90), 1 + ri(20), attrs())
    })
    const u = Y.encodeStateAsUpdate(P)
    Y.applyUpdate(O, u)
    steps.push({ kind: 'apply', update: b64(u), expect: expect() })
  }
  for (let i = 0; i < nSteps; i++) {
    if (i === getAt) {
      events = []
      tO = O.getText('t')
      observe()
      steps.push({ kind: 'get', expect: expect() })
      continue
    }
    const r = rnd()
    if (r < 0.35 && tO !== null) {
      const ops = []
      events = []
      // After a placeholder became the text, mostly inserts without attributes: they use the markers.
      const local = placeholder ? { markers: rnd() < 0.85 } : opts
      O.transact(() => { const k = 1 + ri(3); for (let j = 0; j < k; j++) { const op = textOp(tO.length, local); ops.push(op); applyTextOp(tO, op) } })
      steps.push({ kind: 'local', ops, expect: expect() })
    } else if (r < 0.7 || (tO === null && r < 0.85)) {
      const d = pick([P, Q])
      const t = d.getText('t')
      // Once O has its text, a remote format would turn its markers off: mostly plain changes then.
      const remote = placeholder && tO !== null ? { markers: rnd() < 0.85 } : opts
      d.transact(() => { const k = 1 + ri(2); for (let j = 0; j < k; j++) applyTextOp(t, textOp(t.length, remote)) })
    } else {
      const from = pick(docs)
      const to = pick(docs)
      if (from === to) continue
      const u = Y.encodeStateAsUpdate(from, Y.encodeStateVector(to))
      if (to === O) {
        events = []
        Y.applyUpdate(O, u)
        steps.push({ kind: 'apply', update: b64(u), expect: expect() })
      } else {
        Y.applyUpdate(to, u)
      }
    }
  }
  return { n, steps }
}

// ------------------------------------------------------------------------------------- array/map

const prims = [1, 2, 'a', 'b', null, true, { x: 1 }, [1, 2]]
const mkeys = ['k1', 'k2', 'k3']
const valSpec = (depth) => {
  const r = rnd()
  if (r < 0.7 || depth > 3) return { p: pick(prims) }
  if (r < 0.85) return rnd() < 0.5 ? { t: 'map' } : { t: 'map', init: { [pick(mkeys)]: pick(prims) } }
  return rnd() < 0.5 ? { t: 'array' } : { t: 'array', init: [pick(prims), pick(prims)] }
}
const mkVal = (v) => {
  if ('p' in v) return copy(v.p)
  if (v.t === 'map') return v.init ? new Y.Map(Object.entries(copy(v.init))) : new Y.Map()
  return v.init ? Y.Array.from(copy(v.init)) : new Y.Array()
}

// Every shared type of O's roots, depth first, with its path (keys and indices) from the document.
const walkTypes = (roots) => {
  const res = []
  const rec = (t, path) => {
    res.push({ t, path })
    if (t instanceof Y.Map) {
      t.forEach((v, k) => { if (v instanceof Y.AbstractType) rec(v, path.concat([k])) })
    } else if (t instanceof Y.Array || t instanceof Y.XmlFragment) {
      t.toArray().forEach((v, i) => { if (v instanceof Y.AbstractType) rec(v, path.concat([i])) })
    }
  }
  for (const [name, t] of roots) rec(t, [name])
  return res
}

const listMapOp = (d, push) => {
  const { t, path } = pick(walkTypes([['m', d.getMap('m')], ['a', d.getArray('a')]]))
  const depth = path.length
  if (t instanceof Y.Map) return rnd() < 0.7 ? ['mset', path, pick(mkeys), valSpec(depth)] : ['mdel', path, pick(mkeys)]
  const len = t.length
  const r = rnd()
  const vals = () => { const k = 1 + ri(3); const a = []; for (let i = 0; i < k; i++) a.push(valSpec(depth)); return a }
  if (r < 0.45 || len === 0) return ['ains', path, ri(len + 1), vals()]
  if (r < 0.75) { const i = ri(len); return ['adel', path, i, 1 + ri(Math.min(3, len - i))] }
  return push ? ['apush', path, vals()] : ['ains', path, len, vals()]
}

const resolve = (d, path) => {
  let t = path[0] === 'm' ? d.getMap('m') : path[0] === 'a' ? d.getArray('a') : d.getXmlFragment(path[0])
  for (let i = 1; i < path.length; i++) t = t.get(path[i])
  return t
}

const applyListMapOp = (d, op) => {
  const t = resolve(d, op[1])
  switch (op[0]) {
    case 'mset': return t.set(op[2], mkVal(op[3]))
    case 'mdel': return t.delete(op[2])
    case 'ains': return t.insert(op[2], op[3].map(mkVal))
    case 'adel': return t.delete(op[2], op[3])
    case 'apush': return t.push(op[2].map(mkVal))
  }
}

// What an observer sees: path, keys, the change set; for text, also its delta.
const describeEvent = (e) => {
  const d = { path: e.path, keys: keysOf(e), delta: canon(e.changes.delta), added: e.changes.added.size, deleted: e.changes.deleted.size }
  if (e instanceof Y.YXmlEvent) d.attributesChanged = [...e.attributesChanged].sort()
  return d
}

// Observers on every shared type of O, in the order of their paths; a deep observer on every third.
const observeAll = (roots, seen, log) => {
  const fresh = walkTypes(roots()).filter(x => !seen.has(x.t)).map(x => ({ t: x.t, key: JSON.stringify(x.path) }))
    .sort((a, b) => a.key < b.key ? -1 : a.key > b.key ? 1 : 0)
  for (const { t } of fresh) {
    const id = seen.size
    seen.set(t, id)
    t.observe(e => log.current.push(['obs', id, describeEvent(e)]))
    if (id % 3 === 0) t.observeDeep(evs => log.current.push(['deep', id, evs.map(describeEvent)]))
  }
}

const sizeOf = (t) => t instanceof Y.Map ? t.size : t.length

const listMapTrace = (n, push) => {
  rnd = mulberry32(2000003 + (push ? 500009 : 0) + n * 7919)
  const docs = [1, 2, 3].map(id => { const d = new Y.Doc(); d.clientID = id; return d })
  const [O, P, Q] = docs
  const roots = () => [['m', O.getMap('m')], ['a', O.getArray('a')]]
  const log = { current: [] }
  const seen = new Map()
  observeAll(roots, seen, log)
  const steps = []
  const expect = () => ({
    m: canon(O.getMap('m').toJSON()),
    a: canon(O.getArray('a').toJSON()),
    sizes: walkTypes(roots()).map(x => sizeOf(x.t)),
    state: stateHash(O),
    log: log.current
  })
  const nSteps = 15 + ri(40)
  for (let i = 0; i < nSteps; i++) {
    const r = rnd()
    if (r < 0.35) {
      const ops = []
      log.current = []
      O.transact(() => { const k = 1 + ri(4); for (let j = 0; j < k; j++) { const op = listMapOp(O, push); ops.push(op); applyListMapOp(O, op) } })
      steps.push({ kind: 'local', ops, expect: expect() })
      observeAll(roots, seen, log)
    } else if (r < 0.7) {
      const d = pick([P, Q])
      d.transact(() => { const k = 1 + ri(3); for (let j = 0; j < k; j++) applyListMapOp(d, listMapOp(d, push)) })
    } else {
      const from = pick(docs)
      const to = pick(docs)
      if (from === to) continue
      const u = Y.encodeStateAsUpdate(from, Y.encodeStateVector(to))
      if (to === O) {
        log.current = []
        Y.applyUpdate(O, u)
        steps.push({ kind: 'apply', update: b64(u), expect: expect() })
        observeAll(roots, seen, log)
      } else {
        Y.applyUpdate(to, u)
      }
    }
  }
  return { n, steps }
}

// -------------------------------------------------------------------------------------------- xml

const xmlNames = ['p', 'DIV', 'span']
const xmlAttrValues = ['v', 'a b', 1.5, 2, true, null, { x: 1 }, [1, null]]
const xmlFormatValues = [true, null, 'red', { href: 'u' }, [1, 2]]
const xmlNodeSpec = (depth) => {
  const r = rnd()
  if (r < 0.4) return { text: pick(strs) }
  if (r < 0.5) return { hook: 'h' }
  const spec = { element: pick(xmlNames) }
  if (rnd() < 0.4) spec.attrs = { [pick(['id', 'class'])]: pick(xmlAttrValues) }
  if (depth < 3 && rnd() < 0.3) spec.children = [{ text: pick(strs) }]
  return spec
}
const mkNode = (spec) => {
  if ('text' in spec) return new Y.XmlText(spec.text)
  if ('hook' in spec) return new Y.XmlHook(spec.hook)
  const el = new Y.XmlElement(spec.element)
  for (const [k, v] of Object.entries(spec.attrs ?? {})) el.setAttribute(k, copy(v))
  if (spec.children) el.insert(0, spec.children.map(mkNode))
  return el
}

// Every XML type of a fragment with its path; hooks are maps and are not descended into.
const walkXml = (root) => {
  const res = []
  const rec = (t, path) => {
    res.push({ t, path })
    if (t instanceof Y.XmlFragment) t.toArray().forEach((v, i) => { if (v instanceof Y.AbstractType) rec(v, path.concat([i])) })
  }
  rec(root, ['x'])
  return res
}

const xmlOp = (d) => {
  const { t, path } = pick(walkXml(d.getXmlFragment('x')))
  const depth = path.length
  if (t instanceof Y.XmlText) {
    const len = t.length
    const r = rnd()
    if (r < 0.4 || len === 0) return ['tinsert', path, ri(len + 1), pick(strs), rnd() < 0.3 ? { [pick(attrKeys)]: pick(xmlFormatValues) } : null]
    if (r < 0.65) { const i = ri(len); return ['tdelete', path, i, 1 + ri(Math.min(3, len - i))] }
    if (r < 0.9) { const i = ri(len); return ['tformat', path, i, 1 + ri(Math.min(4, len - i)), { [pick(attrKeys)]: pick(xmlFormatValues) }] }
    return ['tembed', path, ri(len + 1), { img: 1 }]
  }
  if (t instanceof Y.XmlHook) return ['hset', path, pick(mkeys), pick(prims)]
  const len = t.length
  const r = rnd()
  if (t instanceof Y.XmlElement && r < 0.25) return rnd() < 0.75 ? ['setAttr', path, pick(['id', 'class', 'title']), pick(xmlAttrValues)] : ['removeAttr', path, pick(['id', 'class', 'title'])]
  if (r < 0.65 || len === 0) return ['xinsert', path, ri(len + 1), [xmlNodeSpec(depth)].concat(rnd() < 0.3 ? [xmlNodeSpec(depth)] : [])]
  if (r < 0.85) { const i = ri(len); return ['xdelete', path, i, 1 + ri(Math.min(2, len - i))] }
  return ['xpush', path, [xmlNodeSpec(depth)]]
}

const applyXmlOp = (d, op) => {
  const t = resolve(d, op[1])
  switch (op[0]) {
    case 'tinsert': return t.insert(op[2], op[3], copy(op[4]) ?? undefined)
    case 'tdelete': return t.delete(op[2], op[3])
    case 'tformat': return t.format(op[2], op[3], copy(op[4]))
    case 'tembed': return t.insertEmbed(op[2], copy(op[3]))
    case 'hset': return t.set(op[2], copy(op[3]))
    case 'setAttr': return t.setAttribute(op[2], copy(op[3]))
    case 'removeAttr': return t.removeAttribute(op[2])
    case 'xinsert': return t.insert(op[2], op[3].map(mkNode))
    case 'xdelete': return t.delete(op[2], op[3])
    case 'xpush': return t.push(op[2].map(mkNode))
  }
}

const xmlTrace = (n) => {
  rnd = mulberry32(3000017 + n * 7919)
  const docs = [1, 2, 3].map(id => { const d = new Y.Doc(); d.clientID = id; return d })
  const [O, P, Q] = docs
  const x = O.getXmlFragment('x')
  const log = { current: [] }
  const seen = new Map()
  const observeXml = () => {
    for (const { t } of walkXml(x)) {
      if (seen.has(t)) continue
      const id = seen.size
      seen.set(t, id)
      t.observe(e => log.current.push(['obs', id, describeEvent(e)]))
      if (id % 3 === 0) t.observeDeep(evs => log.current.push(['deep', id, evs.map(describeEvent)]))
    }
  }
  observeXml()
  const steps = []
  const expect = () => ({
    str: x.toString(),
    json: x.toJSON(),
    nodes: walkXml(x).map(({ t }) =>
      t instanceof Y.XmlElement ? { name: t.nodeName, attrs: canon(t.getAttributes()), len: t.length }
        : t instanceof Y.XmlText ? { delta: canon(t.toDelta()), len: t.length }
          : t instanceof Y.XmlHook ? { hook: canon(t.toJSON()) } : { len: t.length }),
    state: stateHash(O),
    log: log.current
  })
  const nSteps = 15 + ri(40)
  for (let i = 0; i < nSteps; i++) {
    const r = rnd()
    if (r < 0.35) {
      const ops = []
      log.current = []
      O.transact(() => { const k = 1 + ri(3); for (let j = 0; j < k; j++) { const op = xmlOp(O); ops.push(op); applyXmlOp(O, op) } })
      steps.push({ kind: 'local', ops, expect: expect() })
      observeXml()
    } else if (r < 0.7) {
      const d = pick([P, Q])
      d.transact(() => { const k = 1 + ri(2); for (let j = 0; j < k; j++) applyXmlOp(d, xmlOp(d)) })
    } else {
      const from = pick(docs)
      const to = pick(docs)
      if (from === to) continue
      const u = Y.encodeStateAsUpdate(from, Y.encodeStateVector(to))
      if (to === O) {
        log.current = []
        Y.applyUpdate(O, u)
        steps.push({ kind: 'apply', update: b64(u), expect: expect() })
        observeXml()
      } else {
        Y.applyUpdate(to, u)
      }
    }
  }
  return { n, steps }
}

// -------------------------------------------------------------------------------------- snapshots

// Every client id is above 2^31, as half of all Yjs client ids are: computeYChange reports them.
const snapshotClients = [2147483648, 3000000001, 4294967295]
const computeYChange = (type, id) => ({ type, user: id.client })

const snapshotTrace = (n) => {
  rnd = mulberry32(4000037 + n * 7919)
  const docs = snapshotClients.map(id => { const d = new Y.Doc({ gc: false }); d.clientID = id; return d })
  const [O, P, Q] = docs
  const t = O.getText('t')
  const snaps = []
  const steps = []
  const expect = () => {
    const x = { str: t.toString(), delta: canon(t.toDelta()) }
    if (snaps.length > 0) {
      const now = Y.snapshot(O)
      const last = snaps[snaps.length - 1]
      const first = snaps[Math.max(0, snaps.length - 3)]
      x.atLast = canon(t.toDelta(last))
      x.sinceFirst = canon(t.toDelta(now, first))
      x.sinceFirstBy = canon(t.toDelta(now, first, computeYChange))
      x.firstToLastBy = canon(t.toDelta(last, first, computeYChange))
      x.prevOnly = canon(t.toDelta(undefined, first))
    }
    x.state = stateHash(O)
    return x
  }
  const nSteps = 15 + ri(40)
  for (let i = 0; i < nSteps; i++) {
    const r = rnd()
    if (r < 0.15) {
      const s = Y.snapshot(O)
      snaps.push(s)
      steps.push({ kind: 'snapshot', snapshot: b64(Y.encodeSnapshot(s)), expect: expect() })
    } else if (r < 0.45) {
      const ops = []
      O.transact(() => { const k = 1 + ri(3); for (let j = 0; j < k; j++) { const op = textOp(t.length, { types: false }); ops.push(op); applyTextOp(t, op) } })
      steps.push({ kind: 'local', ops, expect: expect() })
    } else if (r < 0.75) {
      const d = pick([P, Q])
      const dt = d.getText('t')
      d.transact(() => { const k = 1 + ri(2); for (let j = 0; j < k; j++) applyTextOp(dt, textOp(dt.length, { types: false })) })
    } else {
      const from = pick(docs)
      const to = pick(docs)
      if (from === to) continue
      const u = Y.encodeStateAsUpdate(from, Y.encodeStateVector(to))
      if (to === O) {
        Y.applyUpdate(O, u)
        steps.push({ kind: 'apply', update: b64(u), expect: expect() })
      } else {
        Y.applyUpdate(to, u)
      }
    }
  }
  return { n, clients: snapshotClients, steps }
}

// ------------------------------------------------------------------------------------------ write

const sets = {
  text: () => Array.from({ length: 200 }, (_, n) => textTrace(n, 'plain')),
  'text-observed': () => Array.from({ length: 300 }, (_, n) => textTrace(n, 'observed')),
  'text-markers': () => Array.from({ length: 25 }, (_, n) => textTrace(n, 'markers')),
  'text-placeholder': () => Array.from({ length: 150 }, (_, n) => textTrace(n, 'placeholder')),
  'list-map': () => Array.from({ length: 200 }, (_, n) => listMapTrace(n, false)),
  'list-map-push': () => Array.from({ length: 200 }, (_, n) => listMapTrace(n, true)),
  xml: () => Array.from({ length: 200 }, (_, n) => xmlTrace(n)),
  snapshots: () => Array.from({ length: 150 }, (_, n) => snapshotTrace(n))
}

for (const [name, make] of Object.entries(sets)) {
  const traces = make()
  const steps = traces.reduce((s, t) => s + t.steps.length, 0)
  const json = JSON.stringify({ yjs: yjsVersion, generator: 'interop/traces/gen.mjs', set: name, traces }) + '\n'
  writeFileSync(join(outDir, `trace-${name}.json.gz`), gzipSync(json, { level: 9 }))
  process.stderr.write(`${name}: ${traces.length} traces, ${steps} steps\n`)
}
