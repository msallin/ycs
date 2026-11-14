// Builds a y-prosemirror-shaped document with real Yjs (Y.XmlFragment > Y.XmlElement, with
// attributes, containing a formatted Y.XmlText and a nested Y.XmlElement, plus a deletion), shells
// out to the Ycs.Xml.Interop console app to decode + edit it with Ycs, then applies Ycs' own v1 and
// v2 updates back into real Yjs and checks the resulting tree -- including attributes and text
// formatting -- matches on both sides.
// Usage: node check-xml.mjs
import { execFileSync } from 'node:child_process'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'

const Y = await import('yjs13')
const b64 = u8 => Buffer.from(u8).toString('base64')
const bytes = b64s => new Uint8Array(Buffer.from(b64s, 'base64'))

const doc = new Y.Doc()
const frag = doc.getXmlFragment('xml')

doc.transact(() => {
  const p = new Y.XmlElement('paragraph')
  p.setAttribute('align', 'center')
  p.setAttribute('class', 'intro')

  const bold = new Y.XmlText()
  bold.insert(0, 'Hello ', { bold: true })
  bold.insert(6, 'world', {})
  p.insert(0, [bold])

  const em = new Y.XmlElement('em')
  em.setAttribute('title', 'shout')
  const emText = new Y.XmlText('!!!')
  em.insert(0, [emText])
  p.insert(1, [em])

  // Inserted then deleted in the same transaction: proves the delete set round-trips too.
  const scratch = new Y.XmlElement('scratch')
  frag.insert(0, [p, scratch])
})
frag.delete(1, 1) // delete 'scratch', leaving only 'paragraph'

const expectedBeforeEdit = frag.toString()
const updateV1 = b64(Y.encodeStateAsUpdate(doc))

const dotnet = process.env.DOTNET || 'dotnet'
const csproj = fileURLToPath(new URL('../Ycs.Xml.Interop/Ycs.Xml.Interop.csproj', import.meta.url))

let csOut
try {
  csOut = execFileSync(dotnet, ['run', '--no-build', '--project', csproj, '-v', 'q', '--', updateV1],
    { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 })
} catch (e) {
  console.error(`Ycs.Xml.Interop failed:\n${e.stderr || e.message}`)
  process.exit(1)
}
const cs = JSON.parse(csOut)

// 1. Ycs read the tree correctly (attributes, nested element, formatted text).
assert.equal(cs.readTree.paragraphNodeName, 'paragraph', 'Ycs misread the top-level node name')
assert.deepEqual(cs.readTree.attributes, { align: 'center', class: 'intro' }, 'Ycs misread paragraph attributes')
assert.equal(cs.readTree.emNodeName, 'em', 'Ycs misread the nested element name')
assert.equal(cs.readTree.emTitle, 'shout', 'Ycs misread the nested element attribute')
assert.equal(cs.readTree.emText, '!!!', 'Ycs misread the nested element text')
assert.equal(cs.toStringBeforeEdit, expectedBeforeEdit, 'Ycs\' XML serialization differs from Yjs\' before any edit')

// 2. Ycs' own v1 and v2 updates apply cleanly in real Yjs and carry Ycs' edits.
for (const [label, update, apply] of [
  ['v1', cs.updateV1, Y.applyUpdate],
  ['v2', cs.updateV2, Y.applyUpdateV2]
]) {
  const verifyDoc = new Y.Doc()
  apply(verifyDoc, bytes(update))
  const gotFrag = verifyDoc.getXmlFragment('xml')

  assert.equal(gotFrag.toString(), cs.toStringAfterEdit, `${label}: XML tree round-trip differs`)

  const paragraph = gotFrag.get(0)
  assert.equal(paragraph.nodeName, 'paragraph', `${label}: top-level node name lost`)
  assert.equal(paragraph.getAttribute('edited-by'), 'ycs', `${label}: Ycs' own attribute edit missing`)
  assert.equal(paragraph.getAttribute('align'), 'center', `${label}: original attribute lost`)

  const em = paragraph.get(1)
  assert.equal(em.nodeName, 'em', `${label}: nested element lost`)
  assert.equal(em.getAttribute('shout-level'), '11', `${label}: Ycs' nested-element edit missing`)

  const bold = paragraph.get(0)
  const delta = bold.toDelta()
  assert.ok(delta.some(d => d.insert.includes('Hello') && d.attributes?.bold === true),
    `${label}: bold formatting lost on the round trip`)

  const tail = gotFrag.get(1)
  assert.equal(tail.constructor.name, 'YXmlText', `${label}: Ycs' appended tail node missing`)
  assert.equal(tail.toString(), '<italic>tail</italic>', `${label}: Ycs' appended formatted text lost`)
}

// 3. Live merge: apply Ycs' update on top of the still-open original Yjs doc.
Y.applyUpdate(doc, bytes(cs.updateV1))
assert.equal(doc.getXmlFragment('xml').get(0).getAttribute('edited-by'), 'ycs', 'merge onto live doc')
assert.equal(doc.getXmlFragment('xml').toString(), cs.toStringAfterEdit, 'merge onto live doc: tree differs')

console.log('OK  check-xml: yjs13 <-> Ycs XmlFragment/XmlElement/XmlText, attributes, nested elements, ' +
  'formatting, delete set, tree walker, v1 + v2, both directions')
