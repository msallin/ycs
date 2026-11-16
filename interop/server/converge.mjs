// The convergence driver: y-websocket clients do seeded random steps against a running server (edits, offline edits,
// disconnects, abrupt drops, late joins, awareness), then every room is checked once it is quiet:
//   1. every client has the same state vector and content as every other,
//   2. a fresh client joining the room gets that content,
//   3. a plain Yjs doc with every update any client made locally, applied in a shuffled order, has that content,
//   4. every connected client sees exactly the other connected clients' last awareness states.
// The seed fixes the steps, not the timing, so a race may need a few replays.
// Usage: node converge.mjs --url ws://127.0.0.1:5000/ws [--seeds 10] [--first 1] [--parallel 4]
//        [--clients 6] [--rooms 1|2] [--steps n] [--name ycs] [--transport websocket|signalr|mixed] [--verbose]
// The transport is the clients' provider: y-websocket's over /ws, interop/signalr's over the hub at /hub (the Ycs
// server only; the hub is on the --url's host), or mixed: the clients alternate between the two in each room.
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'
import WebSocket from 'ws'
import { sleep, waitFor } from './wait.mjs'
import { writeFileSync } from 'node:fs'
import { register } from 'node:module'

const parseArgs = argv => {
  const options = { seeds: 10, first: 1, parallel: 4, name: 'server', transport: 'websocket', verbose: false }
  for (let i = 0; i < argv.length; i++) {
    const key = argv[i].replace(/^--/, '')
    if (key === 'verbose') {
      options.verbose = true
    } else if (['url', 'name', 'transport'].includes(key)) {
      options[key] = argv[++i]
    } else if (['seeds', 'first', 'parallel', 'clients', 'rooms', 'steps'].includes(key)) {
      options[key] = Number(argv[++i])
    } else {
      throw new Error(`unknown option ${argv[i]}`)
    }
  }
  if (!['websocket', 'signalr', 'mixed'].includes(options.transport)) {
    throw new Error(`unknown transport ${options.transport}: websocket, signalr or mixed`)
  }
  options.url ??= process.env.YCS_SERVER_URL
  if (!options.url) {
    throw new Error('usage: node converge.mjs --url ws://host:port/ws [--seeds n] [--first seed] ...')
  }
  return options
}

// Set once the options are parsed, when the transport is not websocket.
let SignalRProvider = null

// The socket under a provider, for an abrupt drop (SignalR: the hub connection's private WebSocket, closed without a
// hub close message), or null while it has none.
const socketOf = provider =>
  SignalRProvider !== null && provider instanceof SignalRProvider
    ? provider.hub?.connection?.transport?._webSocket ?? null
    : provider.ws

// mulberry32
const prng = seed => {
  let a = seed >>> 0
  const next = () => {
    a = (a + 0x6D2B79F5) >>> 0
    let t = a
    t = Math.imul(t ^ (t >>> 15), t | 1)
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61)
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
  const int = n => Math.floor(next() * n)
  return {
    next,
    int,
    pick: list => list[int(list.length)],
    chance: p => next() < p,
    weighted: table => {
      let r = next() * table.reduce((sum, [w]) => sum + w, 0)
      for (const [w, value] of table) {
        if ((r -= w) < 0) return value
      }
      return table[table.length - 1][1]
    }
  }
}

// JSON with sorted keys: a Y.Map's key order is the order its entries were integrated, which differs per client.
const canonical = value => {
  if (Array.isArray(value)) return value.map(canonical)
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(k => [k, canonical(value[k])]))
  }
  return value
}

const contentOf = doc => canonical({
  text: doc.getText('text').toDelta(),
  map: doc.getMap('map').toJSON(),
  array: doc.getArray('array').toJSON()
})

const stateVectorOf = doc =>
  Object.fromEntries([...Y.decodeStateVector(Y.encodeStateVector(doc))].sort(([a], [b]) => a - b))

const show = value => {
  const s = JSON.stringify(value)
  return s === undefined ? 'undefined' : s.length > 200 ? `${s.slice(0, 200)}...` : s
}

