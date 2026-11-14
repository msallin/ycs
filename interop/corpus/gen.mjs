// Generates the recorded Yjs corpus under ./cases: one JSON file per case, holding the updates
// real Yjs produced (v1 and v2, in the order an observer received them) and the state Yjs ends
// up in after applying them. Deterministic: seeded PRNG, fixed client ids, no clocks.
// Usage: node gen.mjs            (rewrites ./cases)
import * as Y from 'yjs'
import { mkdirSync, readdirSync, rmSync, writeFileSync, readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createRequire } from 'node:module'
import { b64, deleteSetOf, docFrom, itemOrderOf, plain, stateOf, stateVectorOf } from './lib.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const outDir = join(here, 'cases')
const yjsVersion = JSON.parse(readFileSync(createRequire(import.meta.url).resolve('yjs/package.json'), 'utf8')).version
const REMOTE = Symbol('remote')

// ---------------------------------------------------------------- helpers

function mulberry32 (seed) {
  let a = seed >>> 0
  return () => {
    a = (a + 0x6D2B79F5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

class Rng {
  constructor (seed) { this.next = mulberry32(seed) }
  int (n) { return Math.floor(this.next() * n) }
  bool (p = 0.5) { return this.next() < p }
  pick (arr) { return arr[this.int(arr.length)] }
  shuffle (arr) {
    const a = arr.slice()
    for (let i = a.length - 1; i > 0; i--) {
      const j = this.int(i + 1);
      [a[i], a[j]] = [a[j], a[i]]
    }
    return a
  }
  word (alphabet, max = 4) {
    let s = ''
    const n = 1 + this.int(max)
    for (let i = 0; i < n; i++) s += this.pick(alphabet)
    return s
  }
}

/** A set of collaborating Yjs docs; every local transaction is logged once as {v1, v2}. */
class Sim {
  constructor (clientIds, { gc = true, seed = 1 } = {}) {
    this.rng = new Rng(seed)
    this.log = []
    this.docs = clientIds.map(id => {
      const doc = new Y.Doc({ gc })
      doc.clientID = id
      let v1 = null
      doc.on('update', (u, origin) => { if (origin !== REMOTE) v1 = u })
      doc.on('updateV2', (u, origin) => {
        if (origin !== REMOTE) { this.log.push({ client: id, v1, v2: u }); v1 = null }
      })
      return doc
    })
    this.seen = this.docs.map(() => new Set())
    // What happened, as data, for a replay by Ycs clients: {client, ops} or {deliver, to}.
    // Only complete when every local change went through ops() (scripted).
    this.steps = []
    this.scripted = true
  }

  /** Like local(), with the changes as data (see runOp), so that the case can be replayed by clients. */
  ops (i, ops) {
    const before = this.log.length
    this.docs[i].transact(() => { for (const op of ops) runOp(this.docs[i], op) })
    for (let k = before; k < this.log.length; k++) this.seen[i].add(k)
    this.steps.push({ client: i, ops })
  }

  deliver (i, k) {
    Y.applyUpdate(this.docs[i], this.log[k].v1, REMOTE)
    this.seen[i].add(k)
    this.steps.push({ deliver: k, to: i })
  }

  /** Runs fn(doc) as one transaction on client i, i.e. one update. */
  local (i, fn) {
    this.scripted = false
    const before = this.log.length
    this.docs[i].transact(() => fn(this.docs[i]))
    for (let k = before; k < this.log.length; k++) this.seen[i].add(k)
  }

  /** Delivers to client i every logged update it has not seen yet, in random order. */
  sync (i) {
    const missing = []
    for (let k = 0; k < this.log.length; k++) if (!this.seen[i].has(k)) missing.push(k)
    for (const k of this.rng.shuffle(missing)) this.deliver(i, k)
  }

  syncAll () { this.docs.forEach((_, i) => this.sync(i)) }

  /** Delivers to client i, in log (causal) order, every update client j has and i has not. */
  syncFrom (i, j) {
    for (let k = 0; k < this.log.length; k++) {
      if (this.seen[j].has(k) && !this.seen[i].has(k)) this.deliver(i, k)
    }
  }

  updates (order = this.log.map((_, k) => k)) { return order.map(k => this.log[k]) }
}

/** One change of a text root, as data: what tests/Ycs.Corpus replays (ClientReplayTests). */
function runOp (doc, { op, root, at, text, length, embed, attributes }) {
  const t = doc.getText(root)
  switch (op) {
    case 'insert': return t.insert(at, text, attributes)
    case 'delete': return t.delete(at, length)
    case 'format': return t.format(at, length, attributes)
    case 'embed': return t.insertEmbed(at, embed, attributes)
    default: throw new Error(op)
  }
}

function randomTextEdit (rng, text, alphabet, { maxDelete = 5 } = {}) {
  const len = text.length
  if (len > 0 && rng.bool(0.35)) {
    const i = rng.int(len)
    text.delete(i, 1 + rng.int(Math.min(maxDelete, len - i)))
  } else {
    text.insert(rng.int(len + 1), rng.word(alphabet))
  }
}

/** Code-point boundaries of a JS string, as UTF-16 offsets (including 0 and length). */
function codePointBoundaries (s) {
  const b = [0]
  let i = 0
  for (const ch of s) { i += ch.length; b.push(i) }
  return b
}

// ---------------------------------------------------------------- recording

function assertSame (what, a, b) {
  if (JSON.stringify(a) !== JSON.stringify(b)) {
    throw new Error(`${what}: ${JSON.stringify(a).slice(0, 300)} !== ${JSON.stringify(b).slice(0, 300)}`)
  }
}

const cases = []

/**
 * Records one case. `updates` are {v1, v2} in the order the observer receives them (may hold
 * duplicates and be out of causal order). The expected state is the observer's, not a client's.
 */
function record (name, description, { updates, roots, gc = true, diffAt, clients }) {
  const v1s = updates.map(u => u.v1)
  const v2s = updates.map(u => u.v2)
  const observer = docFrom(v1s, gc)
  if (observer.store.pendingStructs !== null || observer.store.pendingDs !== null) {
    throw new Error(`${name}: observer has pending structs`)
  }
  const content = stateOf(observer, roots)
  assertSame(`${name} v2`, stateOf(docFrom(v2s, gc, true), roots), content)

  const k = diffAt ?? Math.floor(updates.length / 2)
  const half = docFrom(v1s.slice(0, k), gc)
  const sv = Y.encodeStateVector(half)
  const diffV1 = Y.encodeStateAsUpdate(observer, sv)
  const diffV2 = Y.encodeStateAsUpdateV2(observer, sv)
  Y.applyUpdate(half, diffV1)
  assertSame(`${name} diff`, stateOf(half, roots), content)

  const mergedV1 = Y.mergeUpdates(v1s)
  const mergedV2 = Y.mergeUpdatesV2(v2s)
  assertSame(`${name} merged`, stateOf(docFrom([mergedV1], gc), roots), content)
  assertSame(`${name} mergedV2`, stateOf(docFrom([mergedV2], gc, true), roots), content)

  const stateV1 = Y.encodeStateAsUpdate(observer)
  const diffMeta = Y.parseUpdateMeta(diffV1)
  const metaObj = m => Object.fromEntries([...m].sort((a, b) => a[0] - b[0]).map(([c, v]) => [String(c), v]))

  cases.push({
    name,
    description,
    yjs: yjsVersion,
    gc,
    roots: roots.map(r => ({ name: r.name, type: r.type, delta: !!r.delta })),
    updates: updates.map(u => ({ v1: b64(u.v1), v2: b64(u.v2) })),
    expected: {
      content,
      stateVector: stateVectorOf(observer),
      deleteSet: deleteSetOf(observer),
      stateV1: b64(stateV1),
      stateV2: b64(Y.encodeStateAsUpdateV2(observer)),
      stateVectorV1: b64(Y.encodeStateVector(observer)),
      itemOrder: itemOrderOf(observer, roots),
    },
    diff: {
      prefix: k,
      stateVector: b64(sv),
      stateVectorMap: stateVectorOf(docFrom(v1s.slice(0, k), gc)),
      updateV1: b64(diffV1),
      updateV2: b64(diffV2),
      from: metaObj(diffMeta.from),
      to: metaObj(diffMeta.to),
    },
    merged: { v1: b64(mergedV1), v2: b64(mergedV2) },
    // A scripted session (Sim.ops): what each client did and received, and where it ended up.
    ...(clients && clients.scripted
      ? {
          script: clients.steps,
          clients: clients.docs.map(d => ({ id: d.clientID, stateV1: b64(Y.encodeStateAsUpdate(d)), stateV2: b64(Y.encodeStateAsUpdateV2(d)), content: stateOf(d, roots) })),
        }
      : {}),
  })
}

// ---------------------------------------------------------------- cases

const ASCII = 'abcdefghijklmnopqrstuvwxyz ABC.,'
const T = [{ name: 'text', type: 'text' }]

{
  // One client typing a sentence key by key, with backspaces and cursor jumps.
  const sim = new Sim([1], { seed: 11 })
  const rng = new Rng(12)
  const sentence = 'The quick brown fox jumps over the lazy dog. Pack my box with five dozen liquor jugs.'
  let cursor = 0
  for (const ch of sentence) {
    sim.local(0, d => d.getText('text').insert(cursor, ch))
    cursor++
    if (rng.bool(0.1)) { sim.local(0, d => d.getText('text').delete(cursor - 1, 1)); cursor-- }
    if (rng.bool(0.05)) cursor = rng.int(sim.docs[0].getText('text').length + 1)
  }
  record('text-single-client-typing', 'One client types a sentence key by key, one update per key, with backspaces and cursor jumps.', { updates: sim.updates(), roots: T })
}

for (const n of [2, 3, 5]) {
  // n clients insert concurrently at the same positions; the tie is broken by client id.
  const ids = [7, 3, 900, 42, 1000000].slice(0, n)
  const sim = new Sim(ids, { seed: 20 + n })
  sim.local(0, d => d.getText('text').insert(0, 'ab'))
  sim.syncAll()
  for (let round = 0; round < 3; round++) {
    for (let i = 0; i < n; i++) {
      sim.local(i, d => d.getText('text').insert(1, `[${i}.${round}]`))
      sim.local(i, d => d.getText('text').insert(0, `${i}`))
      sim.local(i, d => { const t = d.getText('text'); t.insert(t.length, `<${i}>`) })
    }
    sim.syncAll()
  }
  record(`text-concurrent-same-position-${n}`, `${n} clients (ids ${ids.join(', ')}) insert concurrently at the start, the end and the same inner position; three rounds.`, { updates: sim.updates(), roots: T })
}

{
  // Random inserts and deletes with partial syncs between rounds.
  const sim = new Sim([10, 20, 30], { seed: 31 })
  for (let round = 0; round < 10; round++) {
    for (let i = 0; i < 3; i++) for (let e = 0; e < 3; e++) sim.local(i, d => randomTextEdit(sim.rng, d.getText('text'), ASCII))
    for (let i = 0; i < 3; i++) if (sim.rng.bool(0.6)) sim.sync(i)
  }
  sim.syncAll()
  record('text-interleaved-deletes', 'Three clients interleave random inserts and deletes, syncing only partially between rounds.', { updates: sim.updates(), roots: T })
}

{
  // Each client deletes ranges that span what the others inserted, concurrently and overlapping.
  const sim = new Sim([1, 2, 3], { seed: 41 })
  sim.local(0, d => d.getText('text').insert(0, 'AAAAAAAAAA'))
  sim.syncAll()
  sim.local(1, d => d.getText('text').insert(5, 'BBBBBBBBBB'))
  sim.local(2, d => d.getText('text').insert(10, 'CCCCCCCCCC'))
  sim.syncAll()
  sim.local(0, d => d.getText('text').delete(3, 9))
  sim.local(1, d => d.getText('text').delete(8, 12))
  sim.local(2, d => d.getText('text').delete(0, 4))
  sim.local(2, d => d.getText('text').insert(2, 'cc'))
  sim.syncAll()
  sim.local(1, d => { const t = d.getText('text'); t.delete(1, t.length - 2) })
  sim.local(0, d => d.getText('text').insert(1, 'aa'))
  sim.syncAll()
  record('text-delete-others-ranges', 'Clients delete overlapping ranges that span text inserted by the others, concurrently.', { updates: sim.updates(), roots: T })
}

function randomEditing (seed, ids, rounds, alphabet, opts) {
  const sim = new Sim(ids, { seed })
  for (let round = 0; round < rounds; round++) {
    for (let i = 0; i < ids.length; i++) for (let e = 0; e < 2; e++) sim.local(i, d => randomTextEdit(sim.rng, d.getText('text'), alphabet, opts))
    sim.sync(sim.rng.int(ids.length))
  }
  sim.syncAll()
  return sim
}

{
  const sim = randomEditing(51, [5, 6, 7, 8], 12, ASCII)
  const rng = new Rng(52)
  const order = rng.shuffle(sim.log.map((_, k) => k))
  const withDups = []
  for (const k of order) { withDups.push(k); if (rng.bool(0.3)) withDups.push(rng.pick(order)) }
  record('text-out-of-order-duplicated', 'Four clients edit at random; the observer receives the updates shuffled, with about 30 % duplicates.', { updates: sim.updates(withDups), roots: T })
}

{
  const sim = randomEditing(61, [100, 200], 10, ASCII)
  record('text-reverse-order', 'Two clients edit at random; the observer receives every update in reverse order, so all but the last stay pending until the first arrives.', { updates: sim.updates(sim.log.map((_, k) => k).reverse()), roots: T })
}

{
  // Large runs: big pastes, big deletes, and a long sequential run that Yjs merges into one item.
  const sim = new Sim([1, 2, 3], { seed: 71 })
  const rng = new Rng(72)
  const chunk = n => { let s = ''; for (let i = 0; i < n; i++) s += ASCII[rng.int(ASCII.length)]; return s }
  sim.local(0, d => d.getText('text').insert(0, chunk(6000)))
  sim.syncAll()
  sim.local(1, d => d.getText('text').insert(3000, chunk(3000)))
  sim.local(2, d => d.getText('text').delete(1000, 2500))
  sim.syncAll()
  sim.local(0, d => d.getText('text').delete(4000, 3000))
  for (let i = 0; i < 300; i++) sim.local(2, d => d.getText('text').insert(100 + i, ASCII[i % ASCII.length]))
  sim.syncAll()
  record('text-large-runs', 'Pastes of 6000 and 3000 characters, deletes of 2500 and 3000, and a 300-key sequential run.', { updates: sim.updates(), roots: T })
}

const UNICODE = ['😀', '👍🏽', '👨‍👩‍👧‍👦', '🇩🇪', 'é', 'é', 'ñ', '中', '文', 'ア', 'ب', 'ש', '한', '𝄞', '\u{10FFFF}', 'x', ' ', '\n', '\t', '"', '\\']

{
  // Unicode at code-point boundaries: surrogate pairs, ZWJ sequences, combining marks, RTL, CJK.
  const sim = new Sim([1, 2, 3], { seed: 81 })
  for (let round = 0; round < 8; round++) {
    for (let i = 0; i < 3; i++) {
      sim.local(i, d => {
        const t = d.getText('text')
        const b = codePointBoundaries(t.toString())
        if (b.length > 2 && sim.rng.bool(0.3)) {
          const a = sim.rng.int(b.length - 1)
          const e = Math.min(b.length - 1, a + 1 + sim.rng.int(3))
          t.delete(b[a], b[e] - b[a])
        } else {
          t.insert(sim.rng.pick(b), sim.rng.word(UNICODE))
        }
      })
    }
    sim.syncAll()
  }
  record('text-unicode', 'Inserts and deletes at code-point boundaries of surrogate pairs, ZWJ sequences, flags, combining marks, RTL and CJK text.', { updates: sim.updates(), roots: T })
}

{
  // Unicode at arbitrary UTF-16 offsets: inserts and deletes inside surrogate pairs.
  const sim = randomEditing(91, [11, 12], 8, ['😀', '𝄞', '👍🏽', 'a', 'é'], { maxDelete: 3 })
  record('text-unicode-split-surrogates', 'Random edits at UTF-16 offsets, splitting surrogate pairs (Yjs replaces a split pair with U+FFFD).', { updates: sim.updates(), roots: T })
}

{
  // Formatting attributes, concurrently and overlapping, plus removal and an embed.
  const sim = new Sim([1, 2, 3], { seed: 101 })
  sim.local(0, d => d.getText('text').insert(0, 'Hello wonderful world of rich text'))
  sim.syncAll()
  sim.local(0, d => d.getText('text').format(0, 15, { bold: true }))
  sim.local(1, d => d.getText('text').format(6, 15, { italic: true }))
  sim.local(2, d => d.getText('text').format(10, 10, { bold: null, color: '#ff0000' }))
  sim.syncAll()
  sim.local(0, d => d.getText('text').insert(5, ' there', { underline: true }))
  sim.local(1, d => d.getText('text').insertEmbed(0, { image: 'logo.png' }))
  sim.local(2, d => d.getText('text').format(0, 40, { link: 'https://example.org/' }))
  sim.syncAll()
  sim.local(1, d => d.getText('text').format(3, 6, { link: null, italic: null }))
  sim.local(0, d => d.getText('text').delete(12, 4))
  sim.local(2, d => d.getText('text').insert(20, 'X', { bold: true, italic: true }))
  sim.syncAll()
  record('text-formatting', 'Concurrent, overlapping bold/italic/color/link formatting, attribute removal with null, formatted inserts and an embed.', { updates: sim.updates(), roots: [{ name: 'text', type: 'text', delta: true }] })
}

// ---- concurrent formatting: what Yjs 13.6's cleanup (cleanupYTextAfterTransaction,
// cleanupFormattingGap, cleanupContextlessFormattingGap) does and leaves, at the observer and, as
// these cases are scripted, at every client (their cleanup transactions are updates in the log).

const TD = [{ name: 'text', type: 'text', delta: true }]
const ins = (at, text, attributes) => ({ op: 'insert', root: 'text', at, text, attributes })
const del = (at, length) => ({ op: 'delete', root: 'text', at, length })
const fmt = (at, length, attributes) => ({ op: 'format', root: 'text', at, length, attributes })
const emb = (at, embed, attributes) => ({ op: 'embed', root: 'text', at, embed, attributes })
const textLength = (sim, i) => sim.docs[i].getText('text').length

for (const synced of [false, true]) {
  // Two clients bold overlapping ranges of one text; the observer gets both formats before (or
  // after) the clients saw each other's.
  const sim = new Sim([1, 2], { seed: 111 })
  sim.ops(0, [ins(0, 'abcdef')])
  sim.syncAll()
  sim.ops(0, [fmt(0, 4, { bold: true })])
  sim.ops(1, [fmt(2, 4, { bold: true })])
  if (synced) sim.syncAll()
  record(`text-format-overlapping-bold-2${synced ? '-synced' : ''}`, `Two clients bold overlapping ranges concurrently${synced ? ', then sync (their cleanups are in the log)' : ''}.`, { updates: sim.updates(), roots: TD, clients: sim })
}

{
  // Three clients: overlapping bold, bold removed with null over a range that crosses both, and
  // italic across all of it, in rounds with partial syncs.
  const sim = new Sim([1, 2, 3], { seed: 112 })
  sim.ops(0, [ins(0, 'the quick brown fox jumps')])
  sim.syncAll()
  sim.ops(0, [fmt(0, 12, { bold: true })])
  sim.ops(1, [fmt(4, 12, { bold: true })])
  sim.ops(2, [fmt(8, 12, { bold: true })])
  sim.syncFrom(1, 0)
  sim.ops(1, [fmt(2, 6, { bold: null })])
  sim.ops(2, [fmt(0, 25, { italic: true })])
  sim.syncAll()
  sim.ops(0, [fmt(10, 5, { bold: true, italic: null })])
  sim.ops(2, [fmt(6, 10, { bold: null })])
  sim.syncAll()
  record('text-format-overlapping-bold-3', 'Three clients bold overlapping ranges, remove bold with null over ranges that cross them and add italic, with partial syncs.', { updates: sim.updates(), roots: TD, clients: sim })
}

{
  // A format racing deletes of its range: the deleted text leaves a gap of formats behind.
  const sim = new Sim([1, 2, 3], { seed: 113 })
  sim.ops(0, [ins(0, 'abcdefghijkl')])
  sim.syncAll()
  sim.ops(0, [fmt(2, 6, { bold: true })])
  sim.ops(1, [del(3, 4)])
  sim.ops(2, [fmt(4, 6, { color: 'red' })])
  sim.syncAll()
  sim.ops(0, [del(0, 4)])
  sim.ops(1, [fmt(0, 3, { bold: null, color: 'blue' })])
  sim.syncAll()
  sim.ops(2, [del(0, textLength(sim, 2))])
  sim.ops(0, [ins(0, 'z', { bold: true })])
  sim.syncAll()
  record('text-format-then-delete', 'Formats racing deletes of the formatted text, then everything deleted while a formatted insert races it.', { updates: sim.updates(), roots: TD, clients: sim })
}

{
  // Formats across text other clients insert concurrently inside and at the edges of the range.
  const sim = new Sim([1, 2, 3], { seed: 114 })
  sim.ops(0, [ins(0, 'abcdefgh')])
  sim.syncAll()
  sim.ops(0, [fmt(1, 6, { bold: true })])
  sim.ops(1, [ins(4, 'XY'), ins(1, 'P'), ins(10, 'Q')])
  sim.ops(2, [ins(7, 'mn', { italic: true })])
  sim.syncAll()
  sim.ops(1, [fmt(0, 6, { italic: true, bold: null })])
  sim.ops(2, [ins(3, 'r')])
  sim.ops(0, [ins(6, 's', { bold: true })])
  sim.syncAll()
  record('text-format-across-concurrent-inserts', 'Formats over ranges into which other clients insert, inside and at the edges, plain and formatted.', { updates: sim.updates(), roots: TD, clients: sim })
}

{
  // Removing formats with null, concurrently and overlapping, over text that is bold throughout.
  const sim = new Sim([1, 2, 3], { seed: 115, gc: false })
  sim.ops(0, [ins(0, 'abcdefghijklmnop'), fmt(0, 16, { bold: true, size: 12 })])
  sim.syncAll()
  sim.ops(0, [fmt(2, 6, { bold: null })])
  sim.ops(1, [fmt(5, 6, { bold: null, size: null })])
  sim.ops(2, [fmt(4, 3, { size: 14 })])
  sim.syncAll()
  sim.ops(1, [ins(6, 'w')])
  sim.ops(2, [fmt(0, 16, { bold: null })])
  sim.ops(0, [fmt(8, 4, { bold: true })])
  sim.syncAll()
  record('text-format-remove-null', 'Formats removed with null, concurrently and overlapping, then all removed while one is set again (gc off).', { updates: sim.updates(), roots: TD, gc: false, clients: sim })
}

for (const seed of [121, 122, 123, 124, 125, 126]) {
  // Random rich-text editing: inserts with and without attributes, formats setting and removing
  // keys (null), deletes and embeds, from three clients with partial syncs between rounds.
  const sim = new Sim([1, 2, 3], { seed })
  const rng = new Rng(seed + 1000)
  const values = { bold: [true, null], italic: [true, null], color: ['red', 'blue', null] }
  const someAttributes = () => {
    const a = {}
    for (const key of Object.keys(values)) if (rng.bool(0.4)) a[key] = rng.pick(values[key])
    return a
  }
  sim.ops(0, [ins(0, 'abcdefghij')])
  sim.syncAll()
  for (let round = 0; round < 6; round++) {
    for (let i = 0; i < 3; i++) {
      for (let n = 1 + rng.int(3); n > 0; n--) {
        const len = textLength(sim, i)
        const op = rng.int(10)
        if (op < 4 && len > 0) {
          const at = rng.int(len)
          sim.ops(i, [fmt(at, 1 + rng.int(Math.min(6, len - at)), someAttributes())])
        } else if (op < 6 && len > 0) {
          const at = rng.int(len)
          sim.ops(i, [del(at, 1 + rng.int(Math.min(3, len - at)))])
        } else if (op < 7) {
          sim.ops(i, [emb(rng.int(len + 1), { image: 'i' + rng.int(9) }, rng.bool() ? someAttributes() : undefined)])
        } else {
          sim.ops(i, [ins(rng.int(len + 1), rng.word('xyz', 3), rng.bool() ? someAttributes() : undefined)])
        }
      }
    }
    if (rng.bool(0.6)) sim.syncFrom(rng.int(3), rng.int(3))
    if (round % 2 === 1) sim.syncAll()
  }
  sim.syncAll()
  record(`text-format-random-${seed}`, `Random rich-text editing by three clients (seed ${seed}): formats setting and removing keys, formatted inserts, embeds, deletes, partial syncs.`, { updates: sim.updates(), roots: TD, clients: sim })
}

{
  // Y.Map: every scalar type, overwrites, deletes, and concurrent writes to the same key.
  const sim = new Sim([1, 2, 3], { seed: 111 })
  sim.local(0, d => {
    const m = d.getMap('map')
    m.set('string', 'hello')
    m.set('int', 42)
    m.set('negative', -7)
    m.set('big', 2 ** 40)
    m.set('float', 3.5)
    m.set('double', 0.1)
    m.set('true', true)
    m.set('false', false)
    m.set('null', null)
    m.set('object', { a: 1, b: [1, 2, { c: 'd' }], e: null })
    m.set('list', ['x', 1, false])
    m.set('bytes', new Uint8Array([0, 1, 2, 254, 255]))
    m.set('unicode', 'ünï😀')
  })
  sim.syncAll()
  sim.local(0, d => d.getMap('map').set('contested', 'from 1'))
  sim.local(1, d => d.getMap('map').set('contested', 'from 2'))
  sim.local(2, d => d.getMap('map').set('contested', 'from 3'))
  sim.local(1, d => d.getMap('map').delete('int'))
  sim.local(2, d => d.getMap('map').set('int', 43))
  sim.local(0, d => d.getMap('map').set('string', 'overwritten'))
  sim.syncAll()
  sim.local(1, d => { d.getMap('map').delete('null'); d.getMap('map').set('later', [1, 2, 3]) })
  sim.syncAll()
  record('map-basics', 'Y.Map with every scalar type, binary, JSON values, overwrites, deletes and concurrent writes to one key.', { updates: sim.updates(), roots: [{ name: 'map', type: 'map' }] })
}

{
  // Y.Array: inserts, pushes, range deletes, concurrent inserts at one index.
  const sim = new Sim([4, 5, 6], { seed: 121 })
  sim.local(0, d => d.getArray('array').insert(0, [1, 2, 3, 'four', { five: 5 }, [6], true, null]))
  sim.syncAll()
  for (let round = 0; round < 6; round++) {
    for (let i = 0; i < 3; i++) {
      sim.local(i, d => {
        const a = d.getArray('array')
        if (a.length > 2 && sim.rng.bool(0.4)) {
          const at = sim.rng.int(a.length - 1)
          a.delete(at, 1 + sim.rng.int(Math.min(3, a.length - at)))
        } else if (sim.rng.bool(0.3)) {
          a.push([`p${i}.${round}`])
        } else {
          a.insert(Math.min(1, a.length), [`c${i}.${round}`, round * 10 + i])
        }
      })
    }
    sim.syncAll()
  }
  record('array-basics', 'Y.Array inserts, pushes, range deletes and concurrent inserts at the same index.', { updates: sim.updates(), roots: [{ name: 'array', type: 'array' }] })
}

{
  // Nested shared types: map > array > map, a text inside a map, edits racing a parent delete.
  const sim = new Sim([1, 2], { seed: 131 })
  sim.local(0, d => {
    const root = d.getMap('root')
    const list = new Y.Array()
    root.set('list', list)
    const item = new Y.Map()
    list.push([item])
    item.set('title', 'first')
    const body = new Y.Text()
    root.set('body', body)
    body.insert(0, 'nested text')
    const doomed = new Y.Map()
    root.set('doomed', doomed)
    doomed.set('k', 'v')
  })
  sim.syncAll()
  sim.local(0, d => d.getMap('root').get('list').push([{ json: true }]))
  sim.local(1, d => {
    const list = d.getMap('root').get('list')
    const m = new Y.Map()
    list.insert(0, [m])
    m.set('title', 'second')
    m.set('inner', new Y.Array())
    m.get('inner').push(['deep'])
  })
  sim.local(0, d => d.getMap('root').get('body').insert(0, 'A '))
  sim.local(1, d => d.getMap('root').get('body').insert(11, '!'))
  sim.local(0, d => d.getMap('root').delete('doomed'))
  sim.local(1, d => d.getMap('root').get('doomed').set('k2', 'edited after delete'))
  sim.local(1, d => d.getArray('top').push([new Y.Text('text in array')]))
  sim.syncAll()
  sim.local(0, d => d.getMap('root').get('list').get(0).get('inner').push(['deeper']))
  sim.syncAll()
  record('nested-types', 'Y.Map holding Y.Array of Y.Map, a Y.Text in a map and in an array, and edits racing a delete of their parent.', { updates: sim.updates(), roots: [{ name: 'root', type: 'map' }, { name: 'top', type: 'array' }] })
}

{
  // Delivered as merged updates and as state-vector diffs, the way providers sync.
  const sim = randomEditing(141, [1, 2, 3], 6, ASCII)
  const groups = [sim.log.slice(0, 10), sim.log.slice(10, 25), sim.log.slice(25)]
  const merged = groups.map(g => ({ v1: Y.mergeUpdates(g.map(u => u.v1)), v2: Y.mergeUpdatesV2(g.map(u => u.v2)) }))
  record('merged-updates', 'Random editing delivered as three Y.mergeUpdates batches, out of order (last batch first).', { updates: [merged[2], merged[0], merged[1]], roots: T })

  const target = new Y.Doc()
  const steps = []
  for (const g of groups) {
    const source = docFrom(sim.log.slice(0, sim.log.indexOf(g[g.length - 1]) + 1).map(u => u.v1), true)
    const sv = Y.encodeStateVector(target)
    const step = { v1: Y.encodeStateAsUpdate(source, sv), v2: Y.encodeStateAsUpdateV2(source, sv) }
    Y.applyUpdate(target, step.v1)
    steps.push(step)
  }
  record('state-vector-diffs', 'Random editing delivered as Y.encodeStateAsUpdate(doc, stateVector) diffs, as in sync step 2.', { updates: steps, roots: T })
}

function gcScenario (gc) {
  const sim = new Sim([1, 2], { gc, seed: 151 })
  sim.local(0, d => d.getText('text').insert(0, 'keep this, drop this, keep that'))
  sim.local(0, d => {
    const m = d.getMap('map')
    const sub = new Y.Map()
    m.set('sub', sub)
    sub.set('a', 'aaaaaaaaaa')
    const arr = new Y.Array()
    sub.set('arr', arr)
    arr.push(['x', 'y', 'z'])
    m.set('overwritten', 'old value')
  })
  sim.syncAll()
  sim.local(1, d => d.getText('text').delete(10, 11))
  sim.local(1, d => d.getMap('map').delete('sub'))
  sim.local(0, d => d.getMap('map').set('overwritten', 'new value'))
  sim.local(0, d => d.getText('text').insert(0, '>'))
  sim.syncAll()
  // A full-state update from a client that already collected its deleted content.
  const snapshot = { v1: Y.encodeStateAsUpdate(sim.docs[1]), v2: Y.encodeStateAsUpdateV2(sim.docs[1]) }
  sim.local(1, d => d.getText('text').insert(d.getText('text').length, '<'))
  return [snapshot, sim.log[sim.log.length - 1]]
}

record('gc-collected-content', 'Deleted text, a deleted nested map and an overwritten value, delivered as the full state of a doc that has garbage-collected them (GC structs), then one more edit.', { updates: gcScenario(true), roots: [{ name: 'text', type: 'text' }, { name: 'map', type: 'map' }] })
record('gc-disabled', 'The same edits with gc: false on every doc, so deleted content stays as ContentDeleted / kept items.', { updates: gcScenario(false), roots: [{ name: 'text', type: 'text' }, { name: 'map', type: 'map' }], gc: false })

{
  // Client ids across the whole uint32 range Yjs draws from (C# YDoc.ClientId is an int).
  const sim = new Sim([4294967295, 2147483648, 2147483647, 0], { seed: 161 })
  for (let round = 0; round < 4; round++) {
    for (let i = 0; i < 4; i++) sim.local(i, d => randomTextEdit(sim.rng, d.getText('text'), ASCII))
    sim.syncAll()
  }
  sim.local(0, d => d.getMap('map').set('who', 'max'))
  sim.local(1, d => d.getMap('map').set('who', '2^31'))
  sim.syncAll()
  record('client-ids-uint32', 'Client ids 0, 2^31-1, 2^31 and 2^32-1 editing text and racing on one map key.', { updates: sim.updates(), roots: [{ name: 'text', type: 'text' }, { name: 'map', type: 'map' }] })
}

// ---------------------------------------------------------------- adversarial interleavings
//
// The interleavings the Lean proof of Yjs' integrate (github.com/iasakura/lean-yjs) treats as
// hard cases, each with every assignment of client ids to the roles, so the client-id tie-break
// goes both ways. Each local() is one single-character insert (one update); syncFrom() fixes who
// knew what when it inserted, i.e. the origin and right origin. The observer receives the base
// text first and then the rest newest first (dependents before what they depend on), which
// drives Yjs' pending-struct path; tests/Ycs.Corpus also applies every permutation.

function permutations (xs) {
  if (xs.length <= 1) return [xs]
  return xs.flatMap((x, i) => permutations([...xs.slice(0, i), ...xs.slice(i + 1)]).map(p => [x, ...p]))
}

function adversarial (name, description, roles, script, { base = 'ab', order } = {}) {
  for (const ids of permutations([3, 5, 7, 9].slice(0, roles.length))) {
    const sim = new Sim([1, ...ids], { seed: 1 })
    if (base) {
      sim.local(0, d => d.getText('text').insert(0, base))
      sim.syncAll()
    }
    const r = Object.fromEntries(roles.map((role, k) => [role, k + 1]))
    const ins = (role, at, ch) => sim.local(r[role], d => d.getText('text').insert(at, ch))
    script({ sim, r, ins })
    const ks = sim.log.map((_, k) => k)
    const delivery = order ? order(ks) : base ? [0, ...ks.slice(1).reverse()] : ks.slice().reverse()
    const tag = roles.map((role, k) => `${role}${ids[k]}`).join('-')
    const who = roles.map((role, k) => `${role}=${ids[k]}`).join(', ')
    record(`adversarial-${name}-${tag}`, `${description} Client ids ${who}${base ? `; base text '${base}' by client 1` : ''}.`, { updates: sim.updates(delivery), roots: T })
  }
}

// The scenario behind the YATA-pseudocode discrepancy (discuss.yjs.dev/t/3875): a and b are
// inserted concurrently into the empty text (both origin ⊥); c is inserted after b by a client
// that has only b. Delivered b, c, a, the published pseudocode keeps scanning past b and puts a
// after c ("bca" vs "abc"); Yjs' integrate (and Lean's findIntegratedIndex) does not.
adversarial('yata-pseudocode', 'a and b concurrently into the empty text, c after b by a client that has only b; delivered b, c, a.',
  ['a', 'b', 'c'], ({ sim, r, ins }) => {
    ins('a', 0, 'a')
    ins('b', 0, 'b')
    sim.syncFrom(r.c, r.b)
    ins('c', 1, 'c')
  }, { base: '', order: ([a, b, c]) => [b, c, a] })

// Same origin, different right origins: X and Y both have origin 'a'; Y's right origin is C
// (Y was inserted before C), X's is 'b'. Lean: ConflictLt.ltOriginSame needs each before the
// other's right origin; findIntegratedIndex's "pending same origin" branch (step_pending_same).
adversarial('same-origin-different-right', "C after 'a'; Y between 'a' and C by a client that has C; X after 'a', concurrently.",
  ['C', 'Y', 'X'], ({ sim, r, ins }) => {
    ins('C', 1, 'C')
    sim.syncFrom(r.Y, r.C)
    ins('Y', 1, 'Y')
    ins('X', 1, 'X')
  })

// An item inserted between an item and its origin's successors: Q's origin is P, R competes with
// P for the slot after 'a'. Lean: ConflictLt.ltOriginDiff, the "advance other" / "pending
// other" branches (o.origin ∈ before, ∉ / ∈ conflicts).
adversarial('between-origin-and-successor', "P after 'a'; Q right after P by a client that has P; R after 'a', concurrently.",
  ['P', 'Q', 'R'], ({ sim, r, ins }) => {
    ins('P', 1, 'P')
    sim.syncFrom(r.Q, r.P)
    ins('Q', 2, 'Q')
    ins('R', 1, 'R')
  })

// A right origin pointing left of a competitor: Q sits between 'a' and P (right origin P), R
// competes with both for the slot after 'a' and has right origin 'b'.
adversarial('right-origin-left-of-competitor', "P after 'a'; Q between 'a' and P by a client that has P; R after 'a', concurrently.",
  ['P', 'Q', 'R'], ({ sim, r, ins }) => {
    ins('P', 1, 'P')
    sim.syncFrom(r.Q, r.P)
    ins('Q', 1, 'Q')
    ins('R', 1, 'R')
  })

// The scan in scanning mode (Lean's ghost flag `scanning`, IntegrationInvariant.scannedRegion):
// S and T form a pending region the new item N has to look past; T's origin S is in
// `conflictingItems`, so T neither advances nor breaks.
adversarial('scan-pending-region', "P after 'a'; S between 'a' and P (has P); T right after S (has S); N after 'a', concurrently.",
  ['P', 'S', 'T', 'N'], ({ sim, r, ins }) => {
    ins('P', 1, 'P')
    sim.syncFrom(r.S, r.P)
    ins('S', 1, 'S')
    sim.syncFrom(r.T, r.S)
    ins('T', 2, 'T')
    ins('N', 1, 'N')
  })

// Chains of concurrent inserts from four clients: A, then B after A (has A), then C between 'a'
// and A (has B); concurrently D after 'a' and d right after D.
adversarial('chains', "A after 'a'; B after A (has A); C between 'a' and A (has B); concurrently D after 'a' and d after D.",
  ['A', 'B', 'C', 'D'], ({ sim, r, ins }) => {
    ins('A', 1, 'A')
    sim.syncFrom(r.B, r.A)
    ins('B', 2, 'B')
    sim.syncFrom(r.C, r.B)
    ins('C', 1, 'C')
    ins('D', 1, 'D')
    ins('D', 2, 'd')
  })

// Tombstones in the scan: as between-origin-and-successor, but Q deletes P (its origin) after
// inserting, and R inserts two characters; the integration must look at deleted items too.
adversarial('deleted-competitor', "P after 'a'; Q after P, then Q deletes P; R after 'a' twice, concurrently.",
  ['P', 'Q', 'R'], ({ sim, r, ins }) => {
    ins('P', 1, 'P')
    sim.syncFrom(r.Q, r.P)
    ins('Q', 2, 'Q')
    sim.local(r.Q, d => d.getText('text').delete(1, 1))
    ins('R', 1, 'R')
    ins('R', 1, 'r')
  })

// ---------------------------------------------------------------- write

mkdirSync(outDir, { recursive: true })
for (const f of readdirSync(outDir)) if (f.endsWith('.json')) rmSync(join(outDir, f))
let total = 0
for (const c of cases) {
  const json = JSON.stringify(c, null, 1) + '\n'
  total += json.length
  writeFileSync(join(outDir, `${c.name}.json`), json)
}
console.log(`${cases.length} cases, ${(total / 1024).toFixed(0)} KiB, yjs ${yjsVersion}`)
