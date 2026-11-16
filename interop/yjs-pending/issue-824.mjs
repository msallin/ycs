// The first case of yjs/yjs#824 against Yjs 13 and 14 (YJS13= / YJS14= other builds).
const Y13 = await import(process.env.YJS13 ?? 'yjs')
const Y14 = await import(process.env.YJS14 ?? 'y14')
for (const [name, Y, T] of [['13', Y13, d => d.getText('text')], ['14', Y14, d => d.get('text')]]) {
const docB = new Y.Doc(); docB.clientID = 1
const docC = new Y.Doc(); docC.clientID = 2
const updatesB = [], updatesC = []
docB.on('update', (u, origin) => { if (origin !== docC) updatesB.push(u) })
docC.on('update', (u, origin) => { if (origin !== docB) updatesC.push(u) })
T(docB).insert(0, 'a'); T(docB).insert(1, 'b'); T(docB).insert(0, 'c')
Y.applyUpdate(docC, Y.mergeUpdates(updatesB.slice(0, 2)), docB)
T(docC).insert(1, 'X')
Y.applyUpdate(docB, updatesC[0], docC)
T(docB).insert(3, 'Y')
const ydoc = new Y.Doc()
Y.applyUpdate(ydoc, updatesB[0]); Y.applyUpdate(ydoc, updatesB[2])
Y.applyUpdate(ydoc, Y.mergeUpdates([updatesC[0], updatesB[3]]))
console.log(name, 'before 1:1', ydoc.store.pendingStructs?.missing)
Y.applyUpdate(ydoc, updatesB[1])
console.log(name, T(ydoc).toString(), ydoc.store.pendingStructs?.missing)
}
