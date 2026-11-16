// Starting the two servers on a free port, pinned to their cores, and reading their CPU time and resident set.
import { spawn, execFileSync } from 'node:child_process'
import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import { setTimeout as sleep } from 'node:timers/promises'

const here = path.dirname(new URL(import.meta.url).pathname)
const benchDir = path.resolve(here, '..')
const hostDll = path.join(benchDir, 'host/bin/Release/net11.0/Ycs.Server.BenchHost.dll')

export const hasTaskset = (() => { try { execFileSync('taskset', ['-p', String(process.pid)], { stdio: 'ignore' }); return true } catch { return false } })()
const clockTicks = Number(execFileSync('getconf', ['CLK_TCK']).toString().trim())

const freePort = () => new Promise((resolve, reject) => {
  const s = net.createServer().listen(0, '127.0.0.1', () => { const { port } = s.address(); s.close(() => resolve(port)) })
  s.on('error', reject)
})

const listening = async (port, child) => {
  for (let i = 0; i < 300; i++) {
    if (child.exitCode !== null) throw new Error(`the server exited with ${child.exitCode}`)
    const ok = await new Promise(resolve => {
      const s = net.connect(port, '127.0.0.1', () => { s.destroy(); resolve(true) })
      s.on('error', () => resolve(false))
    })
    if (ok) return
    await sleep(100)
  }
  throw new Error(`the server did not listen on ${port}`)
}

const command = (kind, port) => kind === 'ycs'
  ? { cmd: 'dotnet', args: [hostDll, '--urls', `http://127.0.0.1:${port}`, '--Logging:LogLevel:Default=Warning'], env: {} }
  : { cmd: 'node', args: ['--expose-gc', path.join(benchDir, 'node-server.mjs')], env: { HOST: '127.0.0.1', PORT: String(port) } }

export const startServer = async (kind, cpus) => {
  const port = await freePort()
  const { cmd, args, env } = command(kind, port)
  const [file, argv] = hasTaskset && cpus ? ['taskset', ['-c', cpus, cmd, ...args]] : [cmd, args]
  const child = spawn(file, argv, { env: { ...process.env, ...env }, stdio: ['ignore', 'ignore', 'inherit'] })
  await listening(port, child)
  return {
    kind,
    pid: child.pid,
    url: `ws://127.0.0.1:${port}/ws`,
    async memory () { return (await fetch(`http://127.0.0.1:${port}/bench/memory`)).json() },
    async stop () {
      if (child.exitCode !== null) return
      const exited = new Promise(resolve => child.once('exit', resolve))
      child.kill('SIGTERM')
      await Promise.race([exited, sleep(10000).then(() => child.kill('SIGKILL'))])
    }
  }
}

// CPU seconds (user + system, all threads) and resident set in bytes of a process.
export const procStats = pid => {
  const stat = fs.readFileSync(`/proc/${pid}/stat`, 'utf8')
  const fields = stat.slice(stat.lastIndexOf(')') + 2).split(' ')
  const cpuSeconds = (Number(fields[11]) + Number(fields[12])) / clockTicks
  const rss = Number(/VmRSS:\s+(\d+) kB/.exec(fs.readFileSync(`/proc/${pid}/status`, 'utf8'))[1]) * 1024
  return { cpuSeconds, rss, waitSeconds: runQueueSeconds(pid) }
}

// Time the process's threads spent runnable but not running (waiting for a core), summed: on a busy machine it shows
// how much of a server's slowness is the machine's.
const runQueueSeconds = pid => {
  let ns = 0
  for (const task of fs.readdirSync(`/proc/${pid}/task`)) {
    try { ns += Number(fs.readFileSync(`/proc/${pid}/task/${task}/schedstat`, 'utf8').split(' ')[1]) } catch {}
  }
  return ns / 1e9
}

// Samples a process every `ms` until stopped: CPU used over the window, peak and last resident set.
export const sampler = (pid, ms = 500) => {
  const start = procStats(pid)
  const t0 = performance.now()
  let peak = start.rss
  let last = start
  const timer = setInterval(() => { try { last = procStats(pid); peak = Math.max(peak, last.rss) } catch {} }, ms)
  return {
    stop () {
      clearInterval(timer)
      try { last = procStats(pid); peak = Math.max(peak, last.rss) } catch {}
      const seconds = (performance.now() - t0) / 1000
      const cpuSeconds = last.cpuSeconds - start.cpuSeconds
      return { seconds, cpuSeconds, cores: cpuSeconds / seconds, waitSeconds: last.waitSeconds - start.waitSeconds, rssPeak: peak, rssEnd: last.rss }
    }
  }
}
