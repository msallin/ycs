// A load worker: many raw y-protocols clients on one thread. The main thread (bench.mjs) drives it by messages; each
// request is answered by one message with the same `op`.
import { parentPort, workerData } from 'node:worker_threads'
import { monitorEventLoopDelay } from 'node:perf_hooks'
import * as Y from 'yjs'
import WebSocket from 'ws'
import { step1Message, emptyStateVector, parseSync, firstStruct, syncStep2, syncUpdate, typingMessages, updateMessage } from './proto.mjs'

// Time in units of 10 µs since the run's base (a process-wide monotonic clock, the same in every worker): an Int32
// holds about 6 hours of it.
const base = BigInt(workerData.base)
const now = () => Number((process.hrtime.bigint() - base) / 10000n)

// sendTimes[slot * maxPerClient + clock] is when the client in that slot sent its update with that clock; 0 is not sent.
const sendTimes = new Int32Array(workerData.sendTimes)
const maxPerClient = workerData.maxPerClient
const firstClientId = workerData.firstClientId

const open = (url, room) => new Promise((resolve, reject) => {
  const ws = new WebSocket(`${url}/${encodeURIComponent(room)}`, { perMessageDeflate: false })
  ws.binaryType = 'nodebuffer'
  ws.once('open', () => resolve(ws))
  ws.once('error', reject)
})

class Samples {
  constructor () { this.data = new Int32Array(1 << 16); this.length = 0 }
  push (v) {
    if (this.length === this.data.length) { const d = new Int32Array(this.data.length * 2); d.set(this.data); this.data = d }
    this.data[this.length++] = v
  }
}

let clients = []
let window = { from: Infinity, to: Infinity }
let latencies = new Samples()
let typingTimer = null
let cpuAtStart = null
let loopDelay = null

const onMessage = (client, data) => {
  const sync = parseSync(data)
  if (sync === null || sync.subtype !== syncUpdate) return
  const at = now()
  const { client: id, clock } = firstStruct(sync.payload)
  if (id === client.clientId) { client.echoes++; return }
  client.received++
  const sentAt = Atomics.load(sendTimes, (id - firstClientId) * maxPerClient + clock)
  if (sentAt >= window.from && sentAt < window.to) {
    client.receivedInWindow++
    latencies.push(at - sentAt)
  }
}

const connect = async ({ url, list, batch = 50 }) => {
  clients = list.map(c => ({ ...c, ws: null, sent: 0, received: 0, receivedInWindow: 0, echoes: 0, closedEarly: false, messages: c.typing ? typingMessages(c.clientId, c.index, c.typing) : null }))
  for (let i = 0; i < clients.length; i += batch) {
    await Promise.all(clients.slice(i, i + batch).map(async c => {
      c.ws = await open(url, c.room)
      c.ws.on('message', data => onMessage(c, data))
      c.ws.on('close', () => { if (!c.closing) c.closedEarly = true })
      c.ws.send(step1Message(emptyStateVector))
    }))
  }
  return { connected: clients.length }
}

// Every typing client sends one message per 1/rate s from `startAt` (a random phase each) for `durationMs`;
// latencies count for messages sent within [windowFrom, windowTo).
const type = ({ startAt, durationMs, rate, windowFrom, windowTo, tickMs = 2 }) => new Promise(resolve => {
  window = { from: windowFrom, to: windowTo }
  const interval = 100000 / rate
  const endAt = startAt + durationMs * 100
  const typists = clients.filter(c => c.messages)
  for (const c of typists) c.next = startAt + Math.random() * interval
  const tick = () => {
    const t = now()
    if (cpuAtStart === null && t >= startAt) {
      cpuAtStart = process.threadCpuUsage()
      loopDelay = monitorEventLoopDelay({ resolution: 10 })
      loopDelay.enable()
    }
    for (const c of typists) {
      while (c.next <= t && c.next < endAt && c.sent < c.messages.length && !c.closedEarly) {
        const slot = (c.clientId - firstClientId) * maxPerClient + c.sent
        Atomics.store(sendTimes, slot, Math.max(1, now()))
        c.ws.send(c.messages[c.sent++])
        c.next += interval
      }
    }
    if (t >= endAt) {
      clearInterval(typingTimer)
      resolve({ sent: typists.map(c => [c.index, c.sent]) })
    }
  }
  typingTimer = setInterval(tick, tickMs)
})

