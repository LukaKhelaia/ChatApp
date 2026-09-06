// ---------------------------------------------------------------------------
//  ChatApp - chat-typing.js
//
//  The typing indicator, in both directions.
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
import { connection, username } from './chat-core.js';

    // ======================================================================
    //  TYPING
    // ======================================================================
    //  Two halves that never speak to each other: we ping while a key is
    //  being pressed, and we display what other people's pings say. Neither
    //  half touches the database and neither expects an answer.

    export const TYPING_PING_MS = 2200;      // how often we tell the other end
    export const TYPING_FORGET_MS = 4500;    // how long one of their pings is believed

    // --- telling them --------------------------------------------------------
    // Throttled, not debounced. Debouncing would send nothing at all until the
    // person paused, which is the exact moment the line should be coming down.

    export function pingTyping() {
        if (connection.state !== signalR.HubConnectionState.Connected) return;
        if (!state.selectedUser && !state.selectedGroupId) return;

        const now = Date.now();
        if (now - state.lastTypingPing < TYPING_PING_MS) return;
        state.lastTypingPing = now;

        const call = state.selectedGroupId
            ? connection.invoke("TypingGroup", state.selectedGroupId)
            : connection.invoke("TypingPrivate", state.selectedUser);

        // A dropped ping costs one second of a missing line. It is not worth
        // a retry and it is certainly not worth an unhandled rejection.
        call.catch(() => { });
    }

    // --- showing theirs ------------------------------------------------------
    // Who we currently believe is typing, and the timer that will stop
    // believing it. A fresh ping replaces the timer rather than adding one.
    export const typingNow = new Map();
    export const typingStrip = document.getElementById("typingStrip");
    export const typingText = document.getElementById("typingText");

    document.getElementById("messageInput")?.addEventListener("input", pingTyping);

    // One person has stopped - because they sent the thing they were typing.
    export function stopTypingFor(who) {
        const key = (who || "").toLowerCase();
        const entry = typingNow.get(key);
        if (!entry) return;
        clearTimeout(entry.timer);
        typingNow.delete(key);
        renderTyping();
    }

    export function clearTyping() {
        typingNow.forEach(entry => clearTimeout(entry.timer));
        typingNow.clear();
        renderTyping();
    }

    export function typingSentence(names) {
        if (names.length === 1) return `${names[0]} is typing`;
        if (names.length === 2) return `${names[0]} and ${names[1]} are typing`;
        return `${names[0]} and ${names.length - 1} others are typing`;
    }

    export function renderTyping() {
        if (!typingStrip) return;

        const names = [...typingNow.values()].map(e => e.nickname);
        if (names.length === 0) {
            typingStrip.classList.remove("show");
            return;
        }

        typingText.textContent = typingSentence(names);
        typingStrip.classList.add("show");
    }

    connection.on("UserTyping", (fromUser, groupId, nickname) => {
        const who = (fromUser || "").toLowerCase();
        if (!who || who === username) return;

        // Only for the conversation actually on screen. A ping from a chat in
        // the sidebar is not interesting enough to badge - it is over in four
        // seconds and would flicker.
        const forOpenChat = groupId
            ? groupId === state.selectedGroupId
            : (!state.selectedGroupId && who === state.selectedUser);
        if (!forOpenChat) return;

        const existing = typingNow.get(who);
        if (existing) clearTimeout(existing.timer);

        typingNow.set(who, {
            // In a private chat the name never travels - it is the person whose
            // chat is open, and we already have it.
            nickname: nickname || (groupId ? who : (state.selectedUserNickname || who)),
            timer: setTimeout(() => { typingNow.delete(who); renderTyping(); }, TYPING_FORGET_MS)
        });

        renderTyping();
    });

