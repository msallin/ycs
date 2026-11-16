// y-protocols sync messages as raw bytes, so that a load client needs no Yjs document.
import * as Y from 'yjs'

export const messageSync = 0
export const syncStep1 = 0
export const syncStep2 = 1
export const syncUpdate = 2

const varUintLength = n => { let l = 1; while (n > 127) { n = Math.floor(n / 128); l++ } return l }

const writeVarUint = (bytes, at, n) => {
  while (n > 127) { bytes[at++] = 128 | (n % 128); n = Math.floor(n / 128) }
  bytes[at++] = n
  return at
}

// Returns [value, offset after it].
export const readVarUint = (bytes, at) => {
  let n = 0
  let mult = 1
  while (true) {
    const b = bytes[at++]
    n += (b & 127) * mult
    if (b < 128) return [n, at]
    mult *= 128
  }
}

const syncMessage = (subtype, payload) => {
  const bytes = new Uint8Array(2 + varUintLength(payload.length) + payload.length)
  bytes[0] = messageSync
  bytes[1] = subtype
  bytes.set(payload, writeVarUint(bytes, 2, payload.length))
  return bytes
}

export const step1Message = stateVector => syncMessage(syncStep1, stateVector)
export const updateMessage = update => syncMessage(syncUpdate, update)
export const emptyStateVector = new Uint8Array([0])

// For a sync message: { subtype, payload }; otherwise null (awareness and anything else).
export const parseSync = bytes => {
  if (bytes[0] !== messageSync) return null
  const [length, at] = readVarUint(bytes, 2)
  return { subtype: bytes[1], payload: bytes.subarray(at, at + length) }
}

// The client and clock of the first struct of a v1 update whose first client group starts with an item, which is
// the case for every update a typing client makes (one insert, one struct).
export const firstStruct = update => {
  let at = readVarUint(update, 0)[1] // number of client groups
  at = readVarUint(update, at)[1] // structs in the first group
  const [client, next] = readVarUint(update, at)
  const [clock] = readVarUint(update, next)
  return { client, clock }
}

export const typedChar = (clientIndex, k) => String.fromCharCode(97 + (clientIndex * 7 + k) % 26)

// The update messages of a client that types `count` characters, each at the end of what it typed before (so each
// update depends only on its own earlier ones): message k inserts the struct (clientId, clock k).
export const typingMessages = (clientId, clientIndex, count) => {
  const doc = new Y.Doc()
  doc.clientID = clientId
  const text = doc.getText('text')
  const out = new Array(count)
  let k = 0
  doc.on('update', update => { out[k] = updateMessage(update) })
  for (; k < count; k++) text.insert(k, typedChar(clientIndex, k))
  doc.destroy()
  return out
}

// The state a client's first `count` typed characters amount to, for the expected document.
export const typingState = (clientId, clientIndex, count) => {
  const doc = new Y.Doc()
  doc.clientID = clientId
  const text = doc.getText('text')
  for (let k = 0; k < count; k++) text.insert(k, typedChar(clientIndex, k))
  const state = Y.encodeStateAsUpdate(doc)
  doc.destroy()
  return state
}
