// Snapshots + relative/absolute positions, byte-for-byte against real Yjs.
//
// 1. Real Yjs (yjs13, gc disabled) builds a doc with a YText and a YArray, takes a snapshot, and
//    creates relative positions at several indices in both - including both `assoc` values.
// 2. Ycs.Positions.Interop (a small C# console app) decodes the Yjs-encoded snapshot and relative
//    positions, resolves each relative position back to an absolute index against the same document,
//    and re-encodes both the snapshot and the positions.
// 3. Back in Yjs: the re-encoded bytes must decode to snapshots/positions equivalent to the
//    originals, and must resolve to the same indices Yjs itself computes.
//
// Usage: node check-positions.mjs (exits non-zero with a message on any mismatch)
import { execFileSync } from 'node:child_process'
import { mkdtempSync, writeFileSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, dirname } from 'node:path'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'

const Y = await import('yjs13')
const b64 = u8 => Buffer.from(u8).toString('base64')
const bytes = b64str => new Uint8Array(Buffer.from(b64str, 'base64'))

const here = dirname(fileURLToPath(import.meta.url))
const dotnet = process.env.DOTNET || (process.env.HOME + '/.dotnet/dotnet')
const interopProject = join(here, '..', 'Ycs.Positions.Interop', 'Ycs.Positions.Interop.csproj')

// 1. Build the doc and take a snapshot + a batch of relative positions with real Yjs.
const doc = new Y.Doc({ gc: false })
doc.transact(() => {
  doc.getText('text').insert(0, 'hello world')
  doc.getArray('array').insert(0, ['a', 'b', 'c'])
})
doc.getText('text').delete(2, 3)   // 'hello world' -> 'he world'
doc.getArray('array').delete(1, 1) // ['a','b','c'] -> ['a','c']

const snapshot = Y.snapshot(doc)

const text = doc.getText('text')
const array = doc.getArray('array')

const cases = []
for (const [label, type] of [['text', text], ['array', array]]) {
  for (const assoc of [0, -1, 1]) {
    for (const index of [0, Math.floor(type.length / 2), type.length]) {
      cases.push({ label: `${label}@${index},assoc=${assoc}`, type, index, assoc })
    }
  }
}

// Attach each case's relative position in place, so both the request-building step below and the
// verification loop further down can see it.
for (const c of cases) {
  c.rpos = Y.createRelativePositionFromTypeIndex(c.type, c.index, c.assoc)
}

// 2. Hand it all to Ycs via the interop console app.
const request = {
  updateV1: b64(Y.encodeStateAsUpdate(doc)),
  snapshotV1: b64(Y.encodeSnapshot(snapshot)),
  positions: cases.map(({ label, rpos }) => ({ label, encoded: b64(Y.encodeRelativePosition(rpos)) }))
}

const dir = mkdtempSync(join(tmpdir(), 'ycs-positions-'))
const requestPath = join(dir, 'request.json')
writeFileSync(requestPath, JSON.stringify(request))

const stdout = execFileSync(dotnet, ['run', '--project', interopProject, '-v', 'q', '--', requestPath], {
  encoding: 'utf8'
})
const response = JSON.parse(stdout)

// 3. Verify everything Ycs handed back, in Yjs.

// 3a. The re-encoded snapshot decodes to something Yjs considers equal to the original, and
// Y.decodeSnapshot(ycsBytes) round-trips through Yjs' own encoder identically.
const reencodedSnapshot = Y.decodeSnapshot(bytes(response.snapshotReencoded))
assert.ok(Y.equalSnapshots(snapshot, reencodedSnapshot), 'snapshot: Ycs re-encoding is not equal to the original once decoded by Yjs')
assert.deepEqual(b64(Y.encodeSnapshot(reencodedSnapshot)), request.snapshotV1, 'snapshot: re-encoding through Yjs again does not reproduce the original bytes')

// 3b. Each relative position: re-encoded bytes decode to an equivalent position, and Ycs' resolved
// index/assoc matches what Yjs itself resolves against the same document.
assert.equal(response.positions.length, cases.length, 'position count mismatch')
const byLabel = new Map(response.positions.map(p => [p.label, p]))

for (const { label, rpos } of cases) {
  const got = byLabel.get(label)
  assert.ok(got, `missing response entry for ${label}`)

  const expected = Y.createAbsolutePositionFromRelativePosition(rpos, doc)
  assert.ok(expected, `${label}: Yjs itself could not resolve the position it created`)
  assert.equal(got.resolvedIndex, expected.index, `${label}: resolved index mismatch (Ycs ${got.resolvedIndex} vs Yjs ${expected.index})`)
  assert.equal(got.resolvedAssoc, expected.assoc, `${label}: resolved assoc mismatch`)

  // Compare decoded-vs-decoded, not the pristine in-memory 'rpos' against the round-tripped one: the
  // wire format only ever carries one of item/tname/type (see RelativePosition.Write), so an in-memory
  // position for a root-level type can have both 'item' and 'tname' set while anything that went
  // through encode/decode - Ycs' re-encoding included - will only ever have the one the wire preferred.
  // Decoding the *original* bytes puts both sides on equal footing.
  const decodedOriginal = Y.decodeRelativePosition(bytes(Y.encodeRelativePosition(rpos)))
  const decodedReencoded = Y.decodeRelativePosition(bytes(got.reencoded))
  assert.ok(Y.compareRelativePositions(decodedOriginal, decodedReencoded), `${label}: re-encoded position is not equivalent to the original once decoded by Yjs`)
}

console.log(`OK  yjs13 (${Y.default?.version ?? '13.6.32'}) <-> Ycs: snapshot v1 round-trip + ${cases.length} relative positions (text & array, assoc -1/0/1) resolved and re-encoded`)
