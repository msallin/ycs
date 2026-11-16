// Sets of 3, 4 and 5 updates of one client's history that all wait for a struct that never arrives (1:0), applied in
// every order to a document of real Yjs 13.6.32, which merges every remainder into what waits
// (pending.update = mergeUpdatesV2([pending.update, rest])). Where the updates overlap by clock that merge is not
// associative, so the state the document encodes depends on the order. tests/Ycs.Tests/UpdateOperationsTests.cs
// replays the fixture, v1 and v2.
//
//   node pending-orders.mjs | gzip -9 > ../../tests/Ycs.Tests/Fixtures/pending-orders.json.gz
const Y = await import('yjs13')
const hex = u => Buffer.from(u).toString('hex')
const unhex = s => new Uint8Array(Buffer.from(s, 'hex'))
let seed = 12345
const rnd = n => { seed = (Math.imul(seed, 1103515245) + 12345) >>> 0; return (seed >>> 8) % n }

// Client 1 nests a text in an array and types n characters into it (clocks 1..n). Variants: the text dropped (with gc
// the characters become GC structs, without they are deleted items), a few characters deleted, characters typed
// backwards from a point (a history no replica of client 1 could share with the others: other origins at one clock).
const nested = (n, variant) => {
  const d = new Y.Doc({ gc: variant.gc }); d.clientID = 1; const a = d.getArray('a')
  const t = new Y.Text(); a.insert(0, [t])
  for (let i = 0; i < n; i++) t.insert(variant.backFrom !== undefined && i >= variant.backFrom ? variant.backFrom : i, 'x')
  if (variant.del) t.delete(Math.min(variant.del[0], t.length - 1), Math.min(variant.del[1], t.length - Math.min(variant.del[0], t.length - 1)))
  if (variant.drop) a.delete(0, 1)
  return d
}
// The state vector of client 1 at `clock`: the update of a history from there on waits for 1:0 .. 1:clock-1.
const below = clock => { const d = new Y.Doc(); d.clientID = 1; d.getArray('z').insert(0, Array(clock).fill(0)); return Y.encodeStateVector(d) }
const permutations = xs => xs.length <= 1 ? [xs] : xs.flatMap((x, i) => permutations([...xs.slice(0, i), ...xs.slice(i + 1)]).map(p => [x, ...p]))

const randomUpdate = kind => {
  const n = 1 + rnd(7)
  const variant = { gc: kind !== 'items' && rnd(3) !== 0, drop: rnd(2) === 0 }
  if (rnd(3) === 0) variant.del = [rnd(n), 1 + rnd(3)]
  if (kind === 'conflicting' && rnd(4) === 0) variant.backFrom = rnd(n)
  return Y.encodeStateAsUpdate(nested(n, variant), below(1 + rnd(n)))
}
// Plain characters 1:from .. 1:n, no deletion, no GC struct.
const plain = (from, n) => Y.encodeStateAsUpdate(nested(n, { gc: false }), below(from))

const sets = [
  // The review's case: 1:2-6, then 1:1, then 1:1-4; Yjs keeps 1:1 and 1:2-6, one merge of all three splits 1:2-6.
  { name: 'review', updates: [unhex('0101010284010105787878787800'), unhex('0101010104000100017800'), unhex('010101010400010004787878780101010005')] },
  { name: 'disjoint', updates: [plain(1, 2), plain(7, 8), plain(4, 5)] },
  { name: 'touching', updates: [plain(1, 2), plain(3, 4), plain(5, 6)] },
  { name: 'split-differently', updates: [plain(3, 4), plain(1, 2), plain(2, 4)] },
  { name: 'staggered', updates: [plain(1, 4), plain(2, 6), plain(3, 8), plain(5, 9)] },
]
// Random sets: items only (no GC struct), with GC structs, conflicting histories, and with GC structs where some
// updates are two merged by Yjs (a Skip struct where they leave a gap); 3, 4 or 5 updates each.
const update = kind => kind === 'skips' && rnd(2) === 0 ? Y.mergeUpdates([randomUpdate('gc'), randomUpdate('gc')]) : randomUpdate(kind)
for (const [kind, count] of [['items', 10], ['gc', 10], ['conflicting', 5], ['skips', 5]]) {
  for (let k = 0; k < count; k++) {
    const size = k % 5 === 4 ? 5 : k % 2 === 0 ? 3 : 4
    sets.push({ name: `${kind}-${k}`, updates: Array.from({ length: size }, () => update(kind)) })
  }
}

const out = []
for (const { name, updates } of sets) {
  const orders = []
  for (const order of permutations(updates.map((_, i) => i))) {
    const v1 = order.map(i => updates[i])
    const p1 = new Y.Doc({ gc: false }); p1.clientID = 9
    v1.forEach(u => Y.applyUpdate(p1, u))
    const p2 = new Y.Doc({ gc: false }); p2.clientID = 9
    v1.forEach(u => Y.applyUpdateV2(p2, Y.convertUpdateFormatV1ToV2(u)))
    orders.push({ order: order.join(''), pending: hex(Y.encodeStateAsUpdate(p1)), pendingV2: hex(Y.encodeStateAsUpdateV2(p2)) })
  }
  out.push({ name, updates: updates.map(hex), orders })
}
console.log(JSON.stringify({ yjs: '13.6.32', sets: out }))
