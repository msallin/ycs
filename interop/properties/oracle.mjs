// The Yjs oracle of tests/Ycs.Properties: executes operation scripts against real Yjs and prints,
// for each step, what it observed. tests/Ycs.Properties/Adapter.cs is the same interpreter
// against Ycs; the two must print the same text, character for character.
//
//   node oracle.mjs < scripts.jsonl > observations.jsonl
//
// One script per input line (the language is described in docs/testing.md), one line per script
// on output: a canonical JSON array with one observation per step and a last one for the end.
// Canonical JSON: no whitespace, object keys sorted by UTF-16 code units, every character
// outside 0x20-0x7e (and " and \) escaped as \u followed by four lowercase hex digits, and only
// integers as numbers (other numbers are rendered as tagged values, see view()).
import * as Y from 'yjs'
import * as encoding from 'lib0/encoding'
import * as decoding from 'lib0/decoding'
import { createHash } from 'node:crypto'
import { createInterface } from 'node:readline'

const NET = 'net'
const INF_TIMEOUT = 2147483647

// ---------------------------------------------------------------- canonical JSON

export function canon (v) {
  if (v === null || v === undefined) return 'null'
  if (v === true) return 'true'
  if (v === false) return 'false'
  if (typeof v === 'number') {
    if (!Number.isSafeInteger(v) || Object.is(v, -0)) throw new Error(`not a canonical number: ${v}`)
    return String(v)
  }
  if (typeof v === 'string') return str(v)
  if (Array.isArray(v)) return '[' + v.map(canon).join(',') + ']'
  const keys = Object.keys(v).filter(k => v[k] !== undefined).sort()
  return '{' + keys.map(k => str(k) + ':' + canon(v[k])).join(',') + '}'
}

function str (s) {
  let out = '"'
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i)
    if (c === 0x22) out += '\\"'
    else if (c === 0x5c) out += '\\\\'
    else if (c >= 0x20 && c < 0x7f) out += s[i]
    else out += '\\u' + c.toString(16).padStart(4, '0')
  }
  return out + '"'
}

const b64 = u8 => Buffer.from(u8).toString('base64')
const hash = u8 => createHash('sha256').update(u8).digest('hex').slice(0, 16)

// ---------------------------------------------------------------- values

function floatFromTag (tag) {
  switch (tag) {
    case 'NaN': return NaN
    case 'Infinity': return Infinity
    case '-Infinity': return -Infinity
    case '-0': return -0
    default: {
      const dv = new DataView(new ArrayBuffer(8))
      dv.setBigUint64(0, BigInt('0x' + tag))
      return dv.getFloat64(0)
    }
  }
}

function numberView (n) {
  if (Number.isNaN(n)) return { $f: 'NaN' }
  if (n === Infinity) return { $f: 'Infinity' }
  if (n === -Infinity) return { $f: '-Infinity' }
  if (Object.is(n, -0)) return { $f: '-0' }
  if (Number.isSafeInteger(n)) return n
  const dv = new DataView(new ArrayBuffer(8))
  dv.setFloat64(0, n)
  return { $f: dv.getBigUint64(0).toString(16).padStart(16, '0') }
}

/** A script value (see docs/testing.md) as the Yjs value it stands for; shared types are new (prelim). */
function val (s) {
  if (s === null || typeof s === 'boolean' || typeof s === 'string' || typeof s === 'number') return s
  if (Array.isArray(s)) return s.map(val)
  if ('$i' in s) return Number(s.$i)
  if ('$f' in s) return floatFromTag(s.$f)
  if ('$n' in s) return BigInt(s.$n)
  if ('$b' in s) return new Uint8Array(Buffer.from(s.$b, 'base64'))
  if ('$t' in s) return newType(s)
  if ('$doc' in s) return new Y.Doc({ guid: s.$doc.g, gc: s.$doc.gc !== false, autoLoad: !!s.$doc.autoLoad })
  const o = {}
  for (const k of Object.keys(s)) o[k] = val(s[k])
  return o
}