// The path of the first difference between two canonical values, or null.
const firstDifference = (a, b, path = '') => {
  if (a === b) return null
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object' || Array.isArray(a) !== Array.isArray(b)) {
    return `${path || '/'}: ${show(a)} != ${show(b)}`
  }
  const keys = Array.isArray(a)
    ? [...Array(Math.max(a.length, b.length)).keys()]
    : [...new Set([...Object.keys(a), ...Object.keys(b)])].sort()
  for (const key of keys) {
    const d = firstDifference(a[key], b[key], `${path}/${key}`)
    if (d !== null) return d
  }
  return null
}

// In some orders Yjs 13.6 leaves complete updates pending (it retries its pending structs only when a missing clock
// arrives, and an update that fills one while itself waiting is merged into them unretried); a second pass integrates
// them. README, "The oracle".
const oracleOf = updates => {
  const doc = new Y.Doc()
  for (const update of updates) Y.applyUpdate(doc, update)
  if (doc.store.pendingStructs !== null) {
    for (const update of updates) Y.applyUpdate(doc, update)
  }
  return doc
}

const letters = 'abcdefghijklmnopqrstuvwxyz'

class Failure extends Error {}

const runSeed = async (seed, options) => {
  const r = prng(seed)
  const clientCount = options.clients ?? 6
  const roomCount = options.rooms ?? (r.chance(0.3) ? 2 : 1)
  const stepCount = options.steps ?? 40 + r.int(80)
  let lateCount = Math.min(r.int(3), clientCount - roomCount)
  const tag = `${options.name}${options.transport === 'websocket' ? '' : `-${options.transport}`}-s${seed}-${process.pid}-${Date.now().toString(36)}`

  let lastActivity = Date.now()
  const mark = () => { lastActivity = Date.now() }
  // Marks every message in either direction, for the quiescence wait.
  class TracedWebSocket extends WebSocket {
    constructor (...args) {
      super(...args)
      this.on('message', mark)
      this.on('error', () => {})
    }

    send (...args) {
      mark()
      super.send(...args)
    }
  }

  const rooms = Array.from({ length: roomCount }, (_, i) => ({ name: `${tag}-r${i}`, clients: [], updates: [] }))
  const all = []
  let step = 0
  const log = options.verbose ? (...a) => console.log(`  [${seed}:${step}]`, ...a) : () => {}

  // A client of the room: mixed alternates between the providers by the client's number.
  const newProvider = (url, room, doc, number, WebSocketPolyfill) =>
    options.transport === 'signalr' || (options.transport === 'mixed' && number % 2 === 1)
      ? new SignalRProvider(url.replace(/^ws/, 'http').replace(/\/ws$/, '/hub'), room, doc, { maxBackoffTime: 500 })
      : new WebsocketProvider(url, room, doc, { WebSocketPolyfill, disableBc: true, maxBackoffTime: 500 })

  const join = room => {
    const doc = new Y.Doc()
    const provider = newProvider(options.url, room.name, doc, all.length, TracedWebSocket)
    const client = { name: `c${all.length}`, room, doc, provider, alive: true, cursor: 0 }
    // A SignalR provider has no traced socket: what arrives (document and awareness updates) marks the activity.
    doc.on('update', (update, origin) => {
      mark()
      if (origin !== provider) room.updates.push(update)
    })
    provider.awareness.on('update', mark)
    provider.awareness.setLocalStateField('user', { name: client.name })
    room.clients.push(client)
    all.push(client)
    log(`${client.name} joins ${room.name} as ${doc.clientID}`)
    return client
  }

  const fail = (what, detail) => {
    throw new Failure(`${what}${detail ? `\n    ${detail}` : ''}`)
  }

  const alive = () => all.filter(c => c.alive)
  const synced = c => c.provider.wsconnected && c.provider.synced

  const randomString = () => Array.from({ length: 1 + r.int(5) }, () => r.pick(letters)).join('')
  const randomValue = () => r.weighted([[3, () => r.int(1000)], [3, randomString], [1, () => r.chance(0.5)], [1, () => null],
    [1, () => ({ k: r.int(10), s: randomString() })], [1, () => [r.int(10), randomString()]]])()

  const nested = () => {
    if (r.chance(0.5)) {
      const map = new Y.Map()
      map.set('v', randomValue())
      return map
    }
    const array = new Y.Array()
    array.insert(0, [randomValue(), randomValue()])
    return array
  }

  const textEdit = text => {
    const length = text.length
    const kind = r.weighted([[5, 'insert'], [3, 'delete'], [2, 'format']])
    if (kind === 'insert' || length === 0) {
      const at = r.int(length + 1)
      const attributes = r.chance(0.3) ? { [r.pick(['bold', 'italic'])]: true } : undefined
      text.insert(at, randomString(), attributes)
    } else if (kind === 'delete') {
      const at = r.int(length)
      text.delete(at, 1 + r.int(Math.min(4, length - at)))
    } else {
      const at = r.int(length)
      text.format(at, 1 + r.int(length - at), { [r.pick(['bold', 'italic'])]: r.chance(0.7) ? true : null })
    }
  }

  const arrayEdit = array => {
    if (array.length === 0 || r.chance(0.6)) {
      const items = Array.from({ length: 1 + r.int(3) }, () => r.chance(0.15) ? nested() : randomValue())
      array.insert(r.int(array.length + 1), items)
    } else {
      const at = r.int(array.length)
      array.delete(at, 1 + r.int(Math.min(3, array.length - at)))
    }
  }

  const mapEdit = map => {
    const keys = [...map.keys()]
    if (keys.length > 0 && r.chance(0.25)) {
      map.delete(r.pick(keys))
    } else {
      map.set(`k${r.int(8)}`, r.chance(0.25) ? nested() : randomValue())
    }
  }

  // An edit of a type nested in the root map or array, if there is one.
  const nestedEdit = doc => {
    const candidates = [...doc.getMap('map').values(), ...doc.getArray('array').toArray()]
      .filter(v => v instanceof Y.Map || v instanceof Y.Array)
    if (candidates.length === 0) return false
    const target = r.pick(candidates)
    if (target instanceof Y.Map) mapEdit(target)
    else arrayEdit(target)
    return true
  }

  const edit = doc => {
    const kind = r.weighted([[4, 'text'], [3, 'map'], [3, 'array'], [2, 'nested']])
    if (kind === 'text') textEdit(doc.getText('text'))
    else if (kind === 'map') mapEdit(doc.getMap('map'))
    else if (kind === 'array' || !nestedEdit(doc)) arrayEdit(doc.getArray('array'))
  }

  // A client leaves only once what it made has been written to an open socket (connected and synced: the server's
  // step 1 came before its step 2, and was answered), so the oracle may count every update it made.
  const leave = async client => {
    client.provider.connect()
    await waitFor(`${client.name} synced before leaving`, () => synced(client), 10000)
      .catch(() => fail(`${client.name} did not sync within 10 s before leaving`))
    client.provider.destroy()
    client.alive = false
    log(`${client.name} leaves`)
  }

  const act = async () => {
    const clients = alive()
    const client = r.pick(clients)
    const { doc, provider } = client
    const action = r.weighted([
      [30, 'edit'], [8, 'transaction'], [4, 'awareness'], [4, 'disconnect'], [5, 'connect'], [3, 'drop'],
      [2, 'replace'], [lateCount > 0 ? 6 : 0, 'join'], [6, 'wait']
    ])
    switch (action) {
      case 'edit':
        doc.transact(() => edit(doc), 'local')
        break
      case 'transaction': {
        const n = 2 + r.int(4)
        doc.transact(() => { for (let i = 0; i < n; i++) edit(doc) }, 'local')
        break
      }
      case 'awareness':
        provider.awareness.setLocalStateField('cursor', ++client.cursor)
        break
      case 'disconnect':
        log(`${client.name} disconnects`)
        provider.disconnect()
        break
      case 'connect':
        log(`${client.name} connects`)
        provider.connect()
        break
      case 'drop':
        if (socketOf(provider) !== null) {
          log(`${client.name} drops`)
          socketOf(provider).terminate()
        }
        break
      case 'replace':
        if (client.room.clients.filter(c => c.alive).length > 1) {
          await leave(client)
          join(client.room)
        }
        break
      case 'join':
        lateCount--
        join(rooms[all.length % roomCount])
        break
      case 'wait':
        await sleep(r.int(40))
        break
    }
    return action
  }

  const quiesce = async () => {
    const quiet = 300
    const ms = await waitFor('quiescence: every client synced and no message for 300 ms',
      () => alive().every(synced) && Date.now() - lastActivity > quiet, 20000)
      .catch(() => fail('no quiescence within 20 s',
        `not synced: ${alive().filter(c => !synced(c)).map(c => c.name).join(', ') || 'none'}`))
    log(`quiet after ${ms} ms`)
  }

  const checkRoom = async room => {
    const clients = room.clients.filter(c => c.alive)
    const [first, ...rest] = clients
    const reference = contentOf(first.doc)
    const referenceVector = stateVectorOf(first.doc)
    for (const other of rest) {
      const d = firstDifference(referenceVector, stateVectorOf(other.doc), '/stateVector')
        ?? firstDifference(reference, contentOf(other.doc))
      if (d !== null) fail(`clients differ in ${room.name}: ${first.name} vs ${other.name}`, d)
    }

    const updates = room.updates.slice()
    for (let i = updates.length - 1; i > 0; i--) {
      const j = r.int(i + 1);
      [updates[i], updates[j]] = [updates[j], updates[i]]
    }
    const oracle = oracleOf(updates)
    const d = firstDifference(stateVectorOf(oracle), referenceVector, '/stateVector') ??
      firstDifference(contentOf(oracle), reference)
    if (d !== null) {
      if (process.env.CONVERGE_DUMP) {
        writeFileSync(`${process.env.CONVERGE_DUMP}/${room.name}.json`,
          JSON.stringify({ made: room.updates.map(u => Buffer.from(u).toString('base64')), shuffled: updates.map(u => Buffer.from(u).toString('base64')) }))
      }
      const pending = oracle.store.pendingStructs ? ` pending structs missing ${show(Object.fromEntries(oracle.store.pendingStructs.missing))};` : ''
      const ordered = oracleOf(room.updates)
      const inOrder = firstDifference(stateVectorOf(ordered), referenceVector) ?? firstDifference(contentOf(ordered), reference)
      fail(`the oracle (${updates.length} local updates, shuffled) differs from the clients in ${room.name}`,
        `oracle vs ${first.name}: ${d}\n   ${pending} applied in the order made, the oracle ${inOrder === null ? 'equals the clients' : `differs too: ${inOrder}`}`)
    }
    return { reference, referenceVector }
  }

  const checkAwareness = async room => {
    const clients = room.clients.filter(c => c.alive)
    const gone = room.clients.filter(c => !c.alive).map(c => c.doc.clientID)
    const expected = new Map(clients.map(c => [c.doc.clientID, canonical(c.provider.awareness.getLocalState())]))
    const mismatch = () => {
      for (const c of clients) {
        const states = c.provider.awareness.getStates()
        for (const [id, state] of expected) {
          const seen = states.get(id)
          if (seen === undefined) return `${c.name} does not see ${id}`
          const d = firstDifference(state, canonical(seen))
          if (d !== null) return `${c.name} sees an old state of ${id}: ${d}`
        }
        const extra = [...states.keys()].filter(id => !expected.has(id))
        if (extra.length > 0) {
          return `${c.name} still sees ${extra.join(', ')} (${extra.every(id => gone.includes(id)) ? 'closed clients' : 'unknown'})`
        }
      }
      return null
    }
    await waitFor('awareness', () => mismatch() === null, 2000)
      .catch(() => fail(`awareness differs in ${room.name} after 2 s`, mismatch()))
  }

  const checkFreshClient = async (room, reference, referenceVector) => {
    // One fresh client per provider in use: numbers 0 and 1 give a WebSocket and a SignalR one.
    for (const number of options.transport === 'mixed' ? [0, 1] : [0]) {
      const doc = new Y.Doc()
      const provider = newProvider(options.url, room.name, doc, options.transport === 'signalr' ? 1 : number, WebSocket)
      try {
        await waitFor('fresh client synced', () => provider.synced, 10000)
          .catch(() => fail(`a fresh client (${provider.constructor.name}) did not sync with ${room.name} within 10 s`))
        // The fresh doc adds no client of its own: it made no edit.
        const d = firstDifference(referenceVector, stateVectorOf(doc), '/stateVector') ?? firstDifference(reference, contentOf(doc))
        if (d !== null) fail(`a fresh client (${provider.constructor.name}) differs from the clients in ${room.name}`, d)
      } finally {
        provider.destroy()
      }
    }
  }

  const started = Date.now()
  try {
    for (let i = 0; i < clientCount - lateCount; i++) join(rooms[i % roomCount])
    for (step = 1; step <= stepCount; step++) {
      await act()
      await sleep(r.chance(0.7) ? 0 : r.int(15))
    }
    step = stepCount
    for (const c of alive()) c.provider.connect()
    await quiesce()
    const references = []
    for (const room of rooms) references.push(await checkRoom(room))
    // A client whose connection closed is removed by the server at the clock it had; its next state is seen again.
    for (const c of alive()) c.provider.awareness.setLocalStateField('final', true)
    for (const room of rooms) await checkAwareness(room)
    for (let i = 0; i < rooms.length; i++) await checkFreshClient(rooms[i], references[i].reference, references[i].referenceVector)
    return { seed, ok: true, ms: Date.now() - started, steps: stepCount, clients: all.length, rooms: roomCount }
  } catch (e) {
    return { seed, ok: false, ms: Date.now() - started, steps: stepCount, clients: all.length, rooms: roomCount, step, error: e }
  } finally {
    for (const c of alive()) c.provider.destroy()
  }
}

