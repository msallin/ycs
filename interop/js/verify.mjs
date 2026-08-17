// Applies the update Ycs produced and checks the document matches, both directions.
// Usage: node verify.mjs <yjs13|yjs14> js-doc.json cs-doc.json
import { readFileSync } from 'node:fs'
import assert from 'node:assert/strict'
const Y = await import(process.argv[2] ?? 'yjs13')
const js = JSON.parse(readFileSync(process.argv[3], 'utf8'))
const cs = JSON.parse(readFileSync(process.argv[4], 'utf8'))
const bytes = b64 => new Uint8Array(Buffer.from(b64, 'base64'))
// JSON-normalize: Yjs' toJSON keeps a Uint8Array as-is, JSON renders it index-keyed.
const json = v => JSON.parse(JSON.stringify(v))

// 1. Ycs read our doc and reported the same content back (plus its own edit).
const expectedMap = { ...js.content.map, 'from-ycs': 'written in C#' }
assert.deepEqual(cs.readBack.map, expectedMap, 'map content differs after Ycs read the v1 update')
assert.deepEqual(cs.readBack.array, js.content.array, 'array content differs')
assert.equal(cs.readBack.text, js.content.text + '!', 'text content differs')

// 2. Ycs' own v1 update applies cleanly here and carries Ycs' edit.
for (const [label, update, apply] of [
  ['v1', cs.updateV1, Y.applyUpdate],
  ['v2', cs.updateV2, Y.applyUpdateV2]
]) {
  const doc = new Y.Doc()
  apply(doc, bytes(update))
  assert.deepEqual(json(doc.getMap('map').toJSON()), cs.readBack.map, `${label}: map round-trip`)
  assert.deepEqual(json(doc.getArray('array').toJSON()), cs.readBack.array, `${label}: array round-trip`)
  assert.equal(doc.getText('text').toString(), cs.readBack.text, `${label}: text round-trip`)
  assert.equal(doc.getMap('map').get('from-ycs'), 'written in C#', `${label}: Ycs' own edit missing`)
  if (js.content.textDelta) {
    assert.deepEqual(json(doc.getText('text').toDelta()), json(js.content.textDelta).concat([{ insert: '!' }]),
      `${label}: text delta (formatting) round-trip`)
  } else {
    // v14: no toDelta(); compare the attributed content tree instead.
    assert.equal(JSON.stringify(doc.getText('text').getContent()).includes('"bold":true'), true,
      `${label}: text formatting lost`)
  }
}

// 3. Live merge: apply Ycs' update on top of the original doc.
const merged = new Y.Doc()
Y.applyUpdate(merged, bytes(js.updateV1))
Y.applyUpdate(merged, bytes(cs.updateV1))
assert.equal(merged.getMap('map').get('from-ycs'), 'written in C#', 'merge onto live doc')
assert.equal(merged.getMap('map').get('str'), 'hello ✓ мир', 'merge kept JS content')

console.log(`OK  ${process.argv[2]} (${js.yjsVersion}) <-> Ycs: v1 + v2, both directions, ${Object.keys(js.content.map).length + 1} map keys, formatted text, nested types, delete set`)
