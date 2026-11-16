import * as Y from 'yjs';
import { WebsocketProvider } from 'y-websocket';
import { EditorView, basicSetup } from 'codemirror';
import { EditorState } from '@codemirror/state';
import { markdown } from '@codemirror/lang-markdown';
import { yCollab } from 'y-codemirror.next';

const words = ['amber', 'birch', 'cedar', 'dune', 'ember', 'fjord', 'grove', 'heron', 'iris', 'juniper', 'koi', 'lark', 'maple', 'nectar', 'otter', 'pine'];
const colors = ['#d9480f', '#1971c2', '#2f9e44', '#9c36b5', '#c2255c', '#0c8599', '#e8590c', '#5f3dc4'];

const transports = {
  websocket(room, doc) {
    const scheme = location.protocol === 'https:' ? 'wss://' : 'ws://';
    return new WebsocketProvider(`${scheme}${location.host}/ws`, room, doc);
  },
};

function selectTransport() {
  const name = new URLSearchParams(location.search).get('transport') || 'websocket';
  return transports[name] ? { name, connect: transports[name] } : null;
}

function pick(list) { return list[Math.floor(Math.random() * list.length)]; }

function roomName() {
  let room = decodeURIComponent(location.hash.slice(1));
  if (!room) {
    room = `${pick(words)}-${pick(words)}-${Math.floor(Math.random() * 90 + 10)}`;
    location.hash = room;
  }
  return room;
}

function storedName() {
  try {
    const saved = localStorage.getItem('ycs-demo-name');
    if (saved) return saved;
  } catch { /* storage unavailable */ }
  return `${pick(words)}-${Math.floor(Math.random() * 90 + 10)}`;
}

function storeName(name) {
  try { localStorage.setItem('ycs-demo-name', name); } catch { /* storage unavailable */ }
}

const $ = (id) => document.getElementById(id);
const transport = selectTransport();
if (!transport) {
  $('status').textContent = 'unknown transport';
  throw new Error('unknown transport');
}

const room = roomName();
const doc = new Y.Doc();
const text = doc.getText('content');
const provider = transport.connect(room, doc);
const awareness = provider.awareness;
const color = pick(colors);
const nameInput = $('name');
nameInput.value = storedName();

function publishUser() {
  const name = nameInput.value.trim() || 'anonymous';
  awareness.setLocalStateField('user', { name, color, colorLight: `${color}33` });
  storeName(name);
}
publishUser();
nameInput.addEventListener('input', publishUser);

new EditorView({
  parent: $('editor'),
  state: EditorState.create({
    doc: text.toString(),
    extensions: [basicSetup, markdown(), EditorView.lineWrapping, yCollab(text, awareness)],
  }),
});

const status = $('status');
const toggle = $('toggle');
function show(state) {
  status.textContent = state;
  status.dataset.state = state;
  toggle.textContent = state === 'offline' ? 'Reconnect' : 'Disconnect';
}
show('connecting');
provider.on('status', ({ status: s }) => {
  if (s === 'disconnected') show('offline');
  else if (s === 'connecting') show('connecting');
  else if (s === 'connected') awareness.setLocalState(awareness.getLocalState()); // y-websocket re-announces the state at the clock its disconnect removed it with, which peers ignore until the 15 s renewal; a higher clock shows it at once
});
provider.on('sync', (synced) => { if (synced) show('synced'); });
toggle.addEventListener('click', () => {
  if (provider.shouldConnect) { provider.disconnect(); show('offline'); } else { provider.connect(); show('connecting'); }
});

$('room').textContent = room;
function updatePeers() { $('peers').textContent = String(awareness.getStates().size); }
awareness.on('change', updatePeers);
updatePeers();

$('copy').addEventListener('click', async () => {
  const url = `${location.origin}${location.pathname}#${encodeURIComponent(room)}`;
  try { await navigator.clipboard.writeText(url); $('copy').textContent = 'Copied'; } catch { prompt('Copy the link', url); }
  setTimeout(() => { $('copy').textContent = 'Copy link'; }, 1500);
});
window.addEventListener('hashchange', () => location.reload());
