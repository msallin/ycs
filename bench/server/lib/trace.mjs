// Documents with a real editing history: the automerge-paper trace (one author typing a paper, 259,778 edits).
import * as Y from 'yjs'
import zlib from 'node:zlib'
import fs from 'node:fs'
import path from 'node:path'

export const loadTrace = path => JSON.parse(zlib.gunzipSync(fs.readFileSync(path)))

// `copies` authors (client ids 1, 2, ...) each type the whole trace, one after the other, at the end of the text;
// one transaction per trace transaction. Also returns the state as it was after `staleFraction` of all transactions,
// which is what a client that went offline then holds.
export const buildHistory = (trace, { copies = 1, txns = trace.txns.length, staleFraction = 0.9 } = {}) => {
  const doc = new Y.Doc({ gc: true })
  const text = doc.getText('text')
  const total = copies * txns
  const staleAt = Math.floor(total * staleFraction)
  let done = 0
  let stale = null
  for (let c = 0; c < copies; c++) {
    doc.clientID = c + 1
    const offset = text.length
    for (let t = 0; t < txns; t++) {
      if (done++ === staleAt) stale = { state: Y.encodeStateAsUpdate(doc), stateVector: Y.encodeStateVector(doc), text: text.toString() }
      doc.transact(() => {
        for (const [pos, del, ins] of trace.txns[t].patches) {
          if (del) text.delete(offset + pos, del)
          if (ins) text.insert(offset + pos, ins)
        }
      })
    }
  }
  const result = { state: Y.encodeStateAsUpdate(doc), text: text.toString(), stale }
  doc.destroy()
  return result
}

// buildHistory's states, kept in `dir` between runs (building 5 copies takes about 10 to 30 s).
export const cachedHistory = (tracePath, dir, { copies, staleFraction }) => {
  const file = path.join(dir, `history-${copies}x-stale${staleFraction}`)
  let state, staleState
  if (fs.existsSync(`${file}.bin`)) {
    state = new Uint8Array(fs.readFileSync(`${file}.bin`))
    staleState = new Uint8Array(fs.readFileSync(`${file}.stale.bin`))
  } else {
    const h = buildHistory(loadTrace(tracePath), { copies, staleFraction })
    state = h.state
    staleState = h.stale.state
    fs.writeFileSync(`${file}.bin`, state)
    fs.writeFileSync(`${file}.stale.bin`, staleState)
  }
  const text = s => { const d = new Y.Doc(); Y.applyUpdate(d, s); const t = d.getText('text').toString(); d.destroy(); return t }
  return { state, text: text(state), stale: { state: staleState, stateVector: Y.encodeStateVectorFromUpdate(staleState), text: text(staleState) } }
}
