// Updates that hold a GC struct and an item for one clock range, merged and left pending in real Yjs 13.6.32, in
// every order: which struct survives is decided by the order of V8's sort (interop/js/v8-sort.mjs), and merging
// them is not associative, so pending structs (merged with each new remainder) can end unlike one merge of all.
// tests/Ycs.Tests/UpdateOperationsTests.cs replays the fixture.
//
//   node merge-conflicts.mjs > ../../tests/Ycs.Tests/Fixtures/merge-conflicts.json
const Y = await import('yjs13')
const hex = u => Buffer.from(u).toString('hex')
const unhex = s => new Uint8Array(Buffer.from(s, 'hex'))

// Client 1 nests a text in an array and types n characters into it (clocks 1..n); with drop it deletes the text,
// which (with gc) turns the characters into GC structs.
const nested = (n, drop) => {
  const d = new Y.Doc({ gc: drop }); d.clientID = 1; const a = d.getArray('a')
  const t = new Y.Text(); a.insert(0, [t])
  for (let i = 0; i < n; i++) t.insert(i, 'x')
  if (drop) a.delete(0, 1)
  return d
}
const below = clock => { const d = new Y.Doc(); d.clientID = 1; d.getArray('z').insert(0, Array(clock).fill(0)); return Y.encodeStateVector(d) }

const sets = {
  // The soak's case (deliver-v1, seed 532368): the GC struct 1:2 and the item 1:2, both waiting for 1:0 and 1:1.
  'gc-and-item': [unhex('0101bbfcffff0f02000101bbfcffff0f010003'), unhex('0201bbfcffff0f020400bbfcffff0f01016101040004010174016100')],
  // GC structs 1:1-2 and 1:3-4, and the item 1:3, all waiting for 1:0.
  'two-gcs-and-item': [Y.encodeStateAsUpdate(nested(2, true), below(1)), Y.encodeStateAsUpdate(nested(4, true), below(3)), Y.encodeStateAsUpdate(nested(3, false), below(3))],
}

const permutations = xs => xs.length <= 1 ? [xs] : xs.flatMap((x, i) => permutations([...xs.slice(0, i), ...xs.slice(i + 1)]).map(p => [x, ...p]))

const cases = []
for (const [name, updates] of Object.entries(sets)) {
  for (const order of permutations(updates.map((_, i) => i))) {
    const v1 = order.map(i => updates[i])
    const v2 = v1.map(u => Y.convertUpdateFormatV1ToV2(u))
    const pending1 = new Y.Doc({ gc: false }); pending1.clientID = 9
    v1.forEach(u => Y.applyUpdate(pending1, u))
    const pending2 = new Y.Doc({ gc: false }); pending2.clientID = 9
    v2.forEach(u => Y.applyUpdateV2(pending2, u))
    cases.push({
      name,
      order: order.join(''),
      updates: v1.map(hex),
      merge: hex(Y.mergeUpdates(v1)),
      mergeV2: hex(Y.mergeUpdatesV2(v2)),
      pending: hex(Y.encodeStateAsUpdate(pending1)),
      pendingV2: hex(Y.encodeStateAsUpdateV2(pending2)),
    })
  }
}
console.log(JSON.stringify({ yjs: '13.6.32', cases }, null, 1))
