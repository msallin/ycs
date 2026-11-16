// Updates that Yjs 13.6.33 leaves pending although the set is complete (see README.md). Writes the fixtures
// tests/Ycs.Tests/PendingRetryParityTests.cs replays:
//
//   node gen.mjs            # writes ../../tests/Ycs.Tests/Fixtures/pending-retry.json and pending-retry-random.json.gz
//
// and prints the counts of the random cases for Yjs 13 and Yjs 14. YJS13= and YJS14= name other builds (a patched
// dist/yjs.mjs, a patched src/index.js of @y/y) to count against; then no fixture is written.
const Y = await import(process.env.YJS13 ?? 'yjs')
const Y14 = await import(process.env.YJS14 ?? 'y14')
const write = !process.env.YJS13 && !process.env.YJS14
import { writeFileSync } from 'node:fs'
import { gzipSync } from 'node:zlib'

const fixtures = new URL('../../tests/Ycs.Tests/Fixtures/', import.meta.url)
const b64 = u => Buffer.from(u).toString('base64')
const unb64 = s => new Uint8Array(Buffer.from(s, 'base64'))

// What a document of Yjs 13 reports after the updates were applied in `order`.
export const observe = (updates, order) => {
  const d = new Y.Doc()
  for (const i of order) Y.applyUpdate(d, updates[i])
  return report(d)
}
const report = d => ({
  pendingStructs: d.store.pendingStructs !== null,
  pendingDs: d.store.pendingDs !== null,
  pendingStructsUpdate: d.store.pendingStructs ? b64(d.store.pendingStructs.update) : null,
  stateVector: b64(Y.encodeStateVector(d)),
  stateV2: b64(Y.encodeStateAsUpdateV2(d)),
  text: d.getText('text').toString(),
  array: JSON.stringify(d.getArray('array').toJSON()),
  map: JSON.stringify(d.getMap('map').toJSON()),
})
const stuck14 = (updates, order) => {
  const d = new Y14.Doc()
  for (const i of order) Y14.applyUpdate(d, updates[i])
  return d.store.pendingStructs !== null
}

// What happens to a stuck document when more arrives: an empty update, an update of a client not in the set, the
// merged pending update applied back, and one of the stuck updates sent again (as a sync step 2 after a reconnect would).
const healing = (updates, order, resend) => {
  const after = extra => {
    const d = new Y.Doc()
    for (const i of order) Y.applyUpdate(d, updates[i])
    for (const u of extra(d)) Y.applyUpdate(d, u)
    return report(d)
  }
  const other = new Y.Doc(); other.clientID = 999; other.getText('unrelated').insert(0, 'u')
  return {
    empty: after(() => [Y.encodeStateAsUpdate(new Y.Doc())]),
    unrelated: after(() => [Y.encodeStateAsUpdate(other)]),
    pendingUpdateApplied: after(d => [Y.convertUpdateFormatV2ToV1(d.store.pendingStructs.update)]),
    resent: after(() => [updates[resend]]),
  }
}

// The convergence driver's 7 updates of 4 clients. Update 6 also deletes 1158237430:1, a struct of a client none of
// the 7 carries: that delete stays in pendingDs in every order.
const repro = ['AQGC34r0DgGE1ZKNWQABdQA=', 'AQL+lNwkBCcBA21hcAJrMAEoAP6U3CQEAXYBfacGAA==', 'AQPfkaCADACI/pTcJAIBfZQJh9+RoIAMAAEoAN+RoIAMAQF2AXgA', 'AQHVko1ZAAQBBHRleHQBagA=', 'AQGC34r0DgAoAQNtYXACazEBfgA=', 'AQH+lNwkAAgBBWFycmF5A3UCfQJ3BHRyYW12AgFrfQIBc3cEc2dhdX27AQA=', 'AQH+lNwkAygA35GggAwBAmszAX2WAwH2maWoBAEBAQ=='].map(unb64)

// The smallest case: client 1 types into the array, client 2 inserts a map after 1:1, client 1 sets a key in that map
// (1:2, parent 2:0) and then types into the text (1:3). Client 2's id must be the higher: Yjs integrates the higher
// client first, so on the retry 2:0 walks to 1:3 and records 1:3's need (1:2) instead of its own (1:1).
const minimal = (() => {
  const m = new Y.Doc(); m.clientID = 1
  const c = new Y.Doc(); c.clientID = 2
  const out = []
  const record = (d, f) => { const before = Y.encodeStateVector(d); f(); out.push(Y.encodeStateAsUpdate(d, before)) }
  const sync = (to, from) => Y.applyUpdate(to, Y.encodeStateAsUpdate(from, Y.encodeStateVector(to)))
  record(m, () => m.getArray('array').insert(0, ['a'])) // a1 = 1:0
  record(m, () => m.getArray('array').insert(1, ['b'])) // a2 = 1:1
  sync(c, m)
  record(c, () => c.getArray('array').insert(2, [new Y.Map()])) // b = 2:0, origin 1:1
  sync(m, c)
  record(m, () => m.getArray('array').get(2).set('k', 1)) // c = 1:2, parent 2:0
  record(m, () => m.getText('text').insert(0, 'x')) // d = 1:3
  return out
})()

const scenario = (updates, stuckOrder, creationOrder, resend) => ({
  updates: updates.map(b64),
  stuckOrder,
  creationOrder,
  stuck: observe(updates, stuckOrder),
  creation: observe(updates, creationOrder),
  healing: healing(updates, stuckOrder, resend),
  yjs14Stuck: stuck14(updates, stuckOrder),
})