/** A script delta (docs/testing.md) as a Yjs delta. */
function deltaOf (delta) {
  return delta.map(d => {
    const o = {}
    if ('insert' in d) o.insert = typeof d.insert === 'string' ? d.insert : val(d.insert)
    if ('retain' in d) o.retain = d.retain
    if ('delete' in d) o.delete = d.delete
    if ('attrs' in d) o.attributes = attrsOf(d.attrs)
    return o
  })
}

/** A new text with its prelim content: "s" inserted, then the delta "dl" applied (both wait for the integration). */
function prelimText (t, s) {
  if (s.dl) t.applyDelta(deltaOf(s.dl), { sanitize: s.dls !== false })
  return t
}

function newType (s) {
  switch (s.$t) {
    case 'text': return prelimText(new Y.Text(s.s), s)
    case 'xmlt': return prelimText(new Y.XmlText(s.s), s)
    case 'array': return Y.Array.from((s.v ?? []).map(val))
    case 'map': return new Y.Map((s.v ?? []).map(([k, v]) => [k, val(v)]))
    case 'xmlf': {
      const f = new Y.XmlFragment()
      if (s.v) f.insert(0, s.v.map(val))
      return f
    }
    case 'xmle': {
      const e = new Y.XmlElement(s.n)
      for (const [k, v] of s.a ?? []) e.setAttribute(k, val(v))
      if (s.v) e.insert(0, s.v.map(val))
      return e
    }
    default: throw new Error(`unknown type ${s.$t}`)
  }
}

export function kindOf (t) {
  if (t instanceof Y.XmlText) return 'xmlt'
  if (t instanceof Y.Text) return 'text'
  if (t instanceof Y.XmlElement) return 'xmle'
  if (t instanceof Y.XmlFragment) return 'xmlf'
  if (t instanceof Y.XmlHook) return 'hook'
  if (t instanceof Y.Map) return 'map'
  if (t instanceof Y.Array) return 'array'
  return null
}

function sortedObject (entries) {
  const o = {}
  for (const [k, v] of entries) o[k] = v
  return o
}

/** What a value looks like, as canonical JSON: shared types with their kind and content. */
export function view (v) {
  if (v === null || v === undefined) return null
  if (typeof v === 'boolean' || typeof v === 'string') return v
  if (typeof v === 'number') return numberView(v)
  if (typeof v === 'bigint') return { $n: v.toString() }
  if (v instanceof Uint8Array) return { $b: b64(v) }
  if (v instanceof Y.Doc) return { $t: 'doc', g: v.guid }
  if (v instanceof Y.AbstractType) return typeView(v)
  if (Array.isArray(v)) return v.map(view)
  return sortedObject(Object.keys(v).map(k => [k, view(v[k])]))
}

function attrsView (attrs) {
  const keys = Object.keys(attrs ?? {})
  return keys.length === 0 ? undefined : sortedObject(keys.map(k => [k, view(attrs[k])]))
}

function deltaView (delta) {
  return delta.map(d => {
    const o = {}
    if (d.insert !== undefined) o.insert = view(d.insert)
    if (d.retain !== undefined) o.retain = d.retain
    if (d.delete !== undefined) o.delete = d.delete
    const a = attrsView(d.attributes)
    if (a !== undefined) o.attributes = a
    return o
  })
}

function typeView (t) {
  switch (kindOf(t)) {
    case 'text': return { $t: 'text', d: deltaView(t.toDelta()), a: attrsView(t.getAttributes()) }
    case 'xmlt': return { $t: 'xmlt', d: deltaView(t.toDelta()), a: attrsView(t.getAttributes()) }
    case 'array': return { $t: 'array', v: t.toArray().map(view) }
    case 'map': return { $t: 'map', v: sortedObject([...t.entries()].map(([k, x]) => [k, view(x)])) }
    case 'xmlf': return { $t: 'xmlf', v: t.toArray().map(view) }
    case 'xmle': return { $t: 'xmle', n: t.nodeName, a: attrsView(t.getAttributes()), v: t.toArray().map(view) }
    case 'hook': return { $t: 'hook', n: t.hookName, v: sortedObject([...t.entries()].map(([k, x]) => [k, view(x)])) }
    default: return { $t: '?' }
  }
}

