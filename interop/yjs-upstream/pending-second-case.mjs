// Second case for yjs/yjs#824: pending structs are not retried. Run: npm i yjs@13.6.33 && node pending-second-case.mjs
import * as Y from 'yjs'

const mk = (id, gc = true) => { const d = new Y.Doc({ gc }); d.clientID = id; return d }
const A = mk(55967, false), B = mk(849942), C = mk(785458) // A receives; B and C write
const sentB = [], sentC = []
const edit = (d, list, f) => { const sv = Y.encodeStateVector(d); f(d); list.push(Y.encodeStateAsUpdate(d, sv)) }
const ins = d => d.getText('t').insert(0, 'a')

edit(C, sentC, ins)                                 // C:0
edit(B, sentB, ins)                                 // B:0
edit(C, sentC, ins)                                 // C:1
Y.applyUpdate(B, sentC[0])                          // B sees C:0
edit(B, sentB, ins)                                 // B:1, right origin C:0
edit(C, sentC, d => d.getArray('a').push(['s']))    // C:2 (needs C:1, which never arrives)

const v2 = Y.convertUpdateFormatV1ToV2
Y.applyUpdateV2(A, v2(sentC[2]))
Y.applyUpdate(A, sentB[1])
Y.applyUpdateV2(A, v2(sentB[0]))
Y.applyUpdateV2(A, v2(sentC[0]))

// A holds C:0, B:0 and B:1, which only depend on each other: "aaa". C:2 rightly waits for C:1.
console.log('t =', JSON.stringify(A.getText('t').toString()), '(expected "aaa")')
console.log('missing =', A.store.pendingStructs?.missing)
console.log('pending structs =', Y.decodeUpdateV2(A.store.pendingStructs.update).structs.map(s => `${s.id.client}:${s.id.clock}`).join())
