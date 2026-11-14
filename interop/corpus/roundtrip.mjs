// Feeds what Ycs encoded (written by tests/Ycs.Corpus RoundTripExport) back into real Yjs and
// checks Yjs ends up in the state the corpus expects. Exits non-zero on any mismatch.
// Usage: node roundtrip.mjs <export dir>      (or ./roundtrip.sh, which also produces the export)
import * as Y from 'yjs'
import { readdirSync, readFileSync } from 'node:fs'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import { deleteSetOf, docFrom, stateOf, stateVectorOf } from './lib.mjs'

const here = dirname(fileURLToPath(import.meta.url))
const exportDir = process.argv[2]
if (!exportDir) throw new Error('usage: node roundtrip.mjs <export dir>')

const bytes = s => new Uint8Array(Buffer.from(s, 'base64'))
const read = p => JSON.parse(readFileSync(p, 'utf8'))
let failures = 0
let checks = 0
let identical = 0
let compared = 0

function check (what, actual, expected) {
  checks++
  const a = JSON.stringify(actual)
  const e = JSON.stringify(expected)
  if (a !== e) {
    failures++
    console.log(`FAIL ${what}\n  Yjs read: ${a.slice(0, 400)}\n  expected: ${e.slice(0, 400)}`)
  }
}

function sameBytes (a, b) {
  compared++
  if (a === b) identical++
}

const files = readdirSync(exportDir).filter(f => f.endsWith('.json')).sort()
if (files.length === 0) throw new Error(`no export in ${exportDir}`)

for (const f of files) {
  const out = read(join(exportDir, f))
  if (out.case) {
    const c = read(join(here, 'cases', `${out.case}.json`))
    const expected = { content: c.expected.content, stateVector: c.expected.stateVector, deleteSet: c.expected.deleteSet }
    const observe = doc => ({ content: stateOf(doc, c.roots), stateVector: stateVectorOf(doc), deleteSet: deleteSetOf(doc) })

    check(`${c.name}: Ycs encodeStateAsUpdate (v1)`, observe(docFrom([bytes(out.stateV1)], c.gc)), expected)
    check(`${c.name}: Ycs encodeStateAsUpdateV2`, observe(docFrom([bytes(out.stateV2)], c.gc, true)), expected)
    check(`${c.name}: Ycs mergeUpdates (v1)`, observe(docFrom([bytes(out.mergedV1)], c.gc)), expected)
    check(`${c.name}: Ycs mergeUpdatesV2`, observe(docFrom([bytes(out.mergedV2)], c.gc, true)), expected)
    check(`${c.name}: Ycs state vector`, Object.fromEntries([...Y.decodeStateVector(bytes(out.stateVector))].sort((a, b) => a[0] - b[0]).map(([k, v]) => [String(k), v])), c.expected.stateVector)
    for (const [key, v2] of [['diffV1', false], ['diffV2', true]]) {
      const prefix = docFrom(c.updates.slice(0, c.diff.prefix).map(u => bytes(v2 ? u.v2 : u.v1)), c.gc, v2)
      ;(v2 ? Y.applyUpdateV2 : Y.applyUpdate)(prefix, bytes(out[key]))
      check(`${c.name}: prefix + Ycs ${key} for its state vector`, observe(prefix), expected)
    }
    sameBytes(out.stateV1, c.expected.stateV1)
    sameBytes(out.stateV2, c.expected.stateV2)
    sameBytes(out.mergedV1, c.merged.v1)
    sameBytes(out.mergedV2, c.merged.v2)
  } else {
    // Updates Ycs authored: Yjs must reach the text every Ycs doc converged to, in any order.
    const updates = out.updates.map(bytes)
    check(`authored seed ${out.seed}: in order`, docFrom(updates, true).getText('text').toString(), out.text)
    check(`authored seed ${out.seed}: reversed`, docFrom(updates.slice().reverse(), true).getText('text').toString(), out.text)
    check(`authored seed ${out.seed}: Y.mergeUpdates`, docFrom([Y.mergeUpdates(updates)], true).getText('text').toString(), out.text)
  }
}

console.log(`${checks - failures}/${checks} round-trip checks passed; ${identical}/${compared} Ycs encodings byte-identical to Yjs.`)
process.exit(failures === 0 ? 0 : 1)
