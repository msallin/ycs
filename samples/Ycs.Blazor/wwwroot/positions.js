// The one replacement between two strings (`diff`), for the page's text area: what an input event changed. No DOM.
// From Plenum's web client (js/text/positions.js), as the text binding (SharedText.cs) is from its Blazor client.

const BLOCK = 1024;

const isHighSurrogate = (code) => code >= 0xd800 && code <= 0xdbff;
const isLowSurrogate = (code) => code >= 0xdc00 && code <= 0xdfff;

/**
 * How many code units `a` and `b` have in common at their start.
 * @param {string} a
 * @param {string} b
 */
export function commonPrefix(a, b) {
  const limit = Math.min(a.length, b.length);
  let length = 0;
  // Whole blocks first: comparing slices is done natively and is much faster than a loop over characters.
  while (length + BLOCK <= limit && a.slice(length, length + BLOCK) === b.slice(length, length + BLOCK)) length += BLOCK;
  while (length < limit && a.charCodeAt(length) === b.charCodeAt(length)) length++;
  return length;
}

/**
 * How many code units `a` and `b` have in common at their end, at most `limit`.
 * @param {string} a
 * @param {string} b
 * @param {number} [limit]
 */
export function commonSuffix(a, b, limit = Math.min(a.length, b.length)) {
  let length = 0;
  while (length + BLOCK <= limit
    && a.slice(a.length - length - BLOCK, a.length - length) === b.slice(b.length - length - BLOCK, b.length - length)) {
    length += BLOCK;
  }
  while (length < limit && a.charCodeAt(a.length - length - 1) === b.charCodeAt(b.length - length - 1)) length++;
  return length;
}

/**
 * The one replacement that turns `before` into `after`: the common prefix and suffix stay, the rest is replaced. A
 * surrogate pair is never split.
 * @param {string} before
 * @param {string} after
 * @param {number} [caret] Where the caret is in `after`. Typing "o" before an "o" gives the same string as typing it
 *   after; the caret tells which, and it matters to the others, whose text may be between the two.
 * @returns {{index: number, remove: number, insert: string}} Remove `remove` code units at `index`, then insert.
 */
export function diff(before, after, caret = after.length) {
  let prefix = Math.min(commonPrefix(before, after), Math.max(0, caret - Math.max(0, after.length - before.length)));
  if (prefix > 0 && isHighSurrogate(before.charCodeAt(prefix - 1))) prefix--;
  let suffix = commonSuffix(before, after, Math.min(before.length, after.length) - prefix);
  if (suffix > 0 && isLowSurrogate(before.charCodeAt(before.length - suffix))) suffix--;
  return {
    index: prefix,
    remove: before.length - prefix - suffix,
    insert: after.slice(prefix, after.length - suffix),
  };
}

