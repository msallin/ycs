// Writes tests/Ycs.Server.Tests/Fixtures/awareness-vectors.json with the real y-protocols/awareness.
// Usage: node gen-awareness.mjs   (after npm install in this folder)
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'
import * as Y from 'yjs'
import * as encoding from 'lib0/encoding'
import * as awarenessProtocol from 'y-protocols/awareness'

const here = path.dirname(fileURLToPath(import.meta.url))
const out = path.join(here, '..', '..', 'tests', 'Ycs.Server.Tests', 'Fixtures', 'awareness-vectors.json')

const hex = (u8) => Buffer.from(u8).toString('hex')
const MAX_SAFE = Number.MAX_SAFE_INTEGER

// A remote peer: a real Awareness whose encoded updates are what clients send.
const peers = new Map()
const peer = (id) => {
  if (!peers.has(id)) {
    const doc = new Y.Doc()
    doc.clientID = id
    peers.set(id, new awarenessProtocol.Awareness(doc))
  }
  return peers.get(id)
}
const encodePeer = (id, ids = [id]) => awarenessProtocol.encodeAwarenessUpdate(peer(id), ids)

// Hand-made entries, for what a real peer does not produce (formatting, a clock that goes back).
const raw = (entries) => {
  const e = encoding.createEncoder()
  encoding.writeVarUint(e, entries.length)
  for (const [id, clock, text] of entries) {
    encoding.writeVarUint(e, id)
    encoding.writeVarUint(e, clock)
    encoding.writeVarString(e, text)
  }
  return encoding.toUint8Array(e)
}

const receiverDoc = new Y.Doc()
receiverDoc.clientID = 4000000001
const receiver = new awarenessProtocol.Awareness(receiverDoc)
receiver.setLocalState(null) // the server has no state of its own

const steps = []
let events
receiver.on('change', (c, origin) => { events.change = { added: c.added, updated: c.updated, removed: c.removed } })
receiver.on('update', (c, origin) => { events.update = { added: c.added, updated: c.updated, removed: c.removed } })

const snapshot = () => {
  const clients = []
  receiver.meta.forEach((meta, id) => {
    if (id === receiver.clientID) return
    const has = receiver.states.has(id)
    clients.push({ id, clock: meta.clock, state: has ? receiver.states.get(id) : null, present: has })
  })
  const present = [...receiver.states.keys()].filter((id) => id !== receiver.clientID)
  const encoded = present.length > 0 ? hex(awarenessProtocol.encodeAwarenessUpdate(receiver, present)) : null
  return { clients, present, encodedAll: encoded }
}

// reformatted: a state kept as received, which y-protocols re-encodes with JSON.stringify
const apply = (name, bytes, reformatted = false) => {
  events = { change: null, update: null }
  awarenessProtocol.applyAwarenessUpdate(receiver, bytes, 'test')
  steps.push({ name, op: 'apply', reformatted, input: hex(bytes), change: events.change, update: events.update, after: snapshot() })
}
const remove = (name, ids) => {
  events = { change: null, update: null }
  awarenessProtocol.removeAwarenessStates(receiver, ids, 'close')
  const removedEncoded = ids.filter((id) => receiver.meta.has(id))
  steps.push({
    name, op: 'remove', ids, change: events.change, update: events.update, after: snapshot(),
    encodedRemoved: hex(awarenessProtocol.encodeAwarenessUpdate(receiver, removedEncoded)), encodedIds: removedEncoded
  })
}