// Every provider adds an exit listener.
process.setMaxListeners(0)
const options = parseArgs(process.argv.slice(2))
if (options.transport !== 'websocket') {
  register('./share-modules.mjs', import.meta.url)
  ;({ SignalRProvider } = await import('../signalr/provider.mjs'))
}
const seeds = Array.from({ length: options.seeds }, (_, i) => options.first + i)
const results = []
const started = Date.now()
const worker = async () => {
  while (seeds.length > 0) {
    const seed = seeds.shift()
    const result = await runSeed(seed, options)
    results.push(result)
    if (result.ok) {
      if (options.verbose) console.log(`ok   ${options.name} seed ${seed} (${result.steps} steps, ${result.ms} ms)`)
      continue
    }
    const overrides = ['clients', 'rooms', 'steps'].filter(k => options[k] !== undefined).map(k => ` --${k} ${options[k]}`).join('')
    const unexpected = result.error instanceof Failure ? '' : ` (driver error)\n${result.error.stack}`
    console.log([
      `FAIL ${options.name} seed ${seed}: ${result.steps} steps, ${result.clients} clients, ${result.rooms} room(s), failed at step ${result.step}${unexpected}`,
      `    ${result.error.message}`,
      `    replay: TRANSPORTS=${options.transport} TARGETS=${options.name} interop/server/converge.sh 1 ${seed}${overrides}`,
      `        or: node interop/server/converge.mjs --url ${options.url} --first ${seed} --seeds 1 --parallel 1 --transport ${options.transport}${overrides} --verbose`
    ].join('\n'))
  }
}
await Promise.all(Array.from({ length: Math.max(1, options.parallel) }, worker))
const failed = results.filter(x => !x.ok)
console.log(`${options.name} (${options.transport}): ${results.length - failed.length}/${results.length} seeds converged ` +
  `(${options.first}..${options.first + options.seeds - 1}) in ${((Date.now() - started) / 1000).toFixed(1)} s` +
  (failed.length > 0 ? `; failed: ${failed.map(x => x.seed).sort((a, b) => a - b).join(' ')}` : ''))
// The providers' awareness timers would keep the process alive.
process.exit(failed.length > 0 ? 1 : 0)
