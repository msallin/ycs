// The load workers and their request/answer messages.
import { Worker } from 'node:worker_threads'

export const createPool = ({ size, base, sendTimes, maxPerClient, firstClientId }) => {
  const workers = Array.from({ length: size }, () => {
    const w = new Worker(new URL('./worker.mjs', import.meta.url), { workerData: { base: String(base), sendTimes, maxPerClient, firstClientId } })
    w.pending = []
    w.on('message', ({ op, result, error }) => {
      const p = w.pending.shift()
      if (p.op !== op) p.reject(new Error(`answer ${op} for request ${p.op}`))
      else if (error) p.reject(new Error(`worker: ${error}`))
      else p.resolve(result)
    })
    w.on('error', e => { for (const p of w.pending) p.reject(e) })
    return w
  })
  const request = (w, op, args) => new Promise((resolve, reject) => { w.pending.push({ op, resolve, reject }); w.postMessage({ op, args }) })
  return {
    size,
    // argsFor(i) gives worker i's arguments; returns every worker's answer.
    all: (op, argsFor = () => ({})) => Promise.all(workers.map((w, i) => request(w, op, argsFor(i)))),
    terminate: () => Promise.all(workers.map(w => w.terminate()))
  }
}
