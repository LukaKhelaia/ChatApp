// ---------------------------------------------------------------------------
//  ChatApp - chat-notify.js
//
//  The arrival sound, and typing anywhere on the page landing in the composer.
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
import { myFriends } from './chat-friends.js';
import { clearReply, replyingTo } from './chat-actions.js';
import { autoGrowComposer, clearPendingAttachment, resetComposerHeight } from './chat-composer.js';
import { updateComposerState } from './chat-ui.js';

    // ======================================================================
    //  ARRIVAL SOUND
    //  Synthesised rather than shipped: two short sine notes through the Web
    //  Audio API. Nothing to download, nothing to 404, and it costs a couple
    //  of hundred bytes instead of a binary in wwwroot.
    // ======================================================================
    export let audioCtx = null;
    export let lastChimeAt = 0;

    // Off in Settings means silent everywhere this account is signed in, so
    // the answer comes from the server rather than from this browser.
    export let soundEnabled = true;

    fetch("/api/account/settings")
        .then(res => res.ok ? res.json() : null)
        .then(data => { if (data) soundEnabled = data.soundEnabled !== false; })
        .catch(() => { /* leave it on - a failed lookup should not mute the app */ });

    // Browsers refuse to start an AudioContext before the user has interacted
    // with the page, so it is built on the first click or keypress rather than
    // at load - otherwise the first message of a session would be silent and
    // log an error besides.
    export function ensureAudio() {
        if (audioCtx) return;
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return;
        try {
            audioCtx = new Ctor();
        } catch (error) {
            console.warn("No audio available:", error);
        }
    }

    document.addEventListener("click", ensureAudio);
    document.addEventListener("keydown", ensureAudio);

    export function playMessageChime() {
        if (!soundEnabled) return;
        if (!audioCtx) return;

        // a burst of messages should not become a burst of beeps
        const now = Date.now();
        if (now - lastChimeAt < 400) return;
        lastChimeAt = now;

        if (audioCtx.state === "suspended") audioCtx.resume();

        const t0 = audioCtx.currentTime;

        // a rising two-note blip, E5 then B5
        [[659.25, 0], [987.77, 0.085]].forEach(pair => {
            const freq = pair[0];
            const delay = pair[1];

            const osc = audioCtx.createOscillator();
            const gain = audioCtx.createGain();

            osc.type = "sine";
            osc.frequency.setValueAtTime(freq, t0 + delay);

            // Exponential ramps, never to or from exactly zero: the curve is
            // undefined at zero and the browser throws instead of playing.
            gain.gain.setValueAtTime(0.0001, t0 + delay);
            gain.gain.exponentialRampToValueAtTime(0.11, t0 + delay + 0.012);
            gain.gain.exponentialRampToValueAtTime(0.0001, t0 + delay + 0.17);

            osc.connect(gain);
            gain.connect(audioCtx.destination);
            osc.start(t0 + delay);
            osc.stop(t0 + delay + 0.2);
        });
    }

    // ======================================================================
    //  TYPE ANYWHERE
    //  With a chat open, typing goes into the composer without clicking it
    //  first - the way every desktop chat app behaves.
    // ======================================================================
    export function aModalIsOpen() {
        return Array.from(document.querySelectorAll(".modal, .modal-overlay"))
            .some(m => getComputedStyle(m).display !== "none");
    }

    document.addEventListener("keydown", (e) => {
        // shortcuts, function keys, arrows, Enter - none of these are typing
        if (e.ctrlKey || e.metaKey || e.altKey) return;
        if (!e.key || e.key.length !== 1) return;

        if (!state.selectedUser && !state.selectedGroupId) return;

        const input = document.getElementById("messageInput");
        if (!input || input.disabled) return;

        const target = e.target;
        if (target === input) return;

        // whatever they are already typing into keeps the keystroke: the
        // search box, the group-name field, an input inside a modal
        const tag = (target.tagName || "").toLowerCase();
        if (tag === "input" || tag === "textarea" || tag === "select") return;
        if (target.isContentEditable) return;
        if (aModalIsOpen()) return;

        e.preventDefault();
        input.focus();
        input.value += e.key;
        input.setSelectionRange(input.value.length, input.value.length);
        autoGrowComposer();
        updateComposerState();
    });

    // Function to send a message (private or group)
    export function sendMessage() {
        const message = document.getElementById("messageInput").value.trim();
        const attachmentId = state.pendingAttachment ? state.pendingAttachment.id : null;

        // Nothing to send, or the upload hasn't landed yet - the send button is
        // disabled in both cases, this is the belt to that pair of braces.
        if (!message && attachmentId === null) return;
        if (state.attachmentUploading) return;

        if (state.selectedUser && !myFriends.has(state.selectedUser.toLowerCase())) {
            updateComposerState();
            return;
        }

        // The id of the message being answered, if the reply strip is up. The
        // hub drops it when it does not belong to this conversation, so a
        // stale one degrades to an ordinary message rather than a wrong quote.
        const replyToId = replyingTo ? replyingTo.id : null;

        if (state.selectedUser) {
            // Send a private message
            connection.invoke("SendPrivateMessage", state.selectedUser, message, attachmentId, replyToId);
        } else if (state.selectedGroupId) {
            // Send a group message
            connection.invoke("SendGroupMessage", state.selectedGroupId, message, attachmentId, replyToId);
        }

        // A sent message ends the sentence. Zeroing the throttle rather than
        // leaving it means the first keystroke of the NEXT message pings
        // immediately instead of waiting out the tail of this one's window.
        state.lastTypingPing = 0;

        // Clear input and refocus for the next message
        document.getElementById("messageInput").value = "";
        clearReply();
        clearPendingAttachment();
        resetComposerHeight();
        document.getElementById("messageInput").focus();
    }

    // Send message when button is clicked
    document.getElementById("sendButton").addEventListener("click", sendMessage);

    // Send message when Enter is pressed (Shift+Enter makes a new line)
    document.getElementById("messageInput").addEventListener("keydown", function (event) {
        if (event.key === "Enter" && !event.shiftKey) {
            event.preventDefault(); // Prevents new line from being added
            sendMessage();
        }
    });

