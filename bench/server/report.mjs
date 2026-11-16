// Renders the JSON of a results directory as the Markdown tables of README.md. Usage: node report.mjs <dir>
import fs from 'node:fs'
import path from 'node:path'

const dir = process.argv[2]
if (!dir) { console.error('usage: node report.mjs <results dir>'); process.exit(2) }
const read = name => { const f = path.join(dir, `${name}.json`); return fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : null }
const f = (v, d = 1) => v === null || v === undefined ? '-' : Number(v).toFixed(d)
const checks = r => r.valid ? 'ok' : `**not counted**: ${r.problems.join('; ')}`
const row = cells => `| ${cells.join(' | ')} |`
const out = []

const machine = m => `Machine: ${m.cpu}, ${m.logicalCores} logical cores, ${(m.memoryBytes / 2 ** 30).toFixed(0)} GiB, kernel ${m.kernel}, Node ${m.node}, .NET SDK ${m.dotnet}; server on cores ${m.serverCpus}, driver on cores ${m.driverCpus} (${m.driverWorkers} workers); load average at start ${m.loadAverage.map(x => x.toFixed(1)).join(' ')}.`

const fanout = read('fanout')
if (fanout) {
  out.push(`### Fan-out (${fanout.date.slice(0, 16)}Z)`, '', machine(fanout.machineBefore), '')
  out.push(row(['clients per room × rooms', 'rate per client', 'server', 'offered upd/s', 'delivered upd/s', 'latency p50 / p95 / p99 ms', 'server cores', 'server CPU µs per delivery', 'server waited for a core (s)', 'server RSS peak MiB', 'drain s', 'driver: busiest worker / loop delay p99 ms', 'checks']))
  out.push(row(Array(13).fill('---')))
  const runs = Object.entries(fanout.servers).flatMap(([kind, rs]) => rs.map(r => ({ kind, ...r })))
  runs.sort((a, b) => a.perRoom - b.perRoom || a.ratePerClient - b.ratePerClient || a.kind.localeCompare(b.kind))
  for (const r of runs) {
    out.push(row([`${r.perRoom} × ${r.rooms}`, `${r.ratePerClient}/s`, r.kind, f(r.offeredUpdatesPerS, 0), f(r.deliveredUpdatesPerS, 0),
      `${f(r.latency.p50Ms)} / ${f(r.latency.p95Ms)} / ${f(r.latency.p99Ms)}`, f(r.server.cores, 2), f(r.server.cpuUsPerDelivery), f(r.server.runQueueWaitS),
      f(r.server.rssPeakMiB, 0), f(r.drainS), `${f(r.driver.busiestWorkerCore * 100, 0)}% / ${f(r.driver.loopDelayP99Ms)}`, checks(r)]))
  }
  out.push('')
}

const join = read('join')
if (join) {
  const any = Object.values(join.servers)[0]
  out.push(`### Join cost (${join.date.slice(0, 16)}Z)`, '', machine(join.machineBefore), '',
    `Document: ${(any.stateBytes / 1e6).toFixed(2)} MB state (v1), ${any.textChars} characters of text; the stale client holds the state after 90% of the edits.`, '')
  out.push(row(['client', 'joins at once', 'server', 'step 2 bytes', 'connect → synced p50 / p95 / max ms', 'server CPU ms per join', 'server waited for a core (s)', 'checks']))
  out.push(row(Array(8).fill('---')))
  const runs = Object.entries(join.servers).flatMap(([kind, j]) => j.runs.map(r => ({ kind, ...r })))
  runs.sort((a, b) => a.mode.localeCompare(b.mode) || a.joins - b.joins || a.kind.localeCompare(b.kind))
  for (const r of runs) {
    out.push(row([r.mode, `${r.joins} (× ${r.repeat})`, r.kind, f(r.step2Bytes, 0), `${f(r.syncedMs.p50Ms)} / ${f(r.syncedMs.p95Ms)} / ${f(r.syncedMs.maxMs)}`, f(r.serverCpuMsPerJoin), f(r.serverRunQueueWaitS), checks(r)]))
  }
  out.push('')
}

const memory = read('memory')
if (memory) {
  const any = Object.values(memory.servers)[0]
  out.push(`### Memory per room (${memory.date.slice(0, 16)}Z)`, '', machine(memory.machineBefore), '',
    `${any.rooms} rooms, each loaded with the first ${any.roomTxns} edits of the trace (${any.roomStateBytes} bytes of state, ${any.roomTextChars} characters), then ${any.editorsPerRoom} clients per room typing ${any.ratePerEditor}/s each for ${any.durationS} s. Heap: the managed heap after a full compacting GC (Ycs), V8's used heap plus external memory after a full GC (Node).`, '')
  out.push(row(['server', 'empty: RSS / heap MiB', 'per room built: RSS / heap KiB', 'per room after editing: RSS / heap KiB', 'editing latency p50 / p99 ms', 'checks']))
  out.push(row(Array(6).fill('---')))
  for (const [kind, r] of Object.entries(memory.servers)) {
    const heap = m => (m.managedBytes ?? m.heapUsedBytes + m.externalBytes) / 2 ** 20
    out.push(row([kind, `${f(r.empty.rssMiB)} / ${f(heap(r.empty))}`, `${f(r.perRoomKiB.rssBuilt, 0)} / ${f(r.perRoomKiB.heapBuilt, 0)}`, `${f(r.perRoomKiB.rssEdited, 0)} / ${f(r.perRoomKiB.heapEdited, 0)}`,
      `${f(r.editing.latency.p50Ms)} / ${f(r.editing.latency.p99Ms)}`, checks(r)]))
  }
  out.push('')
}
console.log(out.join('\n'))
