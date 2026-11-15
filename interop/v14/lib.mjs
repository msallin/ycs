// Shared by gen.mjs and roundtrip.mjs: how a document looks once Yjs 13.6.32 reads it, in the canonical
// form tests/Ycs.Tests/V14InteropTests.cs renders Ycs' documents in. v14 has one type (Y.Node); what it
// writes arrives in v13 as the type the reader asks for: a root as text, map, array or XML fragment, a
// nested named node as a YXmlElement, an unnamed one as a YXmlFragment.
import * as Y13 from 'yjs'

export const b64 = u8 => Buffer.from(u8).toString('base64')
export const bytes = s => new Uint8Array(Buffer.from(s, 'base64'))

// JSON with Uint8Arrays as { $b: base64 } and nested types by their own view.
const value = v => {
  if (v instanceof Uint8Array) return { $b: b64(v) }
  if (v instanceof Y13.AbstractType) return node(v)
  if (Array.isArray(v)) return v.map(value)
  if (v !== null && typeof v === 'object') return Object.fromEntries(Object.keys(v).sort().map(k => [k, value(v[k])]))
  return v
}

const attrs = a => Object.fromEntries(Object.keys(a).sort().map(k => [k, value(a[k])]))

// A nested type, by what v13 reads it as.
export const node = t => {
  if (t instanceof Y13.XmlElement) return { $xml: t.nodeName, attrs: attrs(t.getAttributes()), children: t.toArray().map(value) }
  if (t instanceof Y13.XmlText) return { $xmltext: t.toDelta().map(op => ({ ...op, insert: value(op.insert) })) }
  if (t instanceof Y13.XmlFragment) return { $fragment: t.toArray().map(value) }
  if (t instanceof Y13.Text) return { $text: t.toDelta().map(op => ({ ...op, insert: value(op.insert) })) }
  if (t instanceof Y13.Map) return { $map: attrs(Object.fromEntries(t.entries())) }
  if (t instanceof Y13.Array) return { $array: t.toArray().map(value) }
  throw new Error(`unknown type ${t.constructor.name}`)
}

// A root with its length (a sequence's counts what its view may leave out: v14 writes an object inserted into
// text as ContentAny, which v13's toDelta skips).
export const root = (doc, { name, kind }) => {
  switch (kind) {
    case 'text': return { ...node(doc.getText(name)), length: doc.getText(name).length }
    case 'map': return node(doc.getMap(name))
    case 'array': return { ...node(doc.getArray(name)), length: doc.getArray(name).length }
    case 'xml': return { ...node(doc.getXmlFragment(name)), length: doc.getXmlFragment(name).length, string: doc.getXmlFragment(name).toString() }
    default: throw new Error(`unknown kind ${kind}`)
  }
}

/** The view of every root, as Yjs 13 reads the update. */
export const view13 = (update, roots, v2 = false) => {
  const doc = new Y13.Doc()
  ;(v2 ? Y13.applyUpdateV2 : Y13.applyUpdate)(doc, update)
  return Object.fromEntries(roots.map(r => [r.name, root(doc, r)]))
}

/** A state vector as { client: clock }, sorted by client (client ids up to 2^53 - 1 are exact in a double). */
export const stateVector = sv => Object.fromEntries([...sv].sort((a, b) => a[0] - b[0]).map(([k, v]) => [String(k), v]))

/** Whether Yjs 13 re-encodes the update's document byte for byte (v1 and v2). */
export const reencodes13 = (v1, v2) => {
  const d1 = new Y13.Doc()
  Y13.applyUpdate(d1, v1)
  const d2 = new Y13.Doc()
  Y13.applyUpdateV2(d2, v2)
  return { v1: b64(Y13.encodeStateAsUpdate(d1)) === b64(v1), v2: b64(Y13.encodeStateAsUpdateV2(d2)) === b64(v2) }
}
