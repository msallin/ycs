// The Yjs side of the properties of tests/Ycs.Properties/Properties.cs that do more than run a script
// (merge-partition, idempotence): runs a script against real Yjs, then the property's law with the
// same random choices as Properties.cs (its Rng, seeded by the SHA-256 of the script's canonical text),
// and prints the first violation or "holds". Used to decide whether a violation the property finds in
// Ycs is one Yjs shares (docs/testing.md, "Properties that Yjs breaks").
//
//   node laws.mjs <property> < script.json
import * as Y from 'yjs'
import { createHash } from 'node:crypto'
import { readFileSync } from 'node:fs'
import { Run, Client, canon } from './oracle.mjs'

const MASK = (1n << 64n) - 1n

/** Properties.cs' Rng (splitmix64). */
class Rng {
  constructor (seed) { this.state = seed & MASK }
  next () {
    this.state = (this.state + 0x9E3779B97F4A7C15n) & MASK
    let z = this.state
    z = ((z ^ (z >> 30n)) * 0xBF58476D1CE4E5B9n) & MASK
    z = ((z ^ (z >> 27n)) * 0x94D049BB133111EBn) & MASK
    return z ^ (z >> 31n)
  }

  int (n) { return n <= 0 ? 0 : Number(this.next() % BigInt(n)) }
  shuffle (items) {
    for (let i = items.length - 1; i > 0; i--) {
      const j = this.int(i + 1);
      [items[i], items[j]] = [items[j], items[i]]
    }
  }
}

const rngFor = text => new Rng(createHash('sha256').update(text, 'utf8').digest().readBigUInt64LE(0))

function execute (script) {
  const run = new Run(script)
  let errors = false
  for (const o of run.execute()) {
    if (o.err === true || (Array.isArray(o.r) && o.r.some(r => r !== null && typeof r === 'object' && r.err === true))) errors = true
  }
  return { run, errors }
}

const updates = run => run.clients.flatMap(c => c.log.map((u, k) => ({ client: c.index, k, v1: u.v1, v2: u.v2 })))
const fresh = run => new Client(run, run.clients.length, { id: 999999999, gc: true })
const content = c => canon(c.state(false).roots)
const sv = c => canon(c.state(false).sv)
// As Properties.cs' WithoutAttributes: no attributes (the texts of a delta's inserts joined), no toJSON.
const withoutAttributes = v => {
  if (Array.isArray(v)) {
    const joined = []
    for (const e of v.map(withoutAttributes)) {
      const last = joined[joined.length - 1]
      const plain = o => o !== null && typeof o === 'object' && !Array.isArray(o) && Object.keys(o).length === 1 && typeof o.insert === 'string'
      if (plain(e) && plain(last)) last.insert += e.insert
      else joined.push(e)
    }
    return joined
  }
  return v !== null && typeof v === 'object'
    ? Object.fromEntries(Object.entries(v).filter(([k]) => k !== 'attributes' && k !== 'j').map(([k, x]) => [k, withoutAttributes(x)]))
    : v
}
const apply = (c, u, v2) => v2 ? Y.applyUpdateV2(c.doc, u) : Y.applyUpdate(c.doc, u)

const laws = {
  'merge-partition' (script, text) {
    const { run, errors } = execute(script)
    if (errors) return null
    const rng = rngFor(text)
    for (const v2 of [false, true]) {
      const list = updates(run).map(u => v2 ? u.v2 : u.v1)
        .concat(run.clients.map(c => v2 ? Y.encodeStateAsUpdateV2(c.doc) : Y.encodeStateAsUpdate(c.doc)))
      rng.shuffle(list)
      let cleanedUp = false
      const watched = () => {
        const c = fresh(run)
        c.doc.on('update', (_u, _o, _d, tr) => { cleanedUp ||= tr.local })
        return c
      }
      const reference = watched()
      for (const u of list) apply(reference, u, v2)
      const groups = new Map()
      for (const u of list) {
        const g = rng.int(4)
        if (!groups.has(g)) groups.set(g, [])
        groups.get(g).push(u)
      }
      const merge = us => v2 ? Y.mergeUpdatesV2(us) : Y.mergeUpdates(us)
      const merged = [...groups.values()].map(merge)
      const fromAll = watched()
      apply(fromAll, merge(merged), v2)
      const fromParts = watched()
      for (const m of merged) apply(fromParts, m, v2)
      // As Properties.cs: after a formatting cleanup, the content without attributes.
      const seen = c => cleanedUp ? canon(withoutAttributes(c.state(false).roots)) : content(c)
      const f = v2 ? 'v2' : 'v1'
      if (seen(reference) !== seen(fromAll)) return `merged all ${f}: ${seen(reference)} vs ${seen(fromAll)}`
      if (seen(reference) !== seen(fromParts)) return `merged parts ${f}: ${seen(reference)} vs ${seen(fromParts)}`
      if (sv(reference) !== sv(fromAll)) return `sv ${f}`
    }
    return null
  },

  idempotence (script) {
    const { run, errors } = execute(script)
    if (errors) return null
    const all = updates(run)
    for (const c of run.clients) {
      const before = [content(c), sv(c), Buffer.from(Y.encodeStateAsUpdate(c.doc)).toString('base64'), c.log.length]
      const state = Y.decodeStateVector(Y.encodeStateVector(c.doc))
      // The updates the document already has (Replica.Contains: every struct's end within its state vector).
      for (const u of all.filter(u => Y.decodeUpdate(u.v1).structs.every(s => s.id.clock + s.length <= (state.get(s.id.client) ?? 0)))) {
        Y.applyUpdate(c.doc, u.v1)
        Y.applyUpdateV2(c.doc, u.v2)
      }
      const after = [content(c), sv(c), Buffer.from(Y.encodeStateAsUpdate(c.doc)).toString('base64'), c.log.length]
      const kinds = ['content', 'sv', 'bytes', 'local updates']
      for (let i = 0; i < 4; i++) {
        if (before[i] !== after[i]) return `${kinds[i]}: client ${c.index}: ${before[i]} vs ${after[i]}`
      }
    }
    return null
  },
}

const text = readFileSync(0, 'utf8').trim()
const law = laws[process.argv[2]]
if (law === undefined) throw new Error(`no law ${process.argv[2]}; one of ${Object.keys(laws).join(', ')}`)
console.log(law(JSON.parse(text), text) ?? 'holds')