// Seeded concurrent sessions, each local transaction one update, now and then one document catching up with another.
// broad: 3 to 5 documents edit the text, the array (values, nested maps and arrays) and the map at random places.
// narrow: 2 or 3 documents append to the array (values and maps), set keys in its last map and type into the text;
// six updates. This is the shape of the minimal case, and the only one of the two where Yjs 13 sticks at all.
let seed = 20261116
const rnd = n => { seed = (Math.imul(seed, 1103515245) + 12345) >>> 0; return (seed >>> 8) % n }
const shuffle = xs => { const a = [...xs]; for (let i = a.length - 1; i > 0; i--) { const j = rnd(i + 1); [a[i], a[j]] = [a[j], a[i]] } return a }
const session = kind => {
  const ids = new Set()
  while (ids.size < (kind === 'broad' ? 3 + rnd(3) : 2 + rnd(2))) ids.add(1 + rnd(kind === 'broad' ? 0xfffffff : 1000))
  const docs = [...ids].map(id => { const d = new Y.Doc(); d.clientID = id; return d })
  const updates = []
  for (const d of docs) d.on('update', (u, origin) => { if (origin !== 'sync') updates.push(u) })
  const steps = 8 + rnd(20)
  for (let s = 0; kind === 'broad' ? s < steps || updates.length < 2 : updates.length < 6; s++) {
    const d = docs[rnd(docs.length)]
    if (rnd(kind === 'broad' ? 3 : 2) === 0) {
      const from = docs[rnd(docs.length)]
      if (from !== d) Y.applyUpdate(d, Y.encodeStateAsUpdate(from, Y.encodeStateVector(d)), 'sync')
      continue
    }
    const text = d.getText('text'), array = d.getArray('array'), map = d.getMap('map')
    if (kind === 'narrow') {
      const maps = array.toArray().filter(x => x instanceof Y.Map)
      const op = rnd(3)
      if (op === 0) array.push([rnd(2) ? rnd(1000) : new Y.Map()])
      else if (op === 1) text.insert(0, 'x')
      else if (maps.length > 0) maps[maps.length - 1].set('k' + rnd(3), rnd(1000))
      else array.push([rnd(1000)])
      continue
    }
    const inner = array.toArray().filter(x => x instanceof Y.Map || x instanceof Y.Array)
    switch (rnd(7)) {
      case 0: text.insert(rnd(text.length + 1), 'abcdef'.slice(0, 1 + rnd(4))); break
      case 1: if (text.length > 0) { const i = rnd(text.length); text.delete(i, 1 + rnd(Math.min(3, text.length - i))) } break
      case 2: array.insert(rnd(array.length + 1), [rnd(2) ? rnd(1000) : rnd(2) ? new Y.Map() : new Y.Array()]); break
      case 3: if (array.length > 0) array.delete(rnd(array.length), 1); break
      case 4: map.set('k' + rnd(4), rnd(2) ? rnd(1000) : new Y.Map()); break
      default: {
        const t = inner.length > 0 ? inner[rnd(inner.length)] : null
        if (t instanceof Y.Map) t.set('k' + rnd(3), rnd(1000))
        else if (t instanceof Y.Array) t.insert(rnd(t.length + 1), [rnd(1000)])
        else d.transact(() => { array.push([rnd(1000)]); text.insert(0, 'z') })
      }
    }
  }
  return updates
}

// Every run of a session: the order, whether Yjs 13 left structs or deletions pending, and the state it encodes
// (pending included) when that differs from the creation order's (stuck, or other struct boundaries).
const random = []
const counts = {}
for (const [kind, sessions, ordersPerSession] of [['broad', 300, 5], ['narrow', 3000, 20]]) {
  const c = counts[kind] = { sessions, runs: 0, yjs13Stuck: 0, yjs13StuckSessions: 0, yjs14Stuck: 0, yjs14StuckSessions: 0, both: 0 }
  for (let k = 0; k < sessions; k++) {
    const updates = session(kind)
    const creation = updates.map((_, i) => i)
    const full = observe(updates, creation)
    if (full.pendingStructs || full.pendingDs || stuck14(updates, creation)) throw new Error('the creation order left something pending')
    let any13 = false, any14 = false
    const runs = [creation, ...Array.from({ length: ordersPerSession - 1 }, () => shuffle(creation))].map(order => {
      const r = observe(updates, order)
      const s13 = r.pendingStructs || r.pendingDs, s14 = stuck14(updates, order)
      c.runs++
      if (s13) { c.yjs13Stuck++; any13 = true }
      if (s14) { c.yjs14Stuck++; any14 = true }
      if (s13 && s14) c.both++
      const run = { order: order.join(','), stuck: s13 }
      if (r.stateV2 !== full.stateV2) run.stateV2 = r.stateV2
      return run
    })
    if (any13) c.yjs13StuckSessions++
    if (any14) c.yjs14StuckSessions++
    // The fixture keeps every broad session, the first 200 narrow ones and every one where either version stuck.
    if (kind === 'broad' || k < 200 || any13 || any14) random.push({ kind, updates: updates.map(b64), stateV2: full.stateV2, runs })
  }
}

if (write) writeFileSync(new URL('pending-retry.json', fixtures), JSON.stringify({
  yjs: '13.6.33',
  repro: scenario(repro, [0, 1, 2, 3, 4, 5, 6], [3, 4, 0, 5, 2, 6, 1], 2),
  minimal: scenario(minimal, [4, 2, 0, 1, 3], [0, 1, 2, 3, 4], 2),
}, null, 1) + '\n')
if (write) writeFileSync(new URL('pending-retry-random.json.gz', fixtures), gzipSync(JSON.stringify(random), { level: 9 }))
console.log(JSON.stringify(counts))
