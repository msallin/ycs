// The sync server benchmark: the Ycs server against @y/websocket-server, each started fresh per scenario.
// Usage: node bench.mjs <fanout|join|memory|all> [--option value ...]   (run.sh builds, pins and calls it; README.md)
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { setTimeout as sleep } from 'node:timers/promises'
import * as Y from 'yjs'
import { createPool } from './lib/pool.mjs'
import { startServer, sampler, procStats, hasTaskset } from './lib/servers.mjs'
import { typingState, emptyStateVector } from './lib/proto.mjs'
import { loadTrace, buildHistory, cachedHistory } from './lib/trace.mjs'

const here = path.dirname(new URL(import.meta.url).pathname)

const parseArgs = argv => {
  const opts = { _: [] }
  for (let i = 0; i < argv.length; i++) {
    if (argv[i].startsWith('--')) opts[argv[i].slice(2)] = argv[++i]
    else opts._.push(argv[i])
  }
  return opts
}
const opts = parseArgs(process.argv.slice(2))
const list = (v, d) => (v ?? d).split(',').filter(Boolean)
const num = (v, d) => v === undefined ? d : Number(v)

const scenario = opts._[0] ?? 'all'
const servers = list(opts.servers, 'ycs,node')
const serverCpus = opts['server-cpus'] ?? ''
const outDir = opts.out ?? path.join(here, 'results', new Date().toISOString().slice(0, 10))
const tracePath = opts.trace ?? path.join(here, '.trace/automerge-paper.json.gz')
const workerCount = num(opts.workers, os.availableParallelism())

const base = process.hrtime.bigint()
const now = () => Number((process.hrtime.bigint() - base) / 10000n)
const firstClientId = 1000

const sh = (cmd, args) => { try { return execFileSync(cmd, args).toString().trim() } catch { return null } }
const machine = () => ({
  cpu: /model name\s*:\s*(.*)/.exec(fs.readFileSync('/proc/cpuinfo', 'utf8'))?.[1],
  logicalCores: os.cpus().length,
  memoryBytes: os.totalmem(),
  kernel: os.release(),
  node: process.version,
  dotnet: sh('dotnet', ['--version']),
  serverCpus: hasTaskset ? serverCpus || 'all' : 'not pinned (no taskset)',
  driverCpus: sh('taskset', ['-cp', String(process.pid)])?.split(': ')[1] ?? 'not pinned',
  driverWorkers: workerCount,
  loadAverage: os.loadavg()
})

const percentile = (sorted, p) => sorted.length ? sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))] : null
const latencyStats = samples => {
  const s = Float64Array.from(samples).sort()
  return { count: s.length, p50Ms: percentile(s, 0.5), p95Ms: percentile(s, 0.95), p99Ms: percentile(s, 0.99), maxMs: s.length ? s[s.length - 1] : null }
}
const mib = b => b / 1048576

// A pool with the shared send-time table sized for `clients` clients of up to `perClient` updates each.
const pool = (clients, perClient) => {
  const sendTimes = new SharedArrayBuffer(Math.max(4, clients * perClient * 4))
  return createPool({ size: workerCount, base, sendTimes, maxPerClient: perClient, firstClientId })
}

