// Proves Ycs' update-manipulation utilities (mergeUpdates/diffUpdate/encodeStateVectorFromUpdate/
// convertUpdateFormat - src/Ycs/Utils/UpdateOperations.cs) are wire-compatible with real Yjs
// 13.6.32: it builds updates with real Yjs, shells out to a small C# console app that runs them
// through Ycs, and checks the results against what Yjs itself would produce.
// Usage: node check-updates.mjs (invoked by interop/run.sh, which discovers js/check-*.mjs)
import { execFileSync } from 'node:child_process'
import { mkdtempSync, writeFileSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'

const Y = await import('yjs13')

const here = dirname(fileURLToPath(import.meta.url))
const repoRoot = join(here, '..', '..')
const dotnet = process.env.DOTNET || 'dotnet'
const csprojPath = join(repoRoot, 'interop', 'Ycs.Updates.Interop', 'Ycs.Updates.Interop.csproj')

const b64 = u8 => Buffer.from(u8).toString('base64')
const bytes = b64str => new Uint8Array(Buffer.from(b64str, 'base64'))
const json = v => JSON.parse(JSON.stringify(v))

// ---------------------------------------------------------------------------
// Scenario 1: merge updates from three independent docs - plain inserts, a
// map + a delete, and a doc with gc:true (so its update carries a real GC
// struct, not just deleted-but-kept content).
// ---------------------------------------------------------------------------

const doc1 = new Y.Doc({ gc: false })
doc1.getArray('array').insert(0, [1, 2, 3])

const doc2 = new Y.Doc({ gc: false })
doc2.transact(() => {
  doc2.getArray('array').insert(0, ['a', 'b', 'c', 'd'])
  doc2.getMap('map').set('key', 'value')
})
doc2.getArray('array').delete(1, 2)

const doc3 = new Y.Doc() // gc: true (default) - deleted content becomes a GC struct.
doc3.getArray('array').insert(0, ['x', 'y', 'z', 'w'])
doc3.getArray('array').delete(1, 2)
assert.ok(
  Y.encodeStateAsUpdateV2(doc3).length > 0,
  'sanity: doc3 should have produced an update at all'
)

const s1Docs = [doc1, doc2, doc3]
const s1UpdatesV1 = s1Docs.map(d => Y.encodeStateAsUpdate(d))
const s1UpdatesV2 = s1Docs.map(d => Y.encodeStateAsUpdateV2(d))

// Ground truth: apply all three updates, in order, to a fresh doc.
const s1Expected = new Y.Doc({ gc: false })
s1UpdatesV1.forEach(u => Y.applyUpdate(s1Expected, u))

// ---------------------------------------------------------------------------
// Scenario 2: one doc, four sequential inserts (each its own update, the way
// a real app streams changes), merged and then diffed against a state vector
// covering only the first two.
// ---------------------------------------------------------------------------

const s2Doc = new Y.Doc({ gc: false })
const s2UpdatesV1 = []
const s2UpdatesV2 = []
s2Doc.on('update', u => s2UpdatesV1.push(u))
s2Doc.on('updateV2', u => s2UpdatesV2.push(u))

const s2Array = s2Doc.getArray('array')
s2Array.insert(0, [1])
s2Array.insert(0, [2])
s2Array.insert(0, [3])
s2Array.insert(0, [4])

assert.equal(s2UpdatesV1.length, 4, 'expected 4 incremental v1 updates')
assert.equal(s2UpdatesV2.length, 4, 'expected 4 incremental v2 updates')

const s2PartialDocV1 = new Y.Doc({ gc: false })
Y.applyUpdate(s2PartialDocV1, s2UpdatesV1[0])
Y.applyUpdate(s2PartialDocV1, s2UpdatesV1[1])
const s2PartialSvV1 = Y.encodeStateVector(s2PartialDocV1)

const s2PartialDocV2 = new Y.Doc({ gc: false })
Y.applyUpdateV2(s2PartialDocV2, s2UpdatesV2[0])
Y.applyUpdateV2(s2PartialDocV2, s2UpdatesV2[1])
const s2PartialSvV2 = Y.encodeStateVector(s2PartialDocV2)

// ---------------------------------------------------------------------------
// Hand off to Ycs.
// ---------------------------------------------------------------------------

const input = {
  scenario1: {
    updatesV1: s1UpdatesV1.map(b64),
    updatesV2: s1UpdatesV2.map(b64)
  },
  scenario2: {
    updatesV1: s2UpdatesV1.map(b64),
    updatesV2: s2UpdatesV2.map(b64),
    partialStateVectorV1: b64(s2PartialSvV1),
    partialStateVectorV2: b64(s2PartialSvV2)
  }
}

const tmpDir = mkdtempSync(join(tmpdir(), 'ycs-updates-interop-'))
const inputPath = join(tmpDir, 'input.json')
writeFileSync(inputPath, JSON.stringify(input))

let cs
try {
  const stdout = execFileSync(
    dotnet,
    ['run', '--project', csprojPath, '-v', 'q', '--', inputPath],
    { encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }
  )
  cs = JSON.parse(stdout)
} finally {
  rmSync(tmpDir, { recursive: true, force: true })
}

// ---------------------------------------------------------------------------
// Scenario 1 checks.
// ---------------------------------------------------------------------------

for (const [label, mergedB64, apply] of [
  ['v1', cs.scenario1.mergedV1, Y.applyUpdate],
  ['v2', cs.scenario1.mergedV2, Y.applyUpdateV2]
]) {
  const merged = new Y.Doc({ gc: false })
  apply(merged, bytes(mergedB64))
  assert.deepEqual(
    json(merged.getArray('array').toJSON()),
    json(s1Expected.getArray('array').toJSON()),
    `scenario1 ${label}: merged array differs from applying updates individually`
  )
  assert.deepEqual(
    json(merged.getMap('map').toJSON()),
    json(s1Expected.getMap('map').toJSON()),
    `scenario1 ${label}: merged map differs from applying updates individually`
  )
}

// encodeStateVectorFromUpdate(V2) must match Yjs' own, byte for byte, given the exact same
// (Ycs-produced) merged update bytes.
assert.equal(
  cs.scenario1.stateVectorFromMergedV1,
  b64(Y.encodeStateVectorFromUpdate(bytes(cs.scenario1.mergedV1))),
  'scenario1: encodeStateVectorFromUpdate (v1) does not match Yjs'
)
assert.equal(
  cs.scenario1.stateVectorFromMergedV2,
  b64(Y.encodeStateVectorFromUpdateV2(bytes(cs.scenario1.mergedV2))),
  'scenario1: encodeStateVectorFromUpdateV2 does not match Yjs'
)

// ---------------------------------------------------------------------------
// Scenario 2 checks.
// ---------------------------------------------------------------------------

const s2FullTarget = [4, 3, 2, 1] // insert(0, [n]) four times, in order.

for (const [label, mergedB64, apply] of [
  ['v1', cs.scenario2.mergedAllV1, Y.applyUpdate],
  ['v2', cs.scenario2.mergedAllV2, Y.applyUpdateV2]
]) {
  const doc = new Y.Doc({ gc: false })
  apply(doc, bytes(mergedB64))
  assert.deepEqual(json(doc.getArray('array').toJSON()), s2FullTarget, `scenario2 ${label}: merged-all array mismatch`)
}

// diffUpdate(V2): applying the diff on top of the partial doc must reconstruct the full doc -
// checked against both Ycs' own diff and (independently) Yjs' own diffUpdate on the same bytes.
for (const [label, mergedB64, diffedB64, partialUpdates, applyFn, diffFn, encodeSvFn] of [
  ['v1', cs.scenario2.mergedAllV1, cs.scenario2.diffedV1, s2UpdatesV1, Y.applyUpdate, Y.diffUpdate, Y.encodeStateVector],
  ['v2', cs.scenario2.mergedAllV2, cs.scenario2.diffedV2, s2UpdatesV2, Y.applyUpdateV2, Y.diffUpdateV2, Y.encodeStateVector]
]) {
  const partial = new Y.Doc({ gc: false })
  applyFn(partial, partialUpdates[0])
  applyFn(partial, partialUpdates[1])
  const sv = encodeSvFn(partial)

  const viaYcs = new Y.Doc({ gc: false })
  applyFn(viaYcs, partialUpdates[0])
  applyFn(viaYcs, partialUpdates[1])
  applyFn(viaYcs, bytes(diffedB64))
  assert.deepEqual(json(viaYcs.getArray('array').toJSON()), s2FullTarget, `scenario2 ${label}: Ycs diffUpdate did not reconstruct the full doc`)

  const viaYjs = new Y.Doc({ gc: false })
  applyFn(viaYjs, partialUpdates[0])
  applyFn(viaYjs, partialUpdates[1])
  applyFn(viaYjs, diffFn(bytes(mergedB64), sv))
  assert.deepEqual(json(viaYjs.getArray('array').toJSON()), s2FullTarget, `scenario2 ${label}: sanity - Yjs' own diffUpdate did not reconstruct the full doc`)
}

// convertUpdateFormatV1ToV2 / V2ToV1: byte-for-byte against Yjs' own conversion of the exact same
// (Ycs-produced) v1 update - this is a deterministic re-encoding, so there's no room for the two
// implementations to legitimately disagree.
assert.equal(
  cs.scenario2.convertedV1ToV2,
  b64(Y.convertUpdateFormatV1ToV2(bytes(cs.scenario2.mergedAllV1))),
  'scenario2: convertUpdateFormatV1ToV2 does not match Yjs byte-for-byte'
)
assert.equal(
  cs.scenario2.convertedV2ToV1RoundTrip,
  cs.scenario2.mergedAllV1,
  'scenario2: convertUpdateFormatV2ToV1(convertUpdateFormatV1ToV2(u)) did not round-trip back to u'
)

// parseUpdateMeta: every client starts at clock 0, and 'to' matches the doc's actual end state.
for (const clock of Object.values(cs.scenario2.parseUpdateMetaFrom)) {
  assert.equal(clock, 0, 'scenario2: parseUpdateMeta.from should start every client at clock 0')
}
const s2MergedDoc = new Y.Doc({ gc: false })
Y.applyUpdate(s2MergedDoc, bytes(cs.scenario2.mergedAllV1))
for (const [client, clock] of Object.entries(cs.scenario2.parseUpdateMetaTo)) {
  const structs = s2MergedDoc.store.clients.get(Number(client))
  const last = structs[structs.length - 1]
  assert.equal(last.id.clock + last.length, clock, `scenario2: parseUpdateMeta.to mismatch for client ${client}`)
}

console.log('OK  updates.js parity (Ycs.UpdateOperations) <-> yjs13: mergeUpdates/mergeUpdatesV2, diffUpdate/diffUpdateV2, ' +
  'encodeStateVectorFromUpdate(V2), convertUpdateFormat(V1ToV2/V2ToV1), parseUpdateMeta, a GC\'d doc')
