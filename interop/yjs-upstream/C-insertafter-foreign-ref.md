# New issue: `YXmlFragment.insertAfter(ref, ...)` does not check that `ref` is a child of the fragment

Target: new issue on yjs/yjs. No duplicate found.

---

**Title:** `YXmlFragment.insertAfter` accepts a node of another fragment (or one not in a document) and corrupts the list

**Version:** yjs 13.6.33 (also 14.0.0-16; `@y/y` 14.0.0-rc.30 has no `insertAfter`)

**Repro** (`insert-after-foreign.mjs`):

```js
import * as Y from 'yjs'
const a = new Y.Doc(); a.clientID = 1
const f1 = a.getXmlFragment('f1'), f2 = a.getXmlFragment('f2')
const mk = n => new Y.XmlElement(n)
const kids = [mk('p1'), mk('p2')]
f2.insert(0, kids)
f1.insert(0, [mk('q1'), mk('q2')])
const peer = new Y.Doc(); Y.applyUpdate(peer, Y.encodeStateAsUpdate(a))
a.on('update', u => Y.applyUpdate(peer, u))
f1.insertAfter(kids[0], [mk('x')]) // kids[0] is a child of f2, not f1
for (const [n, d] of [['local ', a], ['remote', peer]]) {
  const x = d.getXmlFragment('f2').get(1)
  console.log(n, 'f1.length', d.getXmlFragment('f1').length, 'f2.length', d.getXmlFragment('f2').length,
    'x.parent is', x.parent === d.getXmlFragment('f1') ? 'f1' : 'f2')
}
```

**Expected:** an error, like the one for an unknown reference in a fragment that is not yet in a document (`Reference item not found`).
**Actual:** no error. In both replicas `x` sits in `f2`'s list (`p1,x,p2`), but locally `x.parent` is `f1` and the lengths are `f1: 3, f2: 2`; the peer, which decodes the update, sees `f1: 2, f2: 3` and `x.parent` is `f2`. A local `f2.delete(0, 3)` then throws `Length exceeded!`, on the peer it works. The replicas disagree on content ownership and any later operation on them can differ.

The same missing check lets `insertAfter(new Y.XmlElement('stranger'), ...)` (a node that is not in any document, so `_item` is `null`) silently insert at the start instead of failing.

**Cause:** `YXmlFragment.insertAfter` (`src/types/YXmlFragment.js:341-342`) takes `ref._item` and hands it to `typeListInsertGenericsAfter` (`src/types/AbstractType.js:645`), which builds the new items with `parent = this` but `left = ref` and `right = ref.right`, both from another list. Nothing checks `ref._item.parent === this`, nor that `ref._item` is not `null` when `ref` is not.

**Suggested fix:** in `insertAfter`, when `ref` is not `null`:

```js
if (refItem === null || refItem.parent !== this) throw new Error('Reference item not found')
```

Checked on a patched copy: the repro throws.

Found by differential tests of Ycs, a C# port of Yjs.