/** toJSON as a view; Yjs' XmlFragment.toString throws on a child that is not an XML type. */
function json (t) {
  try {
    return view(t.toJSON())
  } catch (e) {
    return { $err: 'toJSON' }
  }
}

function lengthOf (t) {
  const k = kindOf(t)
  return k === 'map' || k === 'hook' ? t.size : t.length
}

// ---------------------------------------------------------------- one client

export class Client {
  constructor (run, index, spec) {
    this.run = run
    this.index = index
    const doc = new Y.Doc({ gc: spec.gc !== false })
    doc.clientID = spec.id
    this.doc = doc
    this.roots = {}
    this.log = [] // {v1, v2} per local transaction that changed something
    this.ums = {}
    this.snaps = {}
    let pendingV1 = null
    for (const r of run.script.roots) {
      const t = rootOf(doc, r)
      this.roots[r.name] = t
      t.observe((e, tr) => this.event('obs:' + r.name, [e], tr))
      t.observeDeep((es, tr) => this.event('deep:' + r.name, es, tr))
    }
    doc.on('afterTransaction', tr => this.push({
      w: 'atx',
      origin: run.originLabel(tr.origin),
      local: tr.local,
      changed: [...tr.changed.entries()].map(([t, keys]) => [this.pathOf(t), [...keys].map(k => k ?? '').sort()]).sort((a, b) => canon(a) < canon(b) ? -1 : 1),
    }))
    doc.on('update', (u, origin, _d, tr) => {
      this.push({ w: 'u1', u: b64(u), origin: run.originLabel(origin), local: tr.local })
      pendingV1 = tr.local ? u : null
    })
    doc.on('updateV2', (u, origin, _d, tr) => {
      this.push({ w: 'u2', u: b64(u), origin: run.originLabel(origin), local: tr.local })
      if (tr.local) this.log.push({ v1: pendingV1, v2: u })
      pendingV1 = null
    })
    doc.on('subdocs', ({ added, removed, loaded }) => this.push({
      w: 'subdocs',
      added: [...added].map(d => d.guid).sort(),
      removed: [...removed].map(d => d.guid).sort(),
      loaded: [...loaded].map(d => d.guid).sort(),
    }))
  }

  push (record) { this.run.events.push({ c: this.index, ...record }) }

  event (who, events, tr) {
    this.push({ w: who, es: events.map(e => this.eventView(e)), origin: this.run.originLabel(tr.origin), local: tr.local })
  }

  eventView (e) {
    const k = kindOf(e.target)
    const o = { t: this.pathOf(e.target), k, p: e.path }
    const keys = [...e.changes.keys.entries()].sort((a, b) => a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0)
    if (keys.length > 0) o.keys = sortedObject(keys.map(([key, ch]) => [key, ch.action === 'add' ? { action: 'add' } : { action: ch.action, old: view(ch.oldValue) }]))
    if (k === 'text' || k === 'xmlt') {
      o.d = deltaView(e.delta)
      o.clc = e.childListChanged
    } else if (k !== 'map' && k !== 'hook') {
      o.d = deltaView(e.changes.delta)
    }
    if (k === 'xmle' || k === 'xmlf') {
      o.ac = [...e.attributesChanged].sort()
      o.clc = e.childListChanged
    }
    return o
  }

  /** The path of a type from its root, by visible content only ("~" where it is not visible). */
  pathOf (t) {
    const steps = []
    for (;;) {
      for (const [name, root] of Object.entries(this.roots)) {
        if (root === t) return [name, ...steps.reverse()]
      }
      const parent = t.parent
      if (parent === null) return ['~']
      const step = childStep(parent, t)
      if (step === null) return ['~']
      steps.push(step)
      t = parent
    }
  }

  resolve (path) {
    if (!Array.isArray(path) || path.length === 0) return null
    let t = this.roots[path[0]]
    for (let i = 1; t !== undefined && t !== null && i < path.length; i++) {
      const p = path[i]
      const k = kindOf(t)
      if (typeof p === 'number') {
        if (k !== 'array' && k !== 'xmlf' && k !== 'xmle') return null
        if (p < 0 || p >= t.length) return null
        t = t.get(p)
      } else {
        if (k !== 'map') return null
        t = t.get(p)
      }
      if (!(t instanceof Y.AbstractType)) return null
    }
    return t ?? null
  }

