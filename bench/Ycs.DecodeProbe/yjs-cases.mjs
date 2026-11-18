// Writes yjs-cases.tsv (run from this directory: node yjs-cases.mjs > yjs-cases.tsv; needs npm ci in interop/corpus).
import * as Y from '../../interop/corpus/node_modules/yjs/dist/yjs.mjs';
const b64 = u => Buffer.from(u).toString('base64');
const esc = s => [...s].map(c => c.codePointAt(0)).flatMap(cp => cp > 0xFFFF ? [0xD800 + ((cp - 0x10000) >> 10), 0xDC00 + ((cp - 0x10000) & 0x3FF)] : [cp]).map(u => u >= 32 && u < 127 ? String.fromCharCode(u) : '\\u' + u.toString(16).toUpperCase().padStart(4, '0')).join('');
const out = [];
function emit(name, build) {
  const doc = new Y.Doc();
  const t = doc.getText('t');
  build(doc, t);
  const text = t.toString();
  const u1 = Y.encodeStateAsUpdate(doc), u2 = Y.encodeStateAsUpdateV2(doc);
  const d1 = new Y.Doc(); Y.applyUpdate(d1, u1);
  const d2 = new Y.Doc(); Y.applyUpdateV2(d2, u2);
  out.push([name, 'v1', b64(u1), esc(text), esc(d1.getText('t').toString())].join('\t'));
  out.push([name, 'v2', b64(u2), esc(text), esc(d2.getText('t').toString())].join('\t'));
}
emit('split-pair-two-items', (d, t) => { t.insert(0, '\uD83D'); t.insert(1, '\uDE00'); });
emit('split-pair-three-items', (d, t) => { t.insert(0, 'a\uD83D'); t.insert(2, '\uDE00b\uD83D'); t.insert(5, '\uDE00c'); });
emit('lone-high-at-end', (d, t) => { t.insert(0, 'ab\uD83D'); });
emit('lone-low-then-text', (d, t) => { t.insert(0, '\uDE00ab'); t.insert(3, 'x'); });
emit('pairs-and-latin', (d, t) => { t.insert(0, 'héllo '); t.insert(6, '\u{1F600}\u{1F601}'); t.insert(0, '€'); });
emit('split-pair-reversed-order', (d, t) => { t.insert(0, '\uDE00'); t.insert(0, '\uD83D'); });
emit('many-items', (d, t) => { for (let i = 0; i < 300; i++) t.insert(0, i % 3 === 0 ? '\uD83D' : i % 3 === 1 ? '\uDE00' : 'é'); });
emit('bom', (d, t) => { t.insert(0, '﻿x'); t.insert(0, '﻿'); });
emit('map-keys-and-text', (d, t) => { const m = d.getMap('m'); m.set('ké\u{1F600}', 'v'); m.set('\uD83D', 1); t.insert(0, 'z'); });
console.log(out.join('\n'));