const state = (o) => o
// first sight of three clients; unicode and nested JSON
peer(1).setLocalState(state({ user: { name: 'Ada', color: '#ff0000' }, cursor: { anchor: 3, head: 7 } }))
apply('add 1', encodePeer(1))
peer(2).setLocalState(state({ user: { name: 'Zoë 日本語 😀', color: '#00ff00' }, tags: ['a', ['b', { c: null }]], n: 1.5e-7, big: 12345678901234567890, esc: 'q"\\\n\u0001' }))
peer(3).setLocalState(state({}))
awarenessProtocol.applyAwarenessUpdate(peer(2), encodePeer(3), 'peer')
apply('add 2 and 3 in one message', encodePeer(2, [2, 3]))
// same clock, same state: an update with no change
apply('repeat 2', encodePeer(2))
// a state that differs
peer(1).setLocalState(state({ user: { name: 'Ada', color: '#0000ff' } }))
const oldClock1 = encodePeer(1)
apply('update 1', oldClock1)
// same content, higher clock: update but not change
peer(3).setLocalState(state({}))
apply('renew 3', encodePeer(3))
// a clock that goes backwards is ignored
apply('stale 1 (old clock)', raw([[1, 1, JSON.stringify({ stale: true })]]))
apply('older again', raw([[1, 0, JSON.stringify({ stale: true })]]))
// formatting is kept as received; object key order does not make a change
apply('reformatted 3', raw([[3, 99, ' { "z" : 1 ,\n "a":[1, 2] } ']]), true)
apply('reordered 3', raw([[3, 100, '{"a":[1,2],"z":1}']]))
// a null state with the current clock removes; one with an older clock does not
apply('null 3 with old clock', raw([[3, 5, 'null']]))
apply('null 3 with current clock', raw([[3, 100, 'null']]))
apply('null 3 again (nothing to remove)', raw([[3, 100, 'null']]))
// a null state with a higher clock for a client that is already gone: reported removed
apply('null 3 with higher clock', raw([[3, 101, 'null']]))
// a state for a removed client needs a higher clock
apply('3 back with its old clock', raw([[3, 101, '{"back":1}']]))
apply('3 back with a higher clock', raw([[3, 102, '{"back":2}']]))
// a null state for a client never seen: no event, the clock is kept
apply('null 9 never seen', raw([[9, 4, 'null']]))
apply('9 appears with an older clock', raw([[9, 3, '{"x":1}']]))
apply('9 appears with a newer clock', raw([[9, 5, '{"x":2}']]))
// two entries for one client in one message apply in order
apply('twice in one message', raw([[7, 1, '{"i":1}'], [7, 2, '{"i":2}'], [7, 2, '{"i":3}'], [8, 1, 'null']]))
// large ids and clocks: lib0 numbers, exact up to 2^53 - 1
apply('large ids', raw([[4294967296, 4294967296, '{"a":1}'], [4294967295, 1, '{"a":2}'], [MAX_SAFE, MAX_SAFE, '{"a":3}'], [2 ** 40 + 5, 2 ** 35, '{"a":4}']]))
// real clients with a random id of 32 bits
peer(3735928559).setLocalState(state({ user: { name: 'Max' } }))
apply('32 bit id', encodePeer(3735928559))
// an empty message
apply('empty message', raw([]))
// removal when a connection closes: remote clients keep their clock
remove('remove 1 and 2 and an unknown id', [1, 2, 123456])
remove('remove again', [1, 2])
apply('1 returns with the old clock', encodePeer(1))
peer(1).setLocalState(state({ again: true }))
apply('1 returns with a higher clock', encodePeer(1))

// encoding choices: explicit order and a client with a null state
const encodes = []
const enc = (ids) => encodes.push({ ids, hex: hex(awarenessProtocol.encodeAwarenessUpdate(receiver, ids)) })
enc([2, 1])
enc([1, 1])
enc([])
enc([MAX_SAFE, 4294967296, 2])
enc([3])

fs.mkdirSync(path.dirname(out), { recursive: true })
fs.writeFileSync(out, JSON.stringify({
  generator: 'interop/server/gen-awareness.mjs',
  versions: { 'y-protocols': '1.0.7', lib0: '0.2.120' },
  maxSafeInteger: MAX_SAFE,
  steps,
  encodes
}, null, 1) + '\n')
console.log(`wrote ${out}: ${steps.length} steps`)
process.exit(0)
