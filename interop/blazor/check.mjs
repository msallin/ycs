// The Blazor page and a y-websocket client from Node edit one Y.Text through a sync server: they type, concurrently
// too, and must converge, with each other and with a fresh client; each sees the other's presence, and the page's
// presence goes when the page does. Serves the published web root itself; run.sh starts the server.
//
//   node check.mjs <published wwwroot> <server URL, ws://host:port/ws>
//
// Where Chromium lacks libraries, set LD_LIBRARY_PATH.
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { extname, join, resolve, sep } from "node:path";
import WebSocket from "ws";
import * as Y from "yjs";
import { WebsocketProvider } from "y-websocket";

const { chromium } = createRequire(import.meta.url)("playwright-core");

const [wwwroot, serverUrl] = process.argv.slice(2);
if (!wwwroot || !serverUrl) {
  console.error("usage: node check.mjs <published wwwroot> <server URL>");
  process.exit(2);
}

const TIMEOUT = 30_000;
const room = "blazor-" + Math.random().toString(36).slice(2, 10);

const types = {
  ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".css": "text/css",
  ".json": "application/json", ".wasm": "application/wasm", ".dat": "application/octet-stream",
};

// The web root as files, index.html for "/"; nothing outside it.
const root = resolve(wwwroot);
const web = createServer(async (request, response) => {
  const path = decodeURIComponent(new URL(request.url, "http://x").pathname);
  const file = resolve(join(root, path === "/" ? "index.html" : path));
  try {
    if (!file.startsWith(root + sep)) throw new Error("outside the web root");
    const body = await readFile(file);
    response.writeHead(200, { "content-type": types[extname(file)] ?? "application/octet-stream" });
    response.end(body);
  } catch {
    response.writeHead(404).end();
  }
});
await new Promise((done) => web.listen(0, "127.0.0.1", done));
const pageUrl = `http://127.0.0.1:${web.address().port}/?server=${encodeURIComponent(serverUrl)}#${room}`;

const problems = [];
const ok = (what) => console.log("ok - " + what);

// what: a text, or a function that describes the failure.
async function until(condition, what) {
  const deadline = Date.now() + TIMEOUT;
  while (!(await condition())) {
    if (Date.now() > deadline) throw new Error("not within " + TIMEOUT / 1000 + " s: " + (typeof what === "function" ? await what() : what));
    await new Promise((r) => setTimeout(r, 25));
  }
}

function nodeClient(name) {
  const doc = new Y.Doc();
  const provider = new WebsocketProvider(serverUrl, room, doc, { WebSocketPolyfill: WebSocket });
  if (name) provider.awareness.setLocalStateField("user", { name, color: "#00aa00" });
  return { doc, text: doc.getText("content"), provider };
}

const browser = await chromium.launch();
let node, fresh;
try {
  const page = await browser.newPage();
  page.on("pageerror", (e) => problems.push("page error: " + e.message));
  page.on("console", (m) => m.type() === "error" && problems.push("console: " + m.text()));
  await page.goto(pageUrl);
  const area = page.locator("textarea");
  await page.waitForSelector("textarea:not([readonly])", { timeout: TIMEOUT });
  ok("the page loaded and synced");

  node = nodeClient("node");
  await until(() => node.provider.synced, "the Node client synced");
  ok("the Node client synced");

  const both = async () => {
    const value = await area.inputValue();
    return value === node.text.toString() ? value : null;
  };
  const texts = async (what) => what + "; page " + JSON.stringify(await area.inputValue()) + ", Node " + JSON.stringify(node.text.toString());

  await area.click();
  await page.keyboard.type("blazor;");
  await until(() => node.text.toString() === "blazor;", () => texts("the Node client has the page's edit"));
  node.text.insert(0, "node;");
  await until(async () => (await both()) === "node;blazor;", () => texts("both texts are node;blazor;"));
  ok("sequential edits converge: " + JSON.stringify(await both()));

  // Concurrently: the page types at its caret while Node inserts at random places.
  const typing = page.keyboard.type(" typed in the browser");
  for (let i = 0; i < 20; i++) {
    node.text.insert(Math.floor(Math.random() * (node.text.length + 1)), String.fromCharCode(97 + i));
    await new Promise((r) => setTimeout(r, 10));
  }
  await typing;
  const expectedLength = "node;blazor;".length + " typed in the browser".length + 20;
  await until(async () => (await both())?.length === expectedLength, () => texts("both texts have every edit"));
  ok("concurrent edits converge (" + expectedLength + " characters)");

  fresh = nodeClient();
  await until(() => fresh.provider.synced && fresh.text.toString() === node.text.toString(), "a fresh client has the same text");
  ok("a fresh client has the same text");

  const user = page.locator(".user:not(.own)", { hasText: "node" });
  await user.waitFor({ timeout: TIMEOUT });
  const colour = await user.evaluate((e) => getComputedStyle(e).borderLeftColor);
  if (colour !== "rgb(0, 170, 0)") throw new Error("the page shows node in " + colour);
  ok("the page shows the Node client's presence");

  const guest = () => [...node.provider.awareness.getStates()]
    .find(([id, state]) => id !== node.doc.clientID && state.user?.name?.startsWith("guest "));
  await until(() => guest() !== undefined, "the Node client sees the page's presence");
  ok("the Node client sees the page's presence: " + JSON.stringify(guest()[1].user));

  await page.close();
  await until(() => guest() === undefined, "the page's presence is gone after it closed");
  ok("the page's presence is gone after it closed");
} catch (e) {
  problems.unshift(e.message);
} finally {
  for (const client of [node, fresh]) client?.provider.destroy();
  await browser.close();
  web.close();
}

if (problems.length > 0) {
  console.error("FAILED\n" + problems.join("\n"));
  process.exit(1);
}
console.log("all checks passed");
// y-websocket's destroyed providers can leave a timer behind.
process.exit(0);
