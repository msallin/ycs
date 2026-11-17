# New issue: `YXmlFragment.insertAfter(ref, ...)` with a `ref` that was merged away diverges from remote peers

Target: new issue on yjs/yjs. No duplicate found (`insertAfter` only appears in #259 and PR #267, which added it).

---

**Title:** `YXmlFragment.insertAfter` with a deleted reference node links the new node into a dead item; local and remote differ

**Version:** yjs 13.6.33 (also 14.0.0-16; `@y/y` 14.0.0-rc.30 has no `insertAfter`)

**Repro** (`insert-after-merged.mjs`):

```js
import * as Y from 'yjs'
const a = new Y.Doc(); a.clientID = 1
const f = a.getXmlFragment('f')
const mk = n => new Y.XmlElement(n)
const e = [mk('first'), mk('held'), mk('right')]
f.insert(0, e)
const held = e[1]
const peer = new Y.Doc(); Y.applyUpdate(peer, Y.encodeStateAsUpdate(a))
a.on('update', u => Y.applyUpdate(peer, u))
f.delete(0, 2) // "first" and "held" are deleted; the transaction merges them into one item
f.insertAfter(held, [mk('x')])
const names = d => d.getXmlFragment('f').toArray().map(n => n.nodeName).join()
console.log('local ', names(a))
console.log('remote', names(peer))
```

**Expected:** both print `x,right` (or both `right`, if inserting after a deleted node is not allowed).
**Actual:** local `right`, remote `x,right`. The new node is lost locally and the replicas differ for good. With a concurrent insert by a peer (not in the script) we saw `peer,right` locally and `x,peer,right` remotely.

**Cause:** `held._item` is the item of clock 1. When the two deleted items are merged (`Item.mergeWith`, `src/structs/Item.js:571`), the left item absorbs the right one and `held._item` is left as a stale item that is no longer in the list. `insertAfter` (`src/types/YXmlFragment.js:341`) passes `ref._item` as is, and `typeListInsertGenericsAfter` (`src/types/AbstractType.js:646-650`) uses it as `left`, with `right = left.right`. The new item is linked after the stale item, which no list walk reaches, so it is invisible locally. Its `origin` is the stale item's id (`1:1`), so a peer that decodes the update puts it after the real item and shows it.

**Suggested fix:** in `insertAfter`, take the live item for the reference's last clock before inserting:

```js
refItem = getItemCleanEnd(transaction, transaction.doc.store, createID(refItem.id.client, refItem.id.clock + refItem.length - 1))
```

Checked on a patched copy: local and remote both give `x,right`.

Found by differential tests of Ycs, a C# port of Yjs.
