// Scripts in which Yjs' search markers are stale when an index edit looks them up, run in real Yjs 13.6.32: what
// Yjs observed after every step (the content, an error) and the final state (v1) are written with each script, one
// JSON object per line. tests/Ycs.Tests/StaleMarkerScriptTests.cs replays them on Ycs.
//
//   node gen.mjs <first seed> <count> <mode> > scripts.jsonl
//
// Modes:
//   apply     an update applied inside a local transaction, between index edits (Yjs leaves the markers stale:
//             the transaction is local, so no observer call clears them)
//   small     the same, with fewer steps (most scripts are near the minimal case)
//   observer  an observer of a map edits the list during the cleanup of a remote transaction, before the list's own
//             observer call clears its markers
//
// One document (client 1 or 3) and a peer (client 2); a text (70%) or an array, observed in 40% of the scripts
// (Ycs extends runs only in unobserved types). The steps: ins/del [who, index, value or length], txn [ops], sync
// (to the peer), apply (inside a txn: the peer's diff), applyobs [ops the map observer made], pm (the peer sets a
// map key), cid (the document's client id becomes 7, so that later edits do not continue its runs).
import * as Y from 'yjs'

const first = Number(process.argv[2] || 1)
const count = Number(process.argv[3] || 100)
const mode = process.argv[4] || 'apply'
const small = mode === 'small'
const hex = b => Buffer.from(b).toString('hex').toUpperCase()

// xorshift on BigInt, without reducing the first state to 64 bits (as the scripts were first generated).
function rngOf (seed) {
  let s = BigInt(seed) * 0x9E3779B97F4A7C15n + 0x1234567n
  const M = (1n << 64n) - 1n
  const next = () => { s ^= (s << 13n) & M; s ^= s >> 7n; s ^= (s << 17n) & M; return s }
  next(); next()
  return { int: n => n <= 0 ? 0 : Number(next() % BigInt(n)), chance: p => Number(next() % 10000n) < p * 10000 }
}

for (let seed = first; seed < first + count; seed++) {
  const r = rngOf(seed)
  const doc = new Y.Doc(); doc.clientID = r.chance(0.5) ? 1 : 3; const client0 = doc.clientID
  const peer = new Y.Doc(); peer.clientID = 2
  const kind = r.chance(0.7) ? 'text' : 'array'
  const obs = r.chance(0.4)
  const T = d => kind === 'text' ? d.getText('t') : d.getArray('t')
  const t = T(doc), pt = T(peer)
  if (obs) t.observe(() => {})
  const steps = []
  let armed = null
  if (mode === 'observer') doc.getMap('m').observe(() => { if (armed) { const ops = armed; armed = null; const k = 1 + r.int(4); for (let j = 0; j < k; j++) { const s = localOp(0); ops.push(s); run(s) } } })
  let n = 0
  const val = () => kind === 'text' ? String.fromCharCode(97 + (n++ % 26)) : n++
  const ins = (ty, at, v) => kind === 'text' ? ty.insert(at, v) : ty.insert(at, [v])
  const content = ty => kind === 'text' ? ty.toString() : JSON.stringify(ty.toArray())
  const syncToPeer = () => Y.applyUpdate(peer, Y.encodeStateAsUpdate(doc, Y.encodeStateVector(peer)))
  const localOp = (who) => {
    const ty = who === 0 ? t : pt
    const len = ty.length
    const x = r.int(10)
    let step
    if (x < 3 && len > 0) { const at = r.int(len); const l = Math.min(1 + r.int(2), len - at); step = ['del', who, at, l] } else if (x < 7) { step = ['ins', who, r.int(len + 1), val()] } else { step = ['ins', who, len, val()] }
    return step
  }
  const run = (step) => {
    const ty = step[1] === 0 ? t : pt
    if (step[0] === 'ins') ins(ty, step[2], step[3]); else ty.delete(step[2], step[3])
  }
  const out = []
  const top = (step, f) => { steps.push(step); let err = null; try { f() } catch (e) { err = String(e.message) } out.push({ c: content(t), err }) }
  // The history: edits of the document in transactions of their own and in transactions of several, syncs.
  const pre = small ? 1 + r.int(3) : 2 + r.int(12)
  for (let i = 0; i < pre; i++) {
    const x = r.int(10)
    if (x < 6) { const s = localOp(0); top(s, () => run(s)) } else if (x < 8) { const ops = []; const k = 2 + r.int(5); top(['txn', ops], () => doc.transact(() => { for (let j = 0; j < k; j++) { const s = localOp(0); ops.push(s); run(s) } })) } else { top(['sync'], syncToPeer) }
  }
  top(['sync'], syncToPeer)
  if (mode === 'observer') { peer.getMap('m').set('k', 1); steps.push(['pm']); out.push({ c: content(t), err: null }) }
  const pe = small ? 1 : 1 + r.int(3)
  for (let i = 0; i < pe; i++) { const s = localOp(1); steps.push(s); run(s); out.push({ c: content(t), err: null }) }
  if (mode === 'observer') {
    const ops = []
    top(['applyobs', ops], () => { armed = ops; Y.applyUpdate(doc, Y.encodeStateAsUpdate(peer, Y.encodeStateVector(doc))) })
  } else {
    // The transaction: local edits, the peer's update, local edits.
    const ops = []
    top(['txn', ops], () => doc.transact(() => {
      const k1 = small ? r.int(3) : r.int(6)
      for (let j = 0; j < k1; j++) { const s = localOp(0); ops.push(s); run(s) }
      ops.push(['apply']); Y.applyUpdate(doc, Y.encodeStateAsUpdate(peer, Y.encodeStateVector(doc)))
      const k2 = small ? 1 + r.int(2) : 1 + r.int(6)
      for (let j = 0; j < k2; j++) { const s = localOp(0); ops.push(s); run(s) }
    }))
  }
  doc.clientID = 7; steps.push(['cid']); out.push({ c: content(t), err: null })
  const post = small ? 0 : r.int(4)
  for (let i = 0; i < post; i++) { const s = localOp(0); top(s, () => run(s)) }
  console.log(JSON.stringify({ seed, mode, client: client0, kind, obs, steps, out, v1: hex(Y.encodeStateAsUpdate(doc)) }))
}
