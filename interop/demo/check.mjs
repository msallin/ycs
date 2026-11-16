import { createRequire } from 'node:module';
const { chromium } = createRequire(import.meta.url)('playwright-core');

const [base, shot] = process.argv.slice(2);
const browser = await chromium.launch();
const open = async (room, transport, name, viewport) => {
  const ctx = await browser.newContext({ viewport, deviceScaleFactor: 2 });
  const page = await ctx.newPage();
  page.on('pageerror', (e) => { throw e; });
  await page.goto(`${base}/?transport=${transport}#${room}`);
  await page.fill('#name', name);
  await page.waitForSelector('#status[data-state=synced]');
  const shown = await page.textContent('#transport');
  if (shown !== transport) throw new Error(`status line shows ${shown}, expected ${transport}`);
  return page;
};
const text = (p) => p.evaluate(() => [...document.querySelectorAll('.cm-line')].map((l) => {
  const c = l.cloneNode(true);
  c.querySelectorAll('.cm-ySelectionCaret').forEach((e) => e.remove());
  return c.textContent;
}).join('\n'));
const converged = async (a, b, label) => {
  await a.waitForFunction(() => true);
  for (let i = 0; i < 100; i++) {
    const [x, y] = [await text(a), await text(b)];
    if (x === y && x.length > 0) { console.log(`ok  ${label}: ${JSON.stringify(x)}`); return x; }
    await a.waitForTimeout(100);
  }
  throw new Error(`${label}: not converged: ${JSON.stringify(await text(a))} vs ${JSON.stringify(await text(b))}`);
};
const type = async (p, s) => { await p.click('.cm-content'); await p.keyboard.press('Control+End'); await p.keyboard.type(s); };

const viewport = { width: 640, height: 360 };
const run = async (ta, tb) => {
  const label = ta === tb ? ta : `${ta}+${tb}`;
  const room = `check-${label}-${Date.now()}`;
  const alice = await open(room, ta, 'Alice', viewport);
  const bob = await open(room, tb, 'Bob', viewport);

  await type(alice, '# Hello\n');
  await type(bob, 'from Bob');
  await converged(alice, bob, `${label} online`);

  for (const [page, other] of [[alice, 'Bob'], [bob, 'Alice']]) {
    await page.waitForFunction((n) => [...document.querySelectorAll('.cm-ySelectionCaret')].some((c) => c.textContent.includes(n)), other, { timeout: 10000 });
    console.log(`ok  ${label}: cursor of ${other} shown`);
  }

  await bob.click('#toggle');
  await bob.waitForSelector('#status[data-state=offline]');
  await type(alice, '\nonline edit');
  await type(bob, '\noffline edit');
  await bob.click('#toggle');
  await bob.waitForSelector('#status[data-state=synced]');
  const merged = await converged(alice, bob, `${label} after reconnect`);
  for (const part of ['online edit', 'offline edit']) if (!merged.includes(part)) throw new Error(`${label}: lost ${part}`);

  for (const p of [alice, bob]) await p.waitForFunction(() => document.getElementById('peers').textContent === '2', null, { timeout: 10000 });
  console.log(`ok  ${label}: both pages count two peers after reconnect`);
  return [alice, bob];
};

for (const t of ['websocket', 'signalr']) await run(t, t);
const [alice, bob] = await run('websocket', 'signalr');
await Promise.all([alice, bob].map((p) => p.evaluate(() => document.activeElement?.blur())));
const side = await browser.newContext({ viewport: { width: 1288, height: 380 }, deviceScaleFactor: 2 });
const sheet = await side.newPage();
await sheet.setContent(`<body style="margin:0;display:flex;gap:8px;background:#888"><img style="width:640px" src="data:image/png;base64,${(await alice.screenshot()).toString('base64')}"><img style="width:640px" src="data:image/png;base64,${(await bob.screenshot()).toString('base64')}"></body>`);
await sheet.screenshot({ path: shot });
console.log(`screenshot ${shot}`);
await browser.close();
console.log('demo check passed');