const report = () => {
  const cpu = cpuAtStart ? process.threadCpuUsage(cpuAtStart) : { user: 0, system: 0 }
  loopDelay?.disable()
  const out = {
    clients: clients.map(c => ({ index: c.index, room: c.room, received: c.received, receivedInWindow: c.receivedInWindow, echoes: c.echoes, closedEarly: c.closedEarly })),
    latencies: latencies.data.slice(0, latencies.length),
    cpuSeconds: (cpu.user + cpu.system) / 1e6,
    loopDelayP99Ms: loopDelay ? loopDelay.percentile(99) / 1e6 : 0,
    loopDelayMaxMs: loopDelay ? loopDelay.max / 1e6 : 0
  }
  latencies = new Samples()
  cpuAtStart = null
  loopDelay = null
  return out
}

const progress = () => ({ received: clients.reduce((s, c) => s + c.received, 0), closedEarly: clients.filter(c => c.closedEarly).length })

const close = async () => {
  await Promise.all(clients.map(c => new Promise(resolve => {
    c.closing = true
    if (c.ws.readyState === WebSocket.CLOSED) return resolve()
    c.ws.once('close', resolve)
    c.ws.close()
  })))
  clients = []
  return {}
}

// Fresh or stale clients join at once: connect, send step 1 with their state vector, wait for step 2.
// `startAt` lines up the workers. A join with `verify` applies its step 2 to `verify.state` and compares the text.
const join = async ({ url, list, startAt }) => {
  while (now() < startAt) await new Promise(resolve => setTimeout(resolve, 1))
  const cpu0 = process.threadCpuUsage()
  const results = await Promise.all(list.map(j => new Promise((resolve, reject) => {
    const t0 = now()
    const ws = new WebSocket(`${url}/${encodeURIComponent(j.room)}`, { perMessageDeflate: false })
    let opened = 0
    ws.once('open', () => { opened = now(); ws.send(step1Message(j.stateVector)) })
    ws.once('error', reject)
    ws.on('message', data => {
      const sync = parseSync(data)
      if (sync === null || sync.subtype !== syncStep2) return
      const done = now()
      let ok = null
      if (j.verify) {
        const doc = new Y.Doc()
        if (j.verify.state) Y.applyUpdate(doc, j.verify.state)
        Y.applyUpdate(doc, sync.payload)
        ok = doc.getText('text').toString() === j.verify.text
        doc.destroy()
      }
      ws.close()
      resolve({ connectMs: (opened - t0) / 100, syncedMs: (done - t0) / 100, bytes: sync.payload.length, ok })
    })
  })))
  const cpu = process.threadCpuUsage(cpu0)
  return { results, cpuSeconds: (cpu.user + cpu.system) / 1e6 }
}

// Puts a state into each room: sends it as an update, then a step 1 with its state vector, and waits for the step 2
// (applied, since a connection's messages are handled in order).
const load = async ({ url, rooms, state, stateVector, inFlight = 8 }) => {
  const message = updateMessage(state)
  const step1 = step1Message(stateVector)
  for (let i = 0; i < rooms.length; i += inFlight) {
    await Promise.all(rooms.slice(i, i + inFlight).map(room => new Promise((resolve, reject) => {
      const ws = new WebSocket(`${url}/${encodeURIComponent(room)}`, { perMessageDeflate: false })
      ws.once('open', () => { ws.send(message); ws.send(step1) })
      ws.once('error', reject)
      ws.on('message', data => {
        const sync = parseSync(data)
        if (sync !== null && sync.subtype === syncStep2) { ws.once('close', resolve); ws.close() }
      })
    })))
  }
  return { loaded: rooms.length }
}

const ops = { connect, type, report, progress, close, join, load }
parentPort.on('message', async ({ op, args }) => {
  try {
    const result = await ops[op](args)
    const transfer = result?.latencies ? [result.latencies.buffer] : []
    parentPort.postMessage({ op, result }, transfer)
  } catch (e) {
    parentPort.postMessage({ op, error: String(e?.stack ?? e) })
  }
})
