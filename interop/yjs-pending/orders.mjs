// Every order of the two scenarios of the fixture: how many leave structs pending in Yjs 13 (YJS13= another build).
const Y = await import(process.env.YJS13 ?? 'yjs')
import { readFileSync } from 'node:fs'
const f = JSON.parse(readFileSync(new URL('../../tests/Ycs.Tests/Fixtures/pending-retry.json', import.meta.url)))
const perms = xs => xs.length <= 1 ? [xs] : xs.flatMap((x, i) => perms([...xs.slice(0, i), ...xs.slice(i + 1)]).map(p => [x, ...p]))
for (const k of ['minimal', 'repro']) {
  const us = f[k].updates.map(s => new Uint8Array(Buffer.from(s, 'base64')))
  const stuck = perms(us.map((_, i) => i)).filter(o => { const d = new Y.Doc(); for (const i of o) Y.applyUpdate(d, us[i]); return d.store.pendingStructs })
  console.log(k, stuck.length, 'of', perms(us.map((_, i) => i)).length, stuck.slice(0, 6).map(o => o.join('')).join(' '))
}
