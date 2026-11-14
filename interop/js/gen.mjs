// Builds a reference document with real Yjs and dumps it plus its updates.
// Usage: node gen.mjs <yjs13|yjs14> > js-doc.json
const Y = await import(process.argv[2] ?? 'yjs13')
const b64 = u8 => Buffer.from(u8).toString('base64')

const doc = new Y.Doc()
doc.transact(() => {
  const map = doc.getMap('map')
  map.set('str', 'hello ✓ мир')
  map.set('int', 42)
  map.set('float', 3.5)
  map.set('bool', true)
  map.set('null', null)
  map.set('json', { a: 1, b: ['x', 'y'] })
  map.set('bin', new Uint8Array([1, 2, 3, 250]))
  const nested = new Y.Map()
  nested.set('deep', 'value')
  map.set('nested', nested)

  const arr = doc.getArray('array')
  arr.insert(0, ['a', 1, false, { k: 'v' }])
  const inner = new Y.Array()
  inner.insert(0, ['i0', 'i1'])
  arr.insert(4, [inner])

  const text = doc.getText('text')
  text.insert(0, 'plain ')
  text.insert(6, 'bold', { bold: true })
  text.insert(10, ' tail')
  text.insertEmbed(15, { image: 'x.png' })
})
// a second transaction, then a delete, so the update carries a non-empty delete set
doc.getArray('array').delete(1, 1)

console.log(JSON.stringify({
  yjsVersion: Y.default?.version ?? process.argv[2],
  updateV1: b64(Y.encodeStateAsUpdate(doc)),
  updateV2: b64(Y.encodeStateAsUpdateV2(doc)),
  stateVectorV1: b64(Y.encodeStateVector(doc)),
  content: {
    map: doc.getMap('map').toJSON(),
    array: doc.getArray('array').toJSON(),
    text: doc.getText('text').toString(),
    // v13 exposes toDelta(); v14 replaced it with getContent().
    textDelta: doc.getText('text').toDelta?.() ?? null
  }
}, null, 2))