// Rooms of typing clients: connect, type for `durationS`, wait until every update reached every other client of
// its room (or nothing arrived for `stallS`), report, close. Clients are spread over the workers round robin.
const typingRun = async ({ server, p, rooms, perRoom, rate, durationS, warmupS, stallS = 10 }) => {
  const perClient = Math.ceil(rate * durationS) + 1
  const clients = []
  for (const room of rooms) for (let k = 0; k < perRoom; k++) {
    const index = clients.length
    clients.push({ index, clientId: firstClientId + index, room, typing: perClient })
  }
  const byWorker = i => clients.filter(c => c.index % p.size === i)
  await p.all('connect', i => ({ url: server.url, list: byWorker(i) }))

  const driverCpu0 = process.cpuUsage()
  const serverUse = sampler(server.pid)
  const startAt = now() + 50000
  const windowFrom = startAt + warmupS * 100000
  const windowTo = startAt + durationS * 100000
  const typed = await p.all('type', () => ({ startAt, durationMs: durationS * 1000, rate, windowFrom, windowTo }))
  const typedAt = now()
  const sent = new Map(typed.flatMap(t => t.sent))

  const sentInRoom = new Map()
  for (const c of clients) sentInRoom.set(c.room, (sentInRoom.get(c.room) ?? 0) + sent.get(c.index))
  const expected = clients.reduce((s, c) => s + sentInRoom.get(c.room) - sent.get(c.index), 0)
  let received = 0
  let closedEarly = 0
  let lastChange = now()
  while (true) {
    const progress = await p.all('progress')
    const r = progress.reduce((s, x) => s + x.received, 0)
    closedEarly = progress.reduce((s, x) => s + x.closedEarly, 0)
    if (r !== received) { received = r; lastChange = now() }
    if (received >= expected || now() - lastChange > stallS * 100000) break
    await sleep(100)
  }
  const drainedAt = now()
  const server_ = serverUse.stop()
  const driverCpu = process.cpuUsage(driverCpu0)
  const reports = await p.all('report')
  await p.all('close')

  const latencies = []
  for (const r of reports) for (const v of r.latencies) latencies.push(v / 100)
  const echoes = reports.reduce((s, r) => s + r.clients.reduce((t, c) => t + c.echoes, 0), 0)
  const totalSent = [...sent.values()].reduce((a, b) => a + b, 0)
  const typingS = (typedAt - startAt) / 100000
  const drainS = (drainedAt - typedAt) / 100000
  const wallS = (drainedAt - startAt) / 100000
  return {
    clients, sent,
    result: {
      updatesSent: totalSent,
      offeredUpdatesPerS: clients.length * rate,
      deliveriesExpected: expected,
      deliveries: received,
      echoes,
      closedEarly,
      deliveredUpdatesPerS: received / Math.max(1, perRoom - 1) / (typingS + drainS),
      deliveriesPerS: received / (typingS + drainS),
      typingS, drainS,
      latency: latencyStats(latencies),
      server: { cores: server_.cpuSeconds / wallS, cpuSeconds: server_.cpuSeconds, cpuUsPerDelivery: server_.cpuSeconds * 1e6 / Math.max(1, received), runQueueWaitS: server_.waitSeconds, rssPeakMiB: mib(server_.rssPeak), rssEndMiB: mib(server_.rssEnd) },
      driver: {
        processCores: (driverCpu.user + driverCpu.system) / 1e6 / wallS,
        busiestWorkerCore: Math.max(...reports.map(r => r.cpuSeconds)) / wallS,
        loopDelayP99Ms: Math.max(...reports.map(r => r.loopDelayP99Ms)),
        loopDelayMaxMs: Math.max(...reports.map(r => r.loopDelayMaxMs))
      }
    }
  }
}

// The text each room must hold: `initial` (a state or null) plus what each of its clients typed.
const expectedTexts = (clients, sent, initial) => {
  const texts = new Map()
  const byRoom = Map.groupBy(clients, c => c.room)
  for (const [room, members] of byRoom) {
    const doc = new Y.Doc()
    if (initial) Y.applyUpdate(doc, initial)
    for (const c of members) Y.applyUpdate(doc, typingState(c.clientId, c.index, sent.get(c.index)))
    texts.set(room, doc.getText('text').toString())
    doc.destroy()
  }
  return texts
}

// A fresh client per room syncs and compares; returns the rooms that differ.
const verifyRooms = async (p, server, texts) => {
  const rooms = [...texts.keys()]
  const answers = await p.all('join', i => ({
    url: server.url,
    startAt: 0,
    list: rooms.filter((_, k) => k % p.size === i).map(room => ({ room, stateVector: emptyStateVector, verify: { text: texts.get(room) } }))
  }))
  return answers.flatMap(a => a.results).filter(r => !r.ok).length
}

const fanout = async kind => {
  const sizes = list(opts.clients, '10,100,1000').map(Number)
  const total = num(opts.total, 1000)
  const durationS = num(opts.duration, 20)
  const warmupS = num(opts.warmup, 5)
  const defaultRate = c => c <= 10 ? 4 : c <= 100 ? 1 : 0.1
  const runs = []
  for (const perRoom of sizes) for (const rate of opts.rate ? list(opts.rate).map(Number) : [defaultRate(perRoom)]) {
    const roomCount = Math.max(1, Math.round(total / perRoom))
    const server = await startServer(kind, serverCpus)
    const p = pool(roomCount * perRoom, Math.ceil(rate * durationS) + 1)
    try {
      const rooms = Array.from({ length: roomCount }, (_, r) => `fanout-${perRoom}-${r}`)
      const { clients, sent, result } = await typingRun({ server, p, rooms, perRoom, rate, durationS, warmupS })
      const wrongRooms = await verifyRooms(p, server, expectedTexts(clients, sent, null))
      const problems = []
      if (wrongRooms) problems.push(`${wrongRooms} rooms differ from the expected text`)
      if (result.deliveries < result.deliveriesExpected) problems.push(`${result.deliveriesExpected - result.deliveries} deliveries missing`)
      if (result.closedEarly) problems.push(`${result.closedEarly} connections closed by the server`)
      runs.push({ perRoom, rooms: roomCount, ratePerClient: rate, durationS, warmupS, ...result, valid: problems.length === 0, problems })
      log(`${kind} fanout C=${perRoom} R=${roomCount} rate=${rate}/s: ${fmtFanout(runs.at(-1))}`)
    } finally {
      await p.terminate()
      await server.stop()
    }
  }
  return runs
}

