// What Yjs does when observers throw, phase by phase (walkthrough-core F18), for
// tests/Ycs.Tests/ThrowingObserverTests.cs. Each scenario logs the handlers called, in order, the
// exception that escaped, and what a later, ordinary change of the same document still calls.
// Usage: node throwing-observers.mjs > ../../tests/Ycs.Tests/Fixtures/throwing-observers.json
import * as Y from 'yjs13'

const scenarios = {}

function run (name, setup, act) {
  const log = []
  const doc = new Y.Doc()
  doc.clientID = 1
  const a = doc.getArray('a')
  const m = doc.getMap('m')
  const t = doc.getText('t')
  const throws = new Set()
  const maybe = (what) => { log.push(what); if (throws.has(what)) throw new Error(what) }
  a.observe(() => maybe('obs:a'))
  a.observe(() => maybe('obs:a2'))
  m.observe(() => maybe('obs:m'))
  t.observe(() => maybe('obs:t'))
  a.observeDeep(() => maybe('deep:a'))
  m.observeDeep(() => maybe('deep:m'))
  doc.on('beforeObserverCalls', () => maybe('beforeObserverCalls'))
  doc.on('afterTransaction', () => maybe('afterTransaction'))
  doc.on('afterTransaction', () => maybe('afterTransaction2'))
  doc.on('afterTransactionCleanup', () => maybe('afterTransactionCleanup'))
  doc.on('update', () => maybe('update'))
  doc.on('updateV2', () => maybe('updateV2'))
  doc.on('afterAllTransactions', () => maybe('afterAllTransactions'))
  setup({ doc, a, m, t, throws, log })
  let escaped = null
  try {
    act({ doc, a, m, t, log })
  } catch (e) {
    escaped = e.message
  }
  const first = log.splice(0)
  throws.clear()
  // A later change: is the document still cleaned up?
  let laterEscaped = null
  try {
    doc.transact(() => { a.insert(0, ['later']) })
  } catch (e) {
    laterEscaped = e.message
  }
  scenarios[name] = { log: first, escaped, later: log.splice(0), laterEscaped, content: a.toJSON() }
}

const change = ({ doc, a, m, t }) => doc.transact(() => { a.insert(0, [1]); m.set('k', 1); t.insert(0, 'x') })
const thrower = (...what) => ({ throws }) => what.forEach(w => throws.add(w))

run('nothing throws', () => {}, change)
run('an observer', thrower('obs:a'), change)
run('two observers of one type', thrower('obs:a', 'obs:a2'), change)
run('observers of two types', thrower('obs:a', 'obs:m'), change)
run('beforeObserverCalls', thrower('beforeObserverCalls'), change)
run('a deep observer', thrower('deep:a'), change)
run('a deep observer and an observer', thrower('deep:a', 'obs:t'), change)
run('afterTransaction', thrower('afterTransaction'), change)
run('afterTransaction and an observer', thrower('afterTransaction', 'obs:m'), change)
run('afterTransactionCleanup', thrower('afterTransactionCleanup'), change)
run('update', thrower('update'), change)
run('updateV2', thrower('updateV2'), change)
run('afterAllTransactions', thrower('afterAllTransactions'), change)
run('the function and an observer', thrower('obs:m'), ({ doc, a, m }) => doc.transact(() => { a.insert(0, [1]); m.set('k', 1); throw new Error('function') }))
run('the function', () => {}, ({ doc, a }) => doc.transact(() => { a.insert(0, [1]); throw new Error('function') }))
run('an observer that changes the document, then one of its transaction', ({ a, m, throws, log }) => {
  throws.add('obs:m')
  let once = false
  a.observe(() => { if (!once) { once = true; log.push('obs:a changes m'); m.set('n', 2) } })
}, change)
run('an observer of a nested transaction and of the first', ({ a, m, throws, log }) => {
  throws.add('obs:a')
  let once = false
  a.observe(() => { if (!once) { once = true; m.set('n', 2) } })
  m.observe(() => { if (m.has('n')) { log.push('obs:m nested'); throw new Error('obs:m nested') } })
}, change)

console.log(JSON.stringify({ yjs: '13.6.32', scenarios }, null, 1))
