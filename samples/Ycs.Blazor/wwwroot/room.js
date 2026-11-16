// The page side of the room: the URL, and the textarea's value and caret. A module, so it starts Blazor (autostart is
// off) once window.ycsRoom exists.
import { diff } from "./positions.js";

window.ycsRoom = {
    // The room is the fragment (a new random one when there is none); the server the query parameter "server", by
    // default /ws on the page's own host.
    start() {
        let room = location.hash.slice(1);
        try {
            room = decodeURIComponent(room);
        } catch {
            // Not percent-encoded: the fragment as it is.
        }
        if (room === "") {
            room = "room-" + crypto.getRandomValues(new Uint32Array(1))[0].toString(16);
            history.replaceState(null, "", location.pathname + location.search + "#" + encodeURIComponent(room));
        }
        const scheme = location.protocol === "https:" ? "wss:" : "ws:";
        const server = new URLSearchParams(location.search).get("server") ?? `${scheme}//${location.host}/ws`;
        return { room, server };
    },
    attach(area, dotnet) {
        this.area = area;
        // The text .NET holds: the textarea's value before every input event, kept in step by this file alone.
        this.shown = area.value;
        // Synchronous, both ways: .NET applies a keystroke against exactly the text it was typed into, and puts every
        // remote change into the textarea before it returns. Only the change crosses (index, removed, inserted), never
        // the whole text; the caret says where among equal characters it is.
        area.addEventListener("input", () => {
            const edit = diff(this.shown, area.value, area.selectionEnd);
            if (edit.remove === 0 && edit.insert === "") return;
            dotnet.invokeMethod("OnInput", edit.index, edit.remove, edit.insert);
            this.shown = area.value;
        });
        // Another room: a fresh start.
        addEventListener("hashchange", () => location.reload());
    },
    selection() {
        return { start: this.area.selectionStart, end: this.area.selectionEnd };
    },
    // A remote change: only the changed range is replaced, so what the browser keeps of undo stays. The selection is
    // where .NET carried it through the changes.
    replace(start, removed, inserted, selectionStart, selectionEnd) {
        const area = this.area, { scrollTop, scrollLeft, selectionDirection } = area;
        area.setRangeText(inserted, start, start + removed);
        this.shown = area.value;
        area.setSelectionRange(selectionStart, selectionEnd, selectionDirection);
        area.scrollTop = scrollTop;
        area.scrollLeft = scrollLeft;
    },
};

Blazor.start();
