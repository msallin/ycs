// What V8's Array.prototype.sort (TimSort) makes of comparators, consistent and not: Yjs' mergeUpdatesV2 sorts its
// struct readers with a comparator that answers -1 for both orders of a GC struct and an item at one clock, so which
// one Yjs keeps is decided by the order in which V8 asks. tests/Ycs.Tests/StableSortTests.cs sorts the same cases with
// Ycs' StableSort and compares.
//
//   node v8-sort.mjs > ../../tests/Ycs.Tests/Fixtures/v8-sort.json
//
// A case is regenerated from its numbers on both sides: the elements are 0..n-1; mulberry32(seed) draws the keys; the
// comparator of a pair (a, b) is a function of (seed, a, b) through mix32 (both are written out in StableSortTests).
const mix32 = x => {
  x = (x ^ (x >>> 16)) >>> 0
  x = Math.imul(x, 0x7feb352d) >>> 0
  x = (x ^ (x >>> 15)) >>> 0
  x = Math.imul(x, 0x846ca68b) >>> 0
  return (x ^ (x >>> 16)) >>> 0
}
const pair = (seed, a, b) => mix32((mix32(seed) + Math.imul(a, 0x9e3779b1) + Math.imul(b, 0x85ebca77)) >>> 0)
const mulberry32 = seed => {
  let a = seed | 0
  return () => {
    a = (a + 0x6d2b79f5) | 0
    let t = Math.imul(a ^ (a >>> 15), 1 | a)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return (t ^ (t >>> 14)) >>> 0
  }
}

// Keys in runs that ascend, descend or stay level (TimSort's runs and gallops), from a range of `range` values (ties).
export const keys = (seed, n, range) => {
  const next = mulberry32(seed)
  const ks = []
  while (ks.length < n) {
    const len = 1 + (next() % 80)
    const shape = next() % 3
    let k = next() % range
    for (let i = 0; i < len && ks.length < n; i++) {
      ks.push(k)
      if (shape === 0) k = (k + (next() % 3)) % range
      else if (shape === 1) k = (k + range - (next() % 3)) % range
    }
  }
  return ks
}

// kind 0: random answers; 1: by key, a few pairs answered at random; 2: by key (ties stay in order); 3: by key, and a
// tie of two elements of different flags answers -1 both ways (Yjs' comparator of a GC struct and an item).
export const comparator = (kind, seed, ks) => (a, b) => {
  const r = pair(seed, a, b)
  if (kind === 0) return (r % 3) - 1
  if (kind === 1 && r % 100 < 3) return ((r >>> 8) % 3) - 1
  if (ks[a] !== ks[b]) return ks[a] - ks[b]
  if (kind === 3 && (mix32(seed ^ a) & 1) !== (mix32(seed ^ b) & 1)) return -1
  return 0
}

const fnv = perm => {
  let h = 0x811c9dc5
  for (const v of perm) {
    for (let i = 0; i < 4; i++) {
      h = Math.imul(h ^ ((v >>> (8 * i)) & 0xff), 0x01000193) >>> 0
    }
  }
  return h
}

const sizes = [0, 1, 2, 3, 4, 5, 7, 8, 16, 31, 32, 33, 63, 64, 65, 66, 100, 127, 128, 129, 200, 255, 256, 300, 513, 1000, 2049]
const cases = []
let seed = 1
for (const n of sizes) {
  for (let kind = 0; kind < 4; kind++) {
    for (const range of [2, 7, 1000]) {
      const ks = keys(seed, n, range)
      const perm = Array.from({ length: n }, (_, i) => i)
      perm.sort(comparator(kind, seed, ks))
      cases.push({ n, kind, range, seed, hash: fnv(perm), perm: n <= 16 ? perm : undefined })
      seed++
    }
  }
}
console.log(JSON.stringify({ node: process.version, cases }, null, 0))
