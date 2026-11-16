// Two y-websocket providers against a running server: text, awareness, and awareness removal on leave.
// Usage: node smoke.mjs ws://127.0.0.1:5000/ws   (or YCS_SERVER_URL). Starts nothing itself; smoke.sh does.
import * as Y from 'yjs'
import { WebsocketProvider } from 'y-websocket'
import WebSocket from 'ws'

const url = process.argv[2] ?? process.env.YCS_SERVER_URL
if (!url) {
  console.error('usage: node smoke.mjs <ws://host:port/ws>')
  process.exit(2)
}

const room = `smoke-${process.pid}-${Date.now()}`

const connect = () => {
  const doc = new Y.Doc()
  const provider = new WebsocketProvider(url, room, doc, { WebSocketPolyfill: WebSocket, disableBc: true })
  return { doc, provider }
}

const waitFor = (what, condition, ms) => new Promise((resolve, reject) => {
  const started = Date.now()
  const timer = setInterval(() => {
    if (condition()) {
      clearInterval(timer)
      resolve(Date.now() - started)
    } else if (Date.now() - started > ms) {
      clearInterval(timer)
      reject(new Error(`timed out after ${ms} ms: ${what}`))
    }
  }, 10)
})

const check = async (name, run) => {
  const ms = await run()
  console.log(`ok   ${name} (${ms} ms)`)
}

const providers = []
try {
  const a = connect()
  const b = connect()
  providers.push(a.provider, b.provider)
  await check('both providers sync', () => waitFor('sync', () => a.provider.synced && b.provider.synced, 5000))

  await check('a text typed in one appears in the other', () => {
    a.doc.getText('text').insert(0, 'hello from a')
    return waitFor('text', () => b.doc.getText('text').toString() === 'hello from a', 5000)
  })

  await check('a local awareness state of one appears in the other', () => {
    a.provider.awareness.setLocalStateField('user', { name: 'a' })
    return waitFor('awareness', () => b.provider.awareness.getStates().get(a.doc.clientID)?.user?.name === 'a', 5000)
  })

  await check('a destroyed provider\'s state is removed in the other within 2 s', () => {
    a.provider.destroy()
    return waitFor('removal on destroy', () => !b.provider.awareness.getStates().has(a.doc.clientID), 2000)
  })

  // A dropped connection sends no awareness removal: the server's close does it.
  const c = connect()
  providers.push(c.provider)
  await waitFor('sync of c', () => c.provider.synced, 5000)
  c.provider.awareness.setLocalStateField('user', { name: 'c' })
  await waitFor('awareness of c', () => b.provider.awareness.getStates().has(c.doc.clientID), 5000)
  await check('a dropped connection\'s state is removed by the server within 2 s', () => {
    c.provider.shouldConnect = false
    c.provider.ws.terminate()
    return waitFor('removal on drop', () => !b.provider.awareness.getStates().has(c.doc.clientID), 2000)
  })
  console.log('smoke passed')
} catch (e) {
  console.error(`FAIL ${e.message}`)
  process.exitCode = 1
} finally {
  for (const provider of providers) {
    provider.destroy()
  }
  // The providers' awareness timers keep the process alive.
  setTimeout(() => process.exit(), 100)
}
