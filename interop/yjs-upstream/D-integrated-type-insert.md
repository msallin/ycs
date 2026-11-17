# Optional low-priority issue: inserting a type that is already in a document

Verdict: user error (the maintainer said so in #342 and #183, both closed: "A shared type can only exist once in a Y.Doc"), but the error is unhelpful and the document is left inconsistent after the throw. Draft only if the operator wants it; not a priority.

Related closed issues: https://github.com/yjs/yjs/issues/342, https://github.com/yjs/yjs/issues/183.

---

**Title:** Inserting a type that is already integrated throws a `TypeError` after the item was added; the doc differs from its own encoding

**Version:** yjs 13.6.33 and 14.0.0-16 (same output)

**Repro** (`insert-integrated-type.mjs`):

```js
import * as Y from 'yjs'
const doc = new Y.Doc(); doc.clientID = 1
const a = doc.getArray('a'), b = doc.getArray('b')
const m = new Y.Map()
a.insert(0, [m])
try { b.insert(0, [m]) } catch (e) { console.log('threw:', e.message) }
m.set('k', 1)
const p = new Y.Doc(); Y.applyUpdate(p, Y.encodeStateAsUpdate(doc))
console.log('doc   a', JSON.stringify(a.toJSON()), 'b', JSON.stringify(b.toJSON()))
console.log('fresh a', JSON.stringify(p.getArray('a').toJSON()), 'b', JSON.stringify(p.getArray('b').toJSON()))
```

**Expected:** a clear error such as "a shared type can be inserted once" before anything changes. The same applies to a type from another doc.
**Actual:** `Cannot read properties of null (reading 'forEach')`. The item was already linked into `b`: the doc shows `m` (with `k`) in both arrays, while a doc loaded from `encodeStateAsUpdate` has `a: [{}]` and `b: [{"k":1}]`. The type now points to the second item, so later edits to `m` appear in `a` locally but not after a reload.

**Cause:** `typeListInsertGenericsAfter` (`src/types/AbstractType.js:687-689`) creates the `Item` and calls `integrate` without checking that `c` is a fresh type. `ContentType.integrate` (`src/structs/ContentType.js:99`) calls `type._integrate`, which re-points `_item`/`doc` and then runs `_prelimContent.forEach` (`src/types/YMap.js:78`; the same in `YArray`, `YText`), but `_prelimContent` is already `null` from the first integration. The throw comes after the item is in the list.

**Suggested fix:** in the `c instanceof AbstractType` branch, `if (c._item !== null || c.doc !== null) throw new Error('Shared type is already part of a document; create a new one')` before the item is built. Root types (`doc.getText('x')`, issue #183) have `_item === null` but `doc !== null`, so test both.

Found by differential tests of Ycs, a C# port of Yjs.
