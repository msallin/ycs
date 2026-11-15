// Documents nested as deep as Yjs handles, written by real Yjs, for tests/Ycs.Tests/DeepNestingTests.cs.
// Usage: node deep-gen.mjs > ../../tests/Ycs.Tests/Fixtures/deep-nesting.json
import * as Y from 'yjs13'

const b64 = u8 => Buffer.from(u8).toString('base64')
const VALUES = 3000
const TYPES = 2000

// A value: lists and objects in turn, n levels below the top (an attribute's or embed's object adds one).
function value (n) {
  const root = []
  let v = root
  for (let i = 0; i < n; i++) {
    const w = i % 2 === 0 ? {} : []
    if (Array.isArray(v)) v.push(w, i)
    else Object.assign(v, { k: w, i })
    v = w
  }
  return root
}

const cases = {}
function record (name, build) {
  const doc = new Y.Doc()
  doc.clientID = 1
  build(doc)
  cases[name] = { v1: b64(Y.encodeStateAsUpdate(doc)), v2: b64(Y.encodeStateAsUpdateV2(doc)) }
}

record('values', doc => doc.getMap('m').set('v', value(VALUES)))
record('format', doc => doc.getText('t').insert(0, 'abc', { deep: value(VALUES - 1) }))
record('embed', doc => doc.getText('t').insertEmbed(0, { deep: value(VALUES - 1) }))
record('maps', doc => {
  let m = doc.getMap('m')
  for (let i = 0; i < TYPES; i++) {
    const c = new Y.Map()
    m.set('c', c)
    m = c
  }
  m.set('leaf', 1)
})
record('arrays', doc => {
  let a = doc.getArray('a')
  for (let i = 0; i < TYPES; i++) {
    const c = new Y.Array()
    a.push([c])
    a = c
  }
  a.push(['leaf'])
})
record('xml', doc => {
  let x = doc.getXmlFragment('x')
  for (let i = 0; i < TYPES; i++) {
    const c = new Y.XmlElement('p')
    x.push([c])
    x = c
  }
  x.push([new Y.XmlText('leaf')])
})

console.log(JSON.stringify({ yjs: '13.6.32', values: VALUES, types: TYPES, cases }, null, 1))
