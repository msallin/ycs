// Shared by gen.mjs and roundtrip.mjs: how a Yjs doc's state is observed and recorded.
import * as Y from 'yjs'

export const b64 = u8 => Buffer.from(u8).toString('base64')

// JSON with Uint8Array as {"$bytes": base64}; key order is irrelevant (the C# side sorts).
export function plain (v) {
  if (v instanceof Uint8Array) return { $bytes: b64(v) }
  if (Array.isArray(v)) return v.map(plain)
  if (v !== null && typeof v === 'object') {
    const o = {}
    for (const k of Object.keys(v).sort()) o[k] = plain(v[k])
    return o
  }
  return v
}

export function stateOf (doc, roots) {
  const content = {}
  for (const r of roots) {
    if (r.type === 'text') {
      const t = doc.getText(r.name)
      content[r.name] = { text: t.toString() }
      if (r.delta) content[r.name].delta = plain(t.toDelta())
    } else if (r.type === 'map') {
      content[r.name] = { json: plain(doc.getMap(r.name).toJSON()) }
    } else if (r.type === 'array') {
      content[r.name] = { json: plain(doc.getArray(r.name).toJSON()) }
    } else throw new Error(r.type)
  }
  return content
}

export function stateVectorOf (doc) {
  const sv = {}
  for (const [client, clock] of [...Y.decodeStateVector(Y.encodeStateVector(doc))].sort((a, b) => a[0] - b[0])) sv[client] = clock
  return sv
}

export function deleteSetOf (doc) {
  const ds = {}
  const clients = [...Y.createDeleteSetFromStructStore(doc.store).clients].sort((a, b) => a[0] - b[0])
  for (const [client, items] of clients) {
    const ranges = items.map(d => [d.clock, d.len]).sort((a, b) => a[0] - b[0])
    const merged = []
    for (const [c, l] of ranges) {
      const last = merged[merged.length - 1]
      if (last && last[0] + last[1] >= c) last[1] = Math.max(last[1], c + l - last[0])
      else merged.push([c, l])
    }
    if (merged.length > 0) ds[client] = merged
  }
  return ds
}

export function docFrom (updates, gc, v2 = false) {
  const doc = new Y.Doc({ gc })
  for (const u of updates) (v2 ? Y.applyUpdateV2 : Y.applyUpdate)(doc, u)
  return doc
}

/**
 * The item list of each sequence root ("text" / "array"), in list order, as maximal runs
 * [client, clock, length, deleted (0/1)] of consecutive ids: what Yjs' integrate decided, with
 * tombstones and independent of how Yjs happened to split or merge its structs.
 */
export function itemOrderOf (doc, roots) {
  const out = {}
  for (const r of roots) {
    if (r.type !== 'text' && r.type !== 'array') continue
    const type = r.type === 'text' ? doc.getText(r.name) : doc.getArray(r.name)
    const runs = []
    for (let s = type._start; s !== null; s = s.right) {
      const last = runs[runs.length - 1]
      const deleted = s.deleted ? 1 : 0
      if (last && last[0] === s.id.client && last[1] + last[2] === s.id.clock && last[3] === deleted) last[2] += s.length
      else runs.push([s.id.client, s.id.clock, s.length, deleted])
    }
    out[r.name] = runs
  }
  return out
}
