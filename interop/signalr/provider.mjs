// A Yjs provider over the Ycs server's SignalR hub, with the surface of y-websocket's WebsocketProvider that the
// tests use: awareness, synced and 'sync'/'synced', 'status', connect(), disconnect(), destroy(), wsconnected.
// The hub carries y-protocols messages unchanged: the hub method `Send` and the client method `message` each take
// one message, as base64 in SignalR's JSON protocol.
import * as encoding from 'lib0/encoding'
import * as decoding from 'lib0/decoding'
import * as buffer from 'lib0/buffer'
import * as math from 'lib0/math'
import { ObservableV2 } from 'lib0/observable'
import * as syncProtocol from 'y-protocols/sync'
import * as awarenessProtocol from 'y-protocols/awareness'
import { HubConnectionBuilder, HttpTransportType, LogLevel } from '@microsoft/signalr'

export const messageSync = 0
export const messageAwareness = 1
export const messageQueryAwareness = 3

const encode = (type, write) => {
  const encoder = encoding.createEncoder()
  encoding.writeVarUint(encoder, type)
  write(encoder)
  return encoding.toUint8Array(encoder)
}

const awarenessMessage = (awareness, clients, states) =>
  encode(messageAwareness, e => encoding.writeVarUint8Array(e, awarenessProtocol.encodeAwarenessUpdate(awareness, clients, states)))

// As y-websocket: answers a sync message (step 1 with step 2), applies awareness, answers a query with every state.
const readMessage = (provider, bytes) => {
  const decoder = decoding.createDecoder(bytes)
  const encoder = encoding.createEncoder()
  const type = decoding.readVarUint(decoder)
  switch (type) {
    case messageSync: {
      encoding.writeVarUint(encoder, messageSync)
      const syncType = syncProtocol.readSyncMessage(decoder, encoder, provider.doc, provider)
      if (syncType === syncProtocol.messageYjsSyncStep2 && !provider.synced) {
        provider.synced = true
      }
      break
    }
    case messageAwareness:
      awarenessProtocol.applyAwarenessUpdate(provider.awareness, decoding.readVarUint8Array(decoder), provider)
      break
    case messageQueryAwareness:
      return awarenessMessage(provider.awareness, Array.from(provider.awareness.getStates().keys()))
    default:
      console.error('Unable to compute message')
  }
  return encoding.length(encoder) > 1 ? encoding.toUint8Array(encoder) : null
}

const send = (provider, hub, bytes) => {
  provider._lastSend = hub.send('Send', buffer.toBase64(bytes)).catch(error => provider.emit('connection-error', [error, provider]))
}

const broadcast = (provider, bytes) => {
  if (provider.wsconnected && provider.hub !== null) {
    send(provider, provider.hub, bytes)
  }
}

const closeConnection = (provider, hub, error) => {
  if (hub === null || hub !== provider.hub) {
    return
  }
  provider.emit('connection-close', [error, provider])
  provider.hub = null
  // What was sent before (the null awareness state of disconnect()) goes out before the hub's close message.
  provider._lastSend.then(() => hub.stop())
  provider.wsconnecting = false
  if (provider.wsconnected) {
    provider.wsconnected = false
    provider.synced = false
    awarenessProtocol.removeAwarenessStates(
      provider.awareness,
      Array.from(provider.awareness.getStates().keys()).filter(client => client !== provider.doc.clientID),
      provider)
    provider.emit('status', [{ status: 'disconnected' }])
  }
  // As y-websocket: every closed connection counts as a failed attempt until one syncs; backoff from 200 ms.
  provider.unsuccessfulReconnects++
  setTimeout(setup, math.min(math.pow(2, provider.unsuccessfulReconnects) * 100, provider.maxBackoffTime), provider)
}

