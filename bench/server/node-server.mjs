// The reference server, @y/websocket-server's src/server.js, plus one endpoint the benchmark reads between phases:
// /bench/memory (the V8 heap after a full GC; needs --expose-gc). HOST and PORT as for the original.
import http from 'node:http'
import { WebSocketServer } from 'ws'
import { setupWSConnection, docs } from '@y/websocket-server/utils'

const wss = new WebSocketServer({ noServer: true })
const host = process.env.HOST || 'localhost'
const port = parseInt(process.env.PORT || '1234')

const server = http.createServer((request, response) => {
  if (request.url === '/bench/memory') {
    globalThis.gc()
    const m = process.memoryUsage()
    response.writeHead(200, { 'Content-Type': 'application/json' })
    response.end(JSON.stringify({ heapUsedBytes: m.heapUsed, heapTotalBytes: m.heapTotal, externalBytes: m.external, arrayBuffersBytes: m.arrayBuffers, rooms: docs.size }))
    return
  }
  response.writeHead(200, { 'Content-Type': 'text/plain' })
  response.end('okay')
})

wss.on('connection', setupWSConnection)
server.on('upgrade', (request, socket, head) => {
  wss.handleUpgrade(request, socket, head, ws => wss.emit('connection', ws, request))
})
server.listen(port, host, () => console.log(`running at '${host}' on port ${port}`))