  state (full) {
    const doc = this.doc
    const v1 = Y.encodeStateAsUpdate(doc)
    const v2 = Y.encodeStateAsUpdateV2(doc)
    const sv = Y.encodeStateVector(doc)
    const roots = {}
    for (const [name, t] of Object.entries(this.roots)) roots[name] = { v: view(t), j: json(t), n: lengthOf(t) }
    return {
      sv: sortedObject([...Y.decodeStateVector(sv).entries()].map(([c, k]) => [String(c), k])),
      svb: b64(sv),
      ds: dsView(Y.decodeUpdate(v1).ds),
      v1: full ? b64(v1) : hash(v1),
      v2: full ? b64(v2) : hash(v2),
      pend: doc.store.pendingStructs !== null || doc.store.pendingDs !== null,
      roots,
    }
  }
}

function rootOf (doc, r) {
  switch (r.kind) {
    case 'text': return doc.getText(r.name)
    case 'array': return doc.getArray(r.name)
    case 'map': return doc.getMap(r.name)
    case 'xml': return doc.getXmlFragment(r.name)
    default: throw new Error(`unknown root kind ${r.kind}`)
  }
}

function childStep (parent, t) {
  const k = kindOf(parent)
  if (k === 'map' || k === 'hook') {
    for (const [key, v] of parent.entries()) if (v === t) return key
    return null
  }
  if (k === 'text' || k === 'xmlt') {
    let i = 0
    for (const d of parent.toDelta()) {
      if (d.insert === t) return i
      i += typeof d.insert === 'string' ? d.insert.length : 1
    }
    return null
  }
  const i = parent.toArray().indexOf(t)
  return i < 0 ? null : i
}

function dsView (ds) {
  const clients = [...ds.clients.entries()].sort((a, b) => a[0] - b[0])
  const out = {}
  for (const [client, items] of clients) {
    const ranges = items.map(d => [d.clock, d.len]).sort((a, b) => a[0] - b[0])
    const merged = []
    for (const [c, l] of ranges) {
      const last = merged[merged.length - 1]
      if (last && last[0] + last[1] >= c) last[1] = Math.max(last[1], c + l - last[0])
      else merged.push([c, l])
    }
    if (merged.length > 0) out[String(client)] = merged
  }
  return out
}

const clamp = (i, n) => Math.max(0, Math.min(i | 0, n))
const attrsOf = a => (a === undefined || a === null) ? undefined : val(a)

// ---------------------------------------------------------------- a run of one script

export class Run {
  constructor (script) {
    this.script = script
    this.events = []
    this.umIds = new Map()
    this.rpos = {}
    this.compact = !!script.compact
    this.clients = script.clients.map((spec, i) => new Client(this, i, spec))
  }

  originLabel (o) {
    if (o === null || o === undefined) return null
    if (typeof o === 'string') return o
    if (this.umIds.has(o)) return 'um:' + this.umIds.get(o)
    return '?'
  }

  execute () {
    const out = []
    this.script.steps.forEach((step, i) => {
      this.events = []
      const obs = { i, op: step.op }
      let touched
      try {
        const r = this.step(step)
        if (r !== undefined) obs.r = r
      } catch (e) {
        obs.err = true
      }
      touched = 'c' in step ? [step.c] : 'to' in step ? [step.to] : []
      obs.ev = this.events
      obs.st = sortedObject(touched.filter(c => this.clients[c] !== undefined).map(c => [String(c), this.clients[c].state(!this.compact)]))
      out.push(obs)
    })
    this.events = []
    out.push({ i: 'end', st: sortedObject(this.clients.map((c, i) => [String(i), c.state(true)])) })
    return out
  }