const setup = provider => {
  if (!provider.shouldConnect || provider.hub !== null) {
    return
  }
  const hub = new HubConnectionBuilder()
    .withUrl(provider.url, { transport: HttpTransportType.WebSockets, skipNegotiation: provider.skipNegotiation })
    .configureLogging(provider.logLevel)
    .build()
  provider.hub = hub
  provider.wsconnecting = true
  provider.wsconnected = false
  provider.synced = false
  hub.on('message', base64 => {
    if (provider.hub !== hub) return
    const reply = readMessage(provider, buffer.fromBase64(base64))
    if (reply !== null) {
      send(provider, hub, reply)
    }
  })
  hub.onclose(error => closeConnection(provider, hub, error))
  hub.start().then(() => {
    if (provider.hub !== hub) {
      hub.stop()
      return
    }
    provider.wsconnecting = false
    provider.wsconnected = true
    provider.emit('status', [{ status: 'connected' }])
    send(provider, hub, encode(messageSync, e => syncProtocol.writeSyncStep1(e, provider.doc)))
    if (provider.awareness.getLocalState() !== null) {
      send(provider, hub, awarenessMessage(provider.awareness, [provider.doc.clientID]))
    }
  }, error => {
    provider.emit('connection-error', [error, provider])
    closeConnection(provider, hub, error)
  })
  provider.emit('status', [{ status: 'connecting' }])
}

export class SignalRProvider extends ObservableV2 {
  /**
   * @param {string} serverUrl the hub, e.g. http://127.0.0.1:5000/hub
   * @param {string} room
   * @param {import('yjs').Doc} doc
   */
  constructor (serverUrl, room, doc, {
    connect = true,
    awareness = new awarenessProtocol.Awareness(doc),
    params = {},
    maxBackoffTime = 2500,
    skipNegotiation = false,
    logLevel = LogLevel.None
  } = {}) {
    super()
    this.serverUrl = serverUrl
    this.roomname = room
    this.doc = doc
    this.awareness = awareness
    this.params = params
    this.maxBackoffTime = maxBackoffTime
    this.skipNegotiation = skipNegotiation
    this.logLevel = logLevel
    this.wsconnected = false
    this.wsconnecting = false
    this.unsuccessfulReconnects = 0
    this.shouldConnect = connect
    /** @type {import('@microsoft/signalr').HubConnection | null} */
    this.hub = null
    this._synced = false
    this._lastSend = Promise.resolve()

    this._updateHandler = (update, origin) => {
      if (origin !== this) {
        broadcast(this, encode(messageSync, e => syncProtocol.writeUpdate(e, update)))
      }
    }
    doc.on('update', this._updateHandler)
    this._awarenessUpdateHandler = ({ added, updated, removed }) => {
      broadcast(this, awarenessMessage(awareness, added.concat(updated, removed)))
    }
    awareness.on('update', this._awarenessUpdateHandler)
    this._exitHandler = () => awarenessProtocol.removeAwarenessStates(awareness, [doc.clientID], 'app closed')
    if (typeof process !== 'undefined') {
      process.on('exit', this._exitHandler)
    }
    if (connect) {
      this.connect()
    }
  }

  get url () {
    const query = new URLSearchParams({ ...this.params, room: this.roomname })
    return `${this.serverUrl}${this.serverUrl.includes('?') ? '&' : '?'}${query}`
  }

  get synced () {
    return this._synced
  }

  set synced (state) {
    if (this._synced !== state) {
      this._synced = state
      if (state) {
        this.unsuccessfulReconnects = 0
      }
      this.emit('synced', [state])
      this.emit('sync', [state])
    }
  }

  connect () {
    this.shouldConnect = true
    if (!this.wsconnected && this.hub === null) {
      setup(this)
    }
  }

  /** Sends the null awareness state of this client, as y-websocket does, and closes the connection. */
  disconnect () {
    this.shouldConnect = false
    broadcast(this, awarenessMessage(this.awareness, [this.doc.clientID], new Map()))
    closeConnection(this, this.hub, null)
  }

  destroy () {
    this.disconnect()
    if (typeof process !== 'undefined') {
      process.off('exit', this._exitHandler)
    }
    this.awareness.off('update', this._awarenessUpdateHandler)
    this.doc.off('update', this._updateHandler)
    super.destroy()
  }
}