const fmtFanout = r => `${r.deliveredUpdatesPerS.toFixed(0)} upd/s (offered ${r.offeredUpdatesPerS}), p50/p99 ${r.latency.p50Ms?.toFixed(2)}/${r.latency.p99Ms?.toFixed(2)} ms, server ${r.server.cores.toFixed(2)} cores, ${r.server.cpuUsPerDelivery.toFixed(1)} µs/delivery (waited ${r.server.runQueueWaitS.toFixed(1)} s for a core) ${r.server.rssPeakMiB.toFixed(0)} MiB, driver busiest worker ${(r.driver.busiestWorkerCore * 100).toFixed(0)}%${r.valid ? '' : ' INVALID: ' + r.problems.join('; ')}`

let history = null
const joinHistory = () => {
  if (!history) {
    const t0 = Date.now()
    history = cachedHistory(tracePath, path.dirname(tracePath), { copies: num(opts.copies, 5), staleFraction: num(opts['stale-fraction'], 0.9) })
    log(`history: ${(history.state.length / 1e6).toFixed(2)} MB state, ${history.text.length} chars, stale diff from ${(history.stale.state.length / 1e6).toFixed(2)} MB, built in ${Date.now() - t0} ms`)
  }
  return history
}

const join = async kind => {
  const h = joinHistory()
  const batches = list(opts.joins, '1,10,100').map(Number)
  const repeat = num(opts.repeat, 3)
  const server = await startServer(kind, serverCpus)
  const p = pool(1, 1)
  const runs = []
  try {
    const room = 'join'
    const rssBefore = procStats(server.pid).rss
    const loadUse = sampler(server.pid)
    const t0 = now()
    await p.all('load', i => ({ url: server.url, rooms: i === 0 ? [room] : [], state: h.state, stateVector: Y.encodeStateVectorFromUpdate(h.state) }))
    const loaded = { ms: (now() - t0) / 100, ...loadUse.stop() }
    const mem = await server.memory()
    const modes = {
      fresh: { stateVector: emptyStateVector, verify: { text: h.text } },
      stale: { stateVector: h.stale.stateVector, verify: { state: h.stale.state, text: h.text } }
    }
    const batch = async (mode, n) => {
      const { stateVector, verify } = modes[mode]
      const use = sampler(server.pid, 100)
      const startAt = now() + 20000
      const answers = await p.all('join', i => {
        const mine = Array.from({ length: n }, (_, k) => k).filter(k => k % p.size === i)
        return { url: server.url, startAt, list: mine.map((k, j) => ({ room, stateVector, verify: j === 0 ? verify : null })) }
      })
      const s = use.stop()
      const results = answers.flatMap(a => a.results)
      return { results, server: s, driverCpuSeconds: answers.reduce((x, a) => x + a.cpuSeconds, 0), busiestWorkerCpuSeconds: Math.max(...answers.map(a => a.cpuSeconds)) }
    }
    for (let w = 0; w < num(opts['warmup-joins'], 10); w++) { await batch('fresh', 1); await batch('stale', 1) }
    for (const mode of Object.keys(modes)) for (const n of batches) {
      const samples = []
      let serverCpuSeconds = 0
      let wrong = 0
      let bytes = 0
      let busiest = 0
      let waitS = 0
      for (let r = 0; r < repeat; r++) {
        const b = await batch(mode, n)
        samples.push(...b.results.map(x => x.syncedMs))
        wrong += b.results.filter(x => x.ok === false).length
        bytes = b.results[0].bytes
        serverCpuSeconds += b.server.cpuSeconds
        waitS += b.server.waitSeconds
        busiest = Math.max(busiest, b.busiestWorkerCpuSeconds / b.server.seconds)
      }
      const stats = latencyStats(samples)
      const run = { mode, joins: n, repeat, step2Bytes: bytes, syncedMs: stats, serverCpuMsPerJoin: serverCpuSeconds * 1000 / (n * repeat), serverRunQueueWaitS: waitS, driverBusiestWorkerCore: busiest, valid: wrong === 0, problems: wrong ? [`${wrong} joins synced a different text`] : [] }
      runs.push(run)
      log(`${kind} join ${mode} x${n}: p50 ${stats.p50Ms.toFixed(1)} ms, max ${stats.maxMs.toFixed(1)} ms, ${run.serverCpuMsPerJoin.toFixed(1)} ms server CPU per join${run.valid ? '' : ' INVALID'}`)
    }
    return { stateBytes: h.state.length, textChars: h.text.length, staleDiffBase: h.stale.state.length, load: { ...loaded, rssDeltaMiB: mib(procStats(server.pid).rss - rssBefore) }, memoryAfterLoad: mem, runs }
  } finally {
    await p.terminate()
    await server.stop()
  }
}