  step (s) {
    const c = this.clients[s.c ?? s.to]
    if (c === undefined) return 'skip'
    switch (s.op) {
      case 'deliver': return this.deliver(s)
      case 'sync': return this.sync(s)
      case 'merged': return this.merged(s)
      case 'raw': return this.raw(s)
      case 'transact': {
        const results = []
        c.doc.transact(() => {
          for (const op of s.ops) {
            try {
              const r = this.local(c, op)
              results.push(r === undefined ? null : r)
            } catch (e) {
              results.push({ err: true })
            }
          }
        }, s.origin ?? null)
        return results
      }
      default: return this.local(c, s)
    }
  }

  local (c, s) {
    switch (s.op) {
      case 'um': return this.undoManager(c, s)
      case 'undo': case 'redo': case 'stop': case 'umclear': {
        const um = c.ums[s.id]
        if (um === undefined) return 'skip'
        let r = null
        if (s.op === 'undo') r = um.undo() !== null
        else if (s.op === 'redo') r = um.redo() !== null
        else if (s.op === 'stop') um.stopCapturing()
        else um.clear()
        return { r, u: um.canUndo(), d: um.canRedo(), n: um.undoStack.length }
      }
      case 'snap': {
        const snap = Y.snapshot(c.doc)
        c.snaps[s.id] = snap
        return { s1: b64(Y.encodeSnapshot(snap)), s2: b64(Y.encodeSnapshotV2(snap)) }
      }
      case 'docFromSnap': {
        const snap = c.snaps[s.id]
        if (snap === undefined) return 'skip'
        const d = Y.createDocFromSnapshot(c.doc, snap)
        const roots = {}
        for (const r of this.script.roots) roots[r.name] = view(rootOf(d, r))
        return { roots, v1: b64(Y.encodeStateAsUpdate(d)) }
      }
      case 'rpos': {
        const t = c.resolve(s.path)
        if (t === null || kindOf(t) === 'map' || kindOf(t) === 'hook') return 'skip'
        const rp = Y.createRelativePositionFromTypeIndex(t, clamp(s.at, t.length), s.assoc ?? 0)
        const bytes = Y.encodeRelativePosition(rp)
        this.rpos[s.id] = bytes
        return { b: b64(bytes), j: view(Y.relativePositionToJSON(rp)) }
      }
      case 'rres': {
        const bytes = this.rpos[s.id]
        if (bytes === undefined) return 'skip'
        const abs = Y.createAbsolutePositionFromRelativePosition(Y.decodeRelativePosition(bytes), c.doc)
        return abs === null ? null : { p: c.pathOf(abs.type), i: abs.index, a: abs.assoc }
      }
    }
    const t = c.resolve(s.path)
    if (t === null) return 'skip'
    const k = kindOf(t)
    const isText = k === 'text' || k === 'xmlt'
    const isSeq = k === 'array' || k === 'xmlf' || k === 'xmle'
    switch (s.op) {
      case 'insert':
        if (!isText) return 'skip'
        t.insert(clamp(s.at, t.length), s.text, attrsOf(s.attrs))
        return
      case 'delete': {
        if (!isText && !isSeq) return 'skip'
        const at = clamp(s.at, t.length)
        t.delete(at, clamp(s.len, t.length - at))
        return
      }
      case 'format':
        if (!isText) return 'skip'
        t.format(clamp(s.at, t.length), s.len | 0, attrsOf(s.attrs))
        return
      case 'embed':
        if (!isText) return 'skip'
        t.insertEmbed(clamp(s.at, t.length), val(s.value), attrsOf(s.attrs))
        return
      case 'delta':
        if (!isText) return 'skip'
        t.applyDelta(deltaOf(s.delta), { sanitize: s.sanitize !== false })
        return
      case 'ains':
        if (!isSeq) return 'skip'
        t.insert(clamp(s.at, t.length), s.values.map(val))
        return
      case 'push':
        if (!isSeq) return 'skip'
        t.push(s.values.map(val))
        return
      case 'unshift':
        if (!isSeq) return 'skip'
        t.unshift(s.values.map(val))
        return
      case 'xafter': {
        if (k !== 'xmlf' && k !== 'xmle') return 'skip'
        const ref = s.ref == null || t.length === 0 ? null : t.get(clamp(s.ref, t.length - 1))
        t.insertAfter(ref, s.values.map(val))
        return
      }
      case 'set':
        if (k !== 'map') return 'skip'
        t.set(s.key, val(s.value))
        return
      case 'mdel':
        if (k !== 'map') return 'skip'
        t.delete(s.key)
        return
      case 'clear':
        if (k !== 'map') return 'skip'
        t.clear()
        return
      case 'setAttr':
        if (k !== 'xmle' && !isText) return 'skip'
        t.setAttribute(s.key, val(s.value))
        return
      case 'rmAttr':
        if (k !== 'xmle' && !isText) return 'skip'
        t.removeAttribute(s.key)
        return
      case 'observe': {
        const label = 'o:' + s.path.join('/')
        t.observe((e, tr) => c.event(label, [e], tr))
        t.observeDeep((es, tr) => c.event('deep' + label, es, tr))
        return
      }
      case 'readSnap': {
        const snap = c.snaps[s.id]
        if (snap === undefined) return 'skip'
        if (isText) {
          const prev = s.prev === undefined ? undefined : c.snaps[s.prev]
          return deltaView(t.toDelta(snap, prev))
        }
        if (isSeq) return Y.typeListToArraySnapshot(t, snap).map(view)
        if (k === 'map') return view(Y.typeMapGetAllSnapshot(t, snap))
        return 'skip'
      }
      default: throw new Error(`unknown op ${s.op}`)
    }
  }

