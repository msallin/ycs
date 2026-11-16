# Demo page source

The source of `../wwwroot/js/demo.js`, the editor of the demo page (CodeMirror 6, `y-codemirror.next`,
`y-websocket`, Yjs). The bundle is committed, so the server and CI need no npm for the page.

Rebuild after changing `src/main.js` or a package:

    npm ci && npm run build

The bundle starts with a comment listing the bundled packages and their licenses.

`?transport=websocket` (the default) or `?transport=signalr` selects the transport; `src/main.js` reads it in one
place, `selectTransport`. The status line shows the transport and a link to the other one.

`src/signalr-provider.mjs` is a copy of `interop/signalr/provider.mjs` (the demo bundles from its own folder); keep
the two files identical.