const memory = async kind => {
  const roomCount = num(opts.rooms, 100)
  const txns = num(opts['room-txns'], 20000)
  const editors = num(opts.editors, 2)
  const rate = num(opts.rate, 2)
  const durationS = num(opts.duration, 120)
  const trace = loadTrace(tracePath)
  const roomDoc = buildHistory(trace, { txns, staleFraction: 1 })
  const server = await startServer(kind, serverCpus)
  const p = pool(roomCount * editors, Math.ceil(rate * durationS) + 1)
  try {
    const snapshot = async () => ({ rssMiB: mib(procStats(server.pid).rss), ...(await server.memory()) })
    const empty = await snapshot()
    const rooms = Array.from({ length: roomCount }, (_, r) => `memory-${r}`)
    await p.all('load', i => ({ url: server.url, rooms: rooms.filter((_, k) => k % p.size === i), state: roomDoc.state, stateVector: Y.encodeStateVectorFromUpdate(roomDoc.state) }))
    const built = await snapshot()
    const { clients, sent, result } = await typingRun({ server, p, rooms, perRoom: editors, rate, durationS, warmupS: 0 })
    const edited = await snapshot()
    const wrongRooms = await verifyRooms(p, server, expectedTexts(clients, sent, roomDoc.state))
    const problems = []
    if (wrongRooms) problems.push(`${wrongRooms} rooms differ from the expected text`)
    if (result.closedEarly) problems.push(`${result.closedEarly} connections closed by the server`)
    if (result.deliveries < result.deliveriesExpected) problems.push(`${result.deliveriesExpected - result.deliveries} deliveries missing`)
    const heap = m => mib(m.managedBytes ?? m.heapUsedBytes + m.externalBytes)
    const run = {
      rooms: roomCount, roomTxns: txns, roomStateBytes: roomDoc.state.length, roomTextChars: roomDoc.text.length,
      editorsPerRoom: editors, ratePerEditor: rate, durationS,
      empty, built, edited,
      perRoomKiB: {
        rssBuilt: (built.rssMiB - empty.rssMiB) * 1024 / roomCount,
        heapBuilt: (heap(built) - heap(empty)) * 1024 / roomCount,
        rssEdited: (edited.rssMiB - empty.rssMiB) * 1024 / roomCount,
        heapEdited: (heap(edited) - heap(empty)) * 1024 / roomCount
      },
      editing: result,
      valid: problems.length === 0,
      problems
    }
    log(`${kind} memory: per room ${run.perRoomKiB.rssBuilt.toFixed(0)} KiB RSS / ${run.perRoomKiB.heapBuilt.toFixed(0)} KiB heap built, ${run.perRoomKiB.rssEdited.toFixed(0)} / ${run.perRoomKiB.heapEdited.toFixed(0)} after ${durationS} s editing${run.valid ? '' : ' INVALID: ' + problems.join('; ')}`)
    return run
  } finally {
    await p.terminate()
    await server.stop()
  }
}

const log = s => console.error(`[${new Date().toISOString().slice(11, 19)}] ${s}`)

const scenarios = { fanout, join, memory }
const chosen = scenario === 'all' ? Object.keys(scenarios) : [scenario]
fs.mkdirSync(outDir, { recursive: true })
for (const name of chosen) {
  const out = { scenario: name, date: new Date().toISOString(), options: opts, machineBefore: machine(), servers: {} }
  for (const kind of servers) out.servers[kind] = await scenarios[name](kind)
  out.loadAverageAfter = os.loadavg()
  const file = path.join(outDir, `${name}.json`)
  fs.writeFileSync(file, JSON.stringify(out, null, 1))
  log(`wrote ${file}`)
}