  undoManager (c, s) {
    const scope = []
    for (const p of s.scope) {
      if (p === '$doc') scope.push(c.doc)
      else {
        const t = c.resolve(p)
        if (t !== null) scope.push(t)
      }
    }
    if (scope.length === 0) return 'skip'
    const opts = { captureTimeout: s.timeout === 'inf' ? INF_TIMEOUT : 0 }
    if (s.origins) opts.trackedOrigins = new Set(s.origins)
    const um = new Y.UndoManager(scope, opts)
    const id = `${c.index}.${s.id}`
    this.umIds.set(um, id)
    c.ums[s.id] = um
    um.on('stack-item-added', e => c.push({ w: 'um+', id, type: e.type, origin: this.originLabel(e.origin) }))
    // Ycs has no 'stack-item-updated' (a change merged into the last stack item); see KnownFailures.
    um.on('stack-item-updated', e => c.push({ w: 'um~', id, type: e.type, origin: this.originLabel(e.origin) }))
    um.on('stack-item-popped', e => c.push({ w: 'um-', id, type: e.type, origin: this.originLabel(e.origin) }))
    um.on('stack-cleared', e => c.push({ w: 'umclear', id, u: e.undoStackCleared, r: e.redoStackCleared }))
  }

  updateOf (i, k, v2) {
    const from = this.clients[i]
    if (from === undefined || k < 0 || k >= from.log.length) return null
    return v2 ? from.log[k].v2 : from.log[k].v1
  }

  deliver (s) {
    const to = this.clients[s.to]
    const via = s.via ?? 'v1'
    const u = this.updateOf(s.from, s.k, via === 'v2' || via === 'v2to1')
    if (u === null) return 'skip'
    switch (via) {
      case 'v1': Y.applyUpdate(to.doc, u, NET); return
      case 'v2': Y.applyUpdateV2(to.doc, u, NET); return
      case 'v1to2': {
        const c = Y.convertUpdateFormatV1ToV2(u)
        Y.applyUpdateV2(to.doc, c, NET)
        return { u: b64(c) }
      }
      case 'v2to1': {
        const c = Y.convertUpdateFormatV2ToV1(u)
        Y.applyUpdate(to.doc, c, NET)
        return { u: b64(c) }
      }
      default: throw new Error(`unknown via ${via}`)
    }
  }

