// ---------------------------------------------------------------------------
//  ChatApp - chat-presence.js
//
//  Who is online, and when everyone else was last here.
//
//  An ES module. Nothing here is global: what this file needs, it imports, and
//  what other files need from it, it exports. There is no load order to keep in
//  your head - chat-boot.js is the single entry and the module graph decides
//  the rest.
//
//  Two rules this file lives under:
//
//  chat-core.js imports NOTHING. It owns `connection`, and a cycle back into it
//  lets another module reach that const while it is still in its dead zone -
//  which is exactly how this refactor failed the first time.
//
//  Shared mutable state lives in chat-state.js, because an imported binding is
//  read-only at the importing end. If two files write the same value, it
//  belongs on that object, not in a `let` here.
// ---------------------------------------------------------------------------

import { state } from './chat-state.js';
import { connection } from './chat-core.js';

    // ======================================================================
    //  PRESENCE
    // ======================================================================
    //  Who is online right now, and when the rest were last here.
    //
    //  "Online" is not stored anywhere - it is whether the hub is holding a
    //  connection, which is the only definition that cannot go stale. A
    //  process restart forgets everyone, which is correct: nobody is connected
    //  to a process that just started. Only "last seen" is persisted, and only
    //  so the phrase still means something after that restart.
    //
    //  The server tells us about friends only, so this map only ever fills up
    //  with people the reader is entitled to know about.

    export const presence = new Map();   // email -> { online, lastSeen }

    export function presenceOf(email) {
        return presence.get((email || "").toLowerCase()) || { online: false, lastSeen: null };
    }

    export function rememberPresence(email, online, lastSeen) {
        const key = (email || "").toLowerCase();
        if (!key) return;
        presence.set(key, { online: !!online, lastSeen: lastSeen || null });
    }

    // Wraps an avatar so a status dot can sit on its corner. The wrapper
    // carries the address, which is what lets a PresenceChanged event find
    // every copy of a face without anyone keeping a list of them.
    export function withPresenceDot(img, email) {
        const wrap = document.createElement("span");
        wrap.className = "avatar-wrap";
        wrap.dataset.presenceFor = (email || "").toLowerCase();

        const dot = document.createElement("span");
        dot.className = "presence-dot";
        dot.title = "Online";

        wrap.appendChild(img);
        wrap.appendChild(dot);
        wrap.classList.toggle("online", presenceOf(email).online);
        return wrap;
    }

    export function paintPresence(email) {
        const key = (email || "").toLowerCase();
        const online = presenceOf(key).online;
        document.querySelectorAll(`[data-presence-for="${CSS.escape(key)}"]`)
                .forEach(node => node.classList.toggle("online", online));
    }

    // "last seen 20 minutes ago" is more use than a timestamp, right up until
    // it isn't - past a couple of days the date is what people actually want.
    export function lastSeenPhrase(iso) {
        const d = iso ? new Date(iso) : null;
        if (!d || isNaN(d.getTime())) return "Offline";

        const mins = Math.floor((Date.now() - d.getTime()) / 60000);
        if (mins < 1) return "last seen just now";
        if (mins < 60) return `last seen ${mins} minute${mins === 1 ? "" : "s"} ago`;

        const hours = Math.floor(mins / 60);
        if (hours < 24) return `last seen ${hours} hour${hours === 1 ? "" : "s"} ago`;

        const days = Math.floor(hours / 24);
        if (days === 1) return "last seen yesterday";
        if (days < 7) return `last seen ${days} days ago`;

        return "last seen " + d.toLocaleDateString(undefined, { day: "numeric", month: "long" });
    }

    // The line under the name in the chat header. Groups do not get one -
    // "online" for twelve people is a different feature.
    export function paintHeaderPresence() {
        const el = document.getElementById("chatHeadStatus");
        if (!el) return;

        if (!state.selectedUser || state.selectedGroupId) {
            el.textContent = "";
            el.classList.remove("online");
            return;
        }

        const theirs = presenceOf(state.selectedUser);
        el.textContent = theirs.online ? "Online" : lastSeenPhrase(theirs.lastSeen);
        el.classList.toggle("online", theirs.online);
    }

    connection.on("PresenceChanged", (who, online, lastSeen) => {
        rememberPresence(who, online, lastSeen);
        paintPresence(who);
        if ((who || "").toLowerCase() === state.selectedUser) paintHeaderPresence();
    });

    // "last seen 3 minutes ago" has to become "4 minutes ago" on its own, or
    // it quietly lies for as long as the chat stays open.
    setInterval(() => {
        if (state.selectedUser && !state.selectedGroupId && !presenceOf(state.selectedUser).online) paintHeaderPresence();
    }, 60000);

