// Random concurrent text editing on several replicas, run in real Yjs, recorded for
// tests/Ycs.Tests/TextDifferentialTests.cs. Ycs replays the same operations on its own
// replicas (same client ids, same delivery order of each replica's updates) and must end with
// Yjs' text, and re-encode Yjs' final document to the same bytes.
//
//   cd interop/differential && npm ci && node gen.mjs > ../../tests/Ycs.Tests/Fixtures/text-differential.json
//
// Deterministic: seeded PRNG, fixed client ids, no clocks.
import * as Y from 'yjs'
import { createRequire } from 'node:module'

const yjsVersion = createRequire(import.meta.url)('yjs/package.json').version

const mulberry32 = (a) => () => {
  a |= 0; a = (a + 0x6D2B79F5) | 0
  let t = Math.imul(a ^ (a >>> 15), 1 | a)
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296
}

const alphabet = ['a', 'b', 'c', 'x', 'y', 'z', ' ', '\n', 'é', '😀']

const runSeed = (seed) => {
  const rnd = mulberry32(seed)
  const int = (n) => Math.floor(rnd() * n)
  const replicas = 2 + int(3)
  const steps = 40 + int(80)
  const clientIds = Array.from({ length: replicas }, (_, i) => 1 + i * 1000 + int(1000))
  const docs = clientIds.map((id) => { const d = new Y.Doc(); d.clientID = id; d.getText('text'); return d })
  const log = [] // every update made, in order
  const received = docs.map(() => new Set()) // update indices each replica has
  const ops = []

  docs.forEach((d, r) => d.on('update', (u, origin) => {
    if (origin !== 'remote') { received[r].add(log.length); log.push(u) }
  }))

  const deliver = (to, u) => {
    ops.push({ op: 'deliver', to, update: u })
    received[to].add(u)
    Y.applyUpdate(docs[to], log[u], 'remote')
  }

  for (let s = 0; s < steps; s++) {
    const r = int(replicas)
    const text = docs[r].getText('text')
    const roll = rnd()
    if (roll < 0.45 || text.length === 0) {
      let str = ''
      const n = 1 + int(4)
      for (let k = 0; k < n; k++) str += alphabet[int(alphabet.length)]
      const index = int(text.length + 1)
      ops.push({ op: 'insert', replica: r, index, text: str })
      text.insert(index, str)
    } else if (roll < 0.7) {
      const index = int(text.length)
      const length = 1 + int(Math.min(5, text.length - index))
      ops.push({ op: 'delete', replica: r, index, length })
      text.delete(index, length)
    } else {
      // Deliver one update this replica does not have yet, chosen at random (out of order on purpose).
      const missing = log.map((_, i) => i).filter((i) => !received[r].has(i))
      if (missing.length > 0) deliver(r, missing[int(missing.length)])
    }
  }

  // Finally everyone gets everything, in random order.
  for (let r = 0; r < replicas; r++) {
    let missing = log.map((_, i) => i).filter((i) => !received[r].has(i))
    while (missing.length > 0) {
      const u = missing.splice(int(missing.length), 1)[0]
      deliver(r, u)
    }
  }

  const texts = docs.map((d) => d.getText('text').toString())
  if (new Set(texts).size !== 1) throw new Error(`Yjs replicas diverged for seed ${seed}`)
  return {
    seed,
    clientIds,
    ops,
    updates: log.length,
    text: texts[0],
    finalUpdateV1: Buffer.from(Y.encodeStateAsUpdate(docs[0])).toString('base64'),
  }
}

const cases = []
for (let seed = 1; seed <= 40; seed++) cases.push(runSeed(seed))
process.stdout.write(JSON.stringify({ yjs: yjsVersion, generator: 'interop/differential/gen.mjs', cases }) + '\n')