  /** y-protocols' sync step 1 / step 2, framed as y-protocols frames them, from `from` to `to`. */
  sync (s) {
    const from = this.clients[s.from]
    const to = this.clients[s.to]
    if (from === undefined) return 'skip'
    const v2 = s.via === 'v2'
    const e1 = encoding.createEncoder()
    encoding.writeVarUint(e1, 0)
    encoding.writeVarUint8Array(e1, Y.encodeStateVector(to.doc))
    const step1 = encoding.toUint8Array(e1)
    // `from` reads step 1 and answers with step 2.
    const d1 = decoding.createDecoder(step1)
    decoding.readVarUint(d1)
    const sv = decoding.readVarUint8Array(d1)
    const e2 = encoding.createEncoder()
    encoding.writeVarUint(e2, 1)
    // With diff, step 2 is diffUpdate of the whole state instead of encodeStateAsUpdate(doc, sv).
    const update2 = s.diff
      ? (v2 ? Y.diffUpdateV2(Y.encodeStateAsUpdateV2(from.doc), sv) : Y.diffUpdate(Y.encodeStateAsUpdate(from.doc), sv))
      : (v2 ? Y.encodeStateAsUpdateV2(from.doc, sv) : Y.encodeStateAsUpdate(from.doc, sv))
    encoding.writeVarUint8Array(e2, update2)
    const step2 = encoding.toUint8Array(e2)
    const d2 = decoding.createDecoder(step2)
    decoding.readVarUint(d2)
    const update = decoding.readVarUint8Array(d2)
    if (v2) Y.applyUpdateV2(to.doc, update, NET)
    else Y.applyUpdate(to.doc, update, NET)
    return { s1: b64(step1), s2: b64(step2) }
  }

  /** An update given as bytes (the structure-aware fuzzer's), applied as v1, v2, or converted v1 to v2 first. */
  raw (s) {
    const to = this.clients[s.to]
    const u = new Uint8Array(Buffer.from(s.u, 'base64'))
    switch (s.via ?? 'v1') {
      case 'v1': Y.applyUpdate(to.doc, u, NET); return
      case 'v2': Y.applyUpdateV2(to.doc, u, NET); return
      case 'v1to2': {
        const c = Y.convertUpdateFormatV1ToV2(u)
        Y.applyUpdateV2(to.doc, c, NET)
        return { u: b64(c) }
      }
      default: throw new Error(`unknown via ${s.via}`)
    }
  }

  merged (s) {
    const to = this.clients[s.to]
    const v2 = s.via === 'v2'
    const updates = s.from.map(([i, k]) => this.updateOf(i, k, v2)).filter(u => u !== null)
    if (updates.length === 0) return 'skip'
    let u = v2 ? Y.mergeUpdatesV2(updates) : Y.mergeUpdates(updates)
    const merged = u
    if (s.diff) u = v2 ? Y.diffUpdateV2(u, Y.encodeStateVector(to.doc)) : Y.diffUpdate(u, Y.encodeStateVector(to.doc))
    const meta = v2 ? Y.parseUpdateMetaV2(u) : Y.parseUpdateMeta(u)
    const r = {
      m: b64(merged),
      u: b64(u),
      sv: b64(v2 ? Y.encodeStateVectorFromUpdateV2(u) : Y.encodeStateVectorFromUpdate(u)),
      from: sortedObject([...meta.from.entries()].map(([c, k]) => [String(c), k])),
      to: sortedObject([...meta.to.entries()].map(([c, k]) => [String(c), k])),
    }
    if (v2) Y.applyUpdateV2(to.doc, u, NET)
    else Y.applyUpdate(to.doc, u, NET)
    return r
  }
}

export function runScript (script) {
  return canon(new Run(script).execute())
}

// ---------------------------------------------------------------- serve: one script per line

if (import.meta.url === `file://${process.argv[1]}`) {
  // Yjs warns on console for some API misuse (e.g. reading a type before it is integrated); the
  // protocol is stdout, so keep stdout for it.
  console.log = console.warn = console.error = () => {}
  const rl = createInterface({ input: process.stdin, crlfDelay: Infinity })
  for await (const line of rl) {
    if (line.trim() === '') continue
    let out
    try {
      out = runScript(JSON.parse(line))
    } catch (e) {
      out = canon({ fatal: String(e && e.stack ? e.stack : e) })
    }
    process.stdout.write(out + '\n')
  }
}
