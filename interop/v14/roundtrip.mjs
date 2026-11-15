// Feeds what Ycs answered to the v14 documents (written by tests/Ycs.Tests V14InteropTests.Export_WhenRequested_
// WritesYcsAnswers) back into Yjs v14 and checks that v14 applies it and converges with Ycs: Ycs' diff for v14's
// state vector, its whole state (v1, v2) and its edits one by one, from a Ycs client id of 53 bits and one just
// above 32. Exits non-zero on any mismatch.
// Usage: node roundtrip.mjs <export dir>      (or ./roundtrip.sh, which also produces the export)
import * as Y from '@y/y'
import { readdirSync, readFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { b64, bytes, view13, stateVector } from './lib.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const exportDir = process.argv[2]
if (!exportDir) throw new Error('usage: node roundtrip.mjs <export dir>')

const fixture = JSON.parse(readFileSync(join(here, '..', '..', 'tests', 'Ycs.Tests', 'Fixtures', 'v14-interop.json'), 'utf8'))
const cases = Object.fromEntries(fixture.cases.map(c => [c.name, c]))

// Keys sorted at every level, so that Ycs' view and lib.mjs' compare as values.
const canonical = v => Array.isArray(v)
  ? v.map(canonical)
  : v !== null && typeof v === 'object' ? Object.fromEntries(Object.keys(v).sort().map(k => [k, canonical(v[k])])) : v

let failures = 0
let checks = 0
function check (what, actual, expected) {
  checks++
  const a = JSON.stringify(actual)
  const e = JSON.stringify(expected)
  if (a !== e) {
    failures++
    console.log(`FAIL ${what}\n  v14:      ${a.slice(0, 600)}\n  expected: ${e.slice(0, 600)}`)
  }
}

const docOf = (updates, v2 = false) => {
  const d = new Y.Doc()
  d.clientID = 1 // never a client of the cases
  for (const u of updates) (v2 ? Y.applyUpdateV2 : Y.applyUpdate)(d, bytes(u))
  return d
}

const files = readdirSync(exportDir).filter(f => f.endsWith('.json')).sort()
if (files.length === 0) throw new Error(`no export in ${exportDir}`)

for (const f of files) {
  const out = JSON.parse(readFileSync(join(exportDir, f), 'utf8'))
  const c = cases[out.case]
  const what = `${c.name}, Ycs client ${out.clientId}`
  const observe = d => ({
    view: canonical(view13(Y.encodeStateAsUpdate(d), c.roots)),
    stateVector: stateVector(Y.decodeStateVector(Y.encodeStateVector(d))),
    pending: d.store.pendingStructs !== null || d.store.pendingDs !== null
  })
  const expected = { view: canonical(JSON.parse(out.view)), stateVector: stateVector(Y.decodeStateVector(bytes(out.stateVector))), pending: false }

  // v14 with its concurrent change takes Ycs' answer to its state vector.
  const v14 = docOf([c.updateV1, c.concurrent.v1, out.diffV1])
  check(`${what}: v14 + Ycs' diff (v1)`, observe(v14), expected)
  const v14v2 = docOf([c.updateV2], true)
  Y.applyUpdate(v14v2, bytes(c.concurrent.v1))
  Y.applyUpdateV2(v14v2, bytes(out.diffV2))
  check(`${what}: v14 + Ycs' diff (v2)`, observe(v14v2), expected)

  // Ycs' whole state, and its edits one by one (before v14's change, as Ycs made them).
  check(`${what}: Ycs' state (v1)`, observe(docOf([out.stateV1])), expected)
  check(`${what}: Ycs' state (v2)`, observe(docOf([out.stateV2], true)), expected)
  check(`${what}: v14 + Ycs' edits + v14's change`, observe(docOf([c.updateV1, ...out.edits, c.concurrent.v1])), expected)

  // The 53-bit (or above-32-bit) client is Ycs', with its edits, and v14 re-encodes the converged state as Ycs does.
  check(`${what}: Ycs' client in the state vector`, Y.decodeStateVector(bytes(out.stateVector)).get(out.clientId) > 0, true)
  check(`${what}: v14's encoding of the converged state`, b64(Y.encodeStateAsUpdate(v14)), out.stateV1)
  check(`${what}: Ycs' snapshot`, Y.equalSnapshots(Y.decodeSnapshot(bytes(out.snapshotV1)), Y.snapshot(v14)), true)
}

console.log(`${failures === 0 ? 'OK' : 'FAILED'}  ${fixture.yjs} <- Ycs: ${files.length} answers, ${checks} checks, ${failures} failed`)
process.exit(failures === 0 ? 0 : 1)
