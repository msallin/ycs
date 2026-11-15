// Differential undo fuzz: random sessions of a local document with an UndoManager, run in real Yjs and
// recorded for tests/Ycs.Corpus/UndoDifferentialTests.cs, which replays each step in Ycs and compares
// after every step: the document state (a hash of encodeStateAsUpdate), what undo()/redo() returned,
// the sizes of both stacks, and the UndoManager events with their order.
//
// Every script draws its own setup: the scope (some of the roots t, m, a, x, or the whole document),
// the capture timeout (0, or merging until stopCapturing), the tracked origins, a deleteFilter, a
// captureTransaction, ignoreRemoteMapChanges, and 0 to 3 remote clients whose updates (concurrent or
// not) are recorded as bytes. Steps: text (with formatting and embeds), map, array, nested map and
// text, XML elements, attributes and texts, several changes in one transaction, untracked and
// uncaptured transactions, undo, redo, stopCapturing, clear with each pair of flags, addToScope.
//
//   cd interop/corpus && npm ci
//   node undo-gen.mjs                      # rewrites undo/undo-differential.json.gz (the pinned set)
//   node undo-gen.mjs 2000 60 /tmp/u.json  # a larger run: scripts, steps, output; replay with
//   YCS_UNDO_FUZZ=/tmp/u.json dotnet test tests/Ycs.Corpus --filter Name~UndoDifferential
//   FULL=1 node undo-gen.mjs ...          # records whole states (hex) instead of hashes, to diagnose
//
// Deterministic: seeded PRNG, fixed client ids; time only decides merging, with a timeout of 0 or 1e9 ms.
import * as Y from 'yjs'
import { createHash } from 'node:crypto'
import { gzipSync } from 'node:zlib'
import { mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'

const here = dirname(fileURLToPath(import.meta.url))
const count = +(process.argv[2] ?? 400)
const steps = +(process.argv[3] ?? 50)
const out = process.argv[4] ?? join(here, 'undo', 'undo-differential.json.gz')

const mulberry32 = (seed) => () => {
  seed |= 0; seed = seed + 0x6D2B79F5 | 0
  let t = Math.imul(seed ^ seed >>> 15, 1 | seed)
  t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t
  return ((t ^ t >>> 14) >>> 0) / 4294967296
}

const hash = (u) => createHash('sha256').update(u).digest('hex').slice(0, 16)
const b64 = (u) => Buffer.from(u).toString('base64')

// An origin whose class is tracked (Yjs compares origin.constructor; Ycs the exact type).
class ClassOrigin {}

const ROOTS = ['t', 'm', 'a', 'x']
const KEYS = ['x', 'y', 'z']
const ORIGINS = { tracked: 'tracked', untracked: 'untracked', nocap: 'nocap' }

const origin = (name) => name === null ? null : name === 'cls' ? new ClassOrigin() : ORIGINS[name]

// The delete filters, by number; Ycs implements the same three.
const DELETE_FILTERS = [
  null,
  (item) => !(item.content instanceof Y.ContentType),
  (item) => item.parentSub !== 'y'
]

const xmlAt = (x, i) => i < x.length ? x.get(i) : null

/** Applies one change, described as data, to doc. Ycs' replay implements the same cases. */
const apply = (doc, op) => {
  const t = doc.getText('t'); const m = doc.getMap('m'); const a = doc.getArray('a'); const x = doc.getXmlFragment('x')
  switch (op[0]) {
    case 'tins': t.insert(op[1], op[2], op[3] ?? undefined); break
    case 'tdel': t.delete(op[1], op[2]); break
    case 'tfmt': t.format(op[1], op[2], { b: op[3] }); break
    case 'temb': t.insertEmbed(op[1], { e: op[2] }); break
    case 'mset': m.set(op[1], op[2]); break
    case 'mdel': m.delete(op[1]); break
    case 'mnest': { const n = new Y.Map(); m.set(op[1], n); n.set('k', op[2]); break }
    case 'mtext': { const n = new Y.Text(); m.set(op[1], n); n.insert(0, op[2]); break }
    case 'mnset': { const n = m.get(op[1]); if (n instanceof Y.Map) n.set(op[2], op[3]); else if (n instanceof Y.Text) n.insert(0, String(op[3])); break }
    case 'ains': a.insert(op[1], [op[2]]); break
    case 'adel': a.delete(op[1], op[2]); break
    case 'anest': { const n = new Y.Map(); a.insert(op[1], [n]); n.set('k', op[2]); break }
    case 'atext': { const n = new Y.Text(); a.insert(op[1], [n]); n.insert(0, op[2]); break }
    case 'nset': { const n = a.get(op[1]); if (n instanceof Y.Map) n.set(op[2], op[3]); else if (n instanceof Y.Text) n.insert(0, String(op[3])); break }
    case 'xel': { const e = new Y.XmlElement('p'); x.insert(op[1], [e]); e.setAttribute('k', op[2]); break }
    case 'xtext': { const n = new Y.XmlText(); x.insert(op[1], [n]); n.insert(0, op[2]); break }
    case 'xattr': { const e = xmlAt(x, op[1]); if (e instanceof Y.XmlElement) { if (op[3] === null) e.removeAttribute(op[2]); else e.setAttribute(op[2], op[3]) } break }
    case 'xchild': {
      const e = xmlAt(x, op[1])
      if (e instanceof Y.XmlElement) e.insert(0, [new Y.XmlText(op[2])])
      else if (e instanceof Y.XmlText) e.insert(0, op[2])
      break
    }
    case 'xfmt': { const e = xmlAt(x, op[1]); if (e instanceof Y.XmlText && e.length > 0) e.format(0, Math.min(op[2], e.length), { i: op[3] }); break }
    case 'xdel': x.delete(op[1], op[2]); break
    default: throw new Error('op ' + op[0])
  }
}

const randOp = (doc, ri, r) => {
  const t = doc.getText('t'); const a = doc.getArray('a'); const x = doc.getXmlFragment('x')
  const key = () => KEYS[ri(3)]
  for (;;) {
    switch (ri(20)) {
      case 0: case 1: return ['tins', ri(t.length + 1), 'abcdefgh'.slice(0, 1 + ri(4)), ri(4) === 0 ? { b: true } : null]
      case 2: if (t.length) { const i = ri(t.length); return ['tdel', i, 1 + ri(Math.min(3, t.length - i))] } break
      case 3: if (t.length) { const i = ri(t.length); return ['tfmt', i, 1 + ri(Math.min(3, t.length - i)), ri(2) ? true : null] } break
      case 4: return ['temb', ri(t.length + 1), ri(100)]
      case 5: return ['mset', key(), ri(3) ? ri(100) : 's' + ri(10)]
      case 6: return ['mdel', key()]
      case 7: return ['mnest', key(), ri(100)]
      case 8: return ['mtext', key(), 'uv']
      case 9: return ['mnset', key(), key(), ri(100)]
      case 10: return ['ains', ri(a.length + 1), ri(100)]
      case 11: if (a.length) { const i = ri(a.length); return ['adel', i, 1 + ri(Math.min(2, a.length - i))] } break
      case 12: return ['anest', ri(a.length + 1), ri(100)]
      case 13: return ['atext', ri(a.length + 1), 'pq']
      case 14: if (a.length) return ['nset', ri(a.length), key(), ri(100)]; break
      case 15: return ['xel', ri(x.length + 1), 'v' + ri(10)]
      case 16: return ['xtext', ri(x.length + 1), 'xy']
      case 17: if (x.length) return ['xattr', ri(x.length), ri(2) ? 'k' : 'l', ri(3) ? 'w' + ri(10) : null]; break
      case 18: if (x.length) return r() < 0.5 ? ['xchild', ri(x.length), 'c' + ri(10)] : ['xfmt', ri(x.length), 1 + ri(2), ri(2) ? true : null]; break
      case 19: if (x.length) { const i = ri(x.length); return ['xdel', i, 1 + ri(Math.min(2, x.length - i))] } break
    }
  }
}

const runScript = (seed) => {
  const r = mulberry32(seed * 7919 + 1); const ri = n => Math.floor(r() * n)
  const doc = new Y.Doc(); doc.clientID = 1
  for (const name of ROOTS) name === 'x' ? doc.getXmlFragment(name) : name === 't' ? doc.getText(name) : name === 'm' ? doc.getMap(name) : doc.getArray(name)

  // The setup of this script.
  const docScope = ri(5) === 0
  const scope = docScope ? null : ROOTS.filter(() => ri(3) !== 0)
  if (scope !== null && scope.length === 0) scope.push(ROOTS[ri(4)])
  const config = {
    scope: scope === null ? null : [...scope],
    timeout: ri(3) === 0 ? 1e9 : 0,
    tracked: ri(3) === 0 ? [null, 'tracked', 'cls'] : null,
    deleteFilter: ri(4) === 0 ? 1 + ri(2) : 0,
    captureTransaction: ri(4) === 0,
    ignoreRemoteMapChanges: ri(4) === 0,
    remotes: [0, 1, 1, 2, 3][ri(5)]
  }
  const rootOf = (name) => doc.share.get(name)
  const trackedOrigins = config.tracked === null ? undefined : new Set(config.tracked.map(o => o === 'cls' ? ClassOrigin : o))
  const options = {
    captureTimeout: config.timeout,
    trackedOrigins,
    deleteFilter: DELETE_FILTERS[config.deleteFilter] ?? undefined,
    captureTransaction: config.captureTransaction ? (tr) => tr.origin !== 'nocap' : undefined,
    ignoreRemoteMapChanges: config.ignoreRemoteMapChanges
  }
  for (const k of Object.keys(options)) if (options[k] === undefined) delete options[k]
  const um = new Y.UndoManager(docScope ? doc : scope.map(rootOf), options)

  let events = []
  um.on('stack-item-added', e => events.push('added ' + e.type + ' ' + e.changedParentTypes.size))
  um.on('stack-item-updated', e => events.push('updated ' + e.type + ' ' + e.changedParentTypes.size))
  um.on('stack-item-popped', e => events.push('popped ' + e.type + ' ' + e.changedParentTypes.size))
  um.on('stack-cleared', e => events.push('cleared ' + e.undoStackCleared + ' ' + e.redoStackCleared))

  const remotes = []
  for (let k = 0; k < config.remotes; k++) { const d = new Y.Doc(); d.clientID = 2 + k; remotes.push(d) }

  const ops = []
  for (let i = 0; i < steps; i++) {
    const k = ri(100); let op; let ret = null
    events = []
    if (k < 18) { op = ['undo']; ret = um.undo() !== null }
    else if (k < 30) { op = ['redo']; ret = um.redo() !== null }
    else if (k < 34 && config.timeout > 0) { op = ['stop']; um.stopCapturing() }
    else if (k < 36) { const flags = [[true, false], [false, true], [true, true]][ri(3)]; op = ['clear', ...flags]; um.clear(...flags) }
    else if (k < 37 && !docScope && scope.length < ROOTS.length) {
      const name = ROOTS.find(n => !scope.includes(n)); scope.push(name); op = ['scope', name]; um.addToScope(rootOf(name))
    } else if (k < 55 && remotes.length > 0) {
      const rd = remotes[ri(remotes.length)]
      // Half of the remote changes are made without the latest local state: concurrent ones.
      if (ri(2) === 0) Y.applyUpdate(rd, Y.encodeStateAsUpdate(doc, Y.encodeStateVector(rd)))
      const sv = Y.encodeStateVector(doc)
      rd.transact(() => apply(rd, randOp(rd, ri, r)))
      const u = Y.encodeStateAsUpdate(rd, sv)
      op = ['remote', b64(u)]
      Y.applyUpdate(doc, u, 'remote')
    } else {
      const originName = k < 62 ? 'untracked' : k < 66 && config.tracked !== null ? (ri(2) ? 'tracked' : 'cls') : k < 69 && config.captureTransaction ? 'nocap' : null
      const n = ri(6) === 0 ? 2 + ri(2) : 1
      const changes = []
      doc.transact(() => {
        for (let c = 0; c < n; c++) { const ch = randOp(doc, ri, r); changes.push(ch); apply(doc, ch) }
      }, origin(originName))
      op = ['local', originName, changes]
    }
    ops.push({ op, ret, state: process.env.FULL ? Buffer.from(Y.encodeStateAsUpdate(doc)).toString('hex') : hash(Y.encodeStateAsUpdate(doc)), undo: um.undoStack.length, redo: um.redoStack.length, events })
  }
  return { seed, config, ops }
}

const scripts = []
for (let s = 0; s < count; s++) scripts.push(runScript(s))
mkdirSync(dirname(out), { recursive: true })
const text = JSON.stringify({ yjs: createRequire(import.meta.url)('yjs/package.json').version, steps, scripts })
writeFileSync(out, out.endsWith('.gz') ? gzipSync(text, { level: 9 }) : text)
console.log(`${scripts.length} scripts of ${steps} steps -> ${out}`)
