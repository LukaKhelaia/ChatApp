// ---------------------------------------------------------------------------
//  ChatApp - chat-history.js
//
//  History a page at a time: drawing a page, joining it to the one below,
//  and fetching the next when the reader scrolls up to it.
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
import { chatBox, username } from './chat-core.js';
import { avatarSpacer, buildMessageRow } from './chat-render.js';
import { sameDay } from './chat-messages.js';
import { makeDayTracker } from './chat-days.js';
import { appendMessageRow } from './chat-actions.js';

    // ======================================================================
    //  HISTORY, A PAGE AT A TIME
    // ======================================================================
    //  A conversation used to arrive in a single response: every message ever
    //  sent, rendered in one go. That is fine for the forty messages a demo
    //  has and hopeless for a real one - the query, the payload and the DOM
    //  all grow without limit. The server now answers with the newest page
    //  and a cursor, and everything above it is fetched only when the reader
    //  actually scrolls up to it.

    export const history = {
        oldest: null,      // id of the topmost message currently on the pane
        hasMore: false,    // is there anything above it worth asking for
        loading: false,    // one request in flight at a time
        key: null          // which conversation this cursor belongs to
    };

    // Identifies the open conversation, so a page that arrives after the
    // reader has moved on can be dropped instead of landing in the wrong chat.
    export function conversationKey() {
        return state.selectedGroupId ? ("g:" + state.selectedGroupId) : ("p:" + (state.selectedUser || ""));
    }

    export function resetHistoryCursor() {
        history.oldest = null;
        history.hasMore = false;
        history.loading = false;
        history.key = conversationKey();
    }

    // Both endpoints answer with { messages, hasMore, oldestId }.
    export function pageMessages(page) {
        return (page && Array.isArray(page.messages)) ? page.messages : [];
    }

    export function rememberCursor(page) {
        history.key = conversationKey();
        history.hasMore = !!(page && page.hasMore);
        if (page && page.oldestId != null) history.oldest = page.oldestId;
    }

    // --- drawing a page ------------------------------------------------------
    // `target` is the pane for the newest page and a detached fragment for an
    // older one; `days` is the tracker that decides where date chips go. A page
    // drawn off-screen brings its own tracker, because it is a separate run of
    // messages and must not be judged against what is already displayed.

    export function renderPrivatePage(messages, target, days) {
        for (let i = 0; i < messages.length; i++) {
            const m = messages[i];
            days.ensure(m.sentAt, target);

            const from = (m.user || "").trim().toLowerCase();

            // The run ends when the sender changes OR the day does, so the
            // last message of a day always keeps its face.
            const next = messages[i + 1];
            const runContinues = next?.user?.toLowerCase() === from &&
                sameDay(next?.sentAt, m.sentAt);

            const row = buildMessageRow({
                id: m.id,
                sender: from,
                text: m.text,
                attachment: m.attachment,
                when: m.sentAt,
                mine: from === username,
                group: false,
                nickname: m.nickname,
                avatarUrl: m.avatarUrl,
                status: m.readAt ? "seen" : (m.deliveredAt ? "delivered" : "sent"),
                readAt: m.readAt
            }, { face: !runContinues });

            appendMessageRow(row, m.replyTo, m.reactions, false, target);
        }
    }

    export function renderGroupPage(messages, target, days) {
        let prevSender = null;
        let prevStamp = null;

        for (let i = 0; i < messages.length; i++) {
            const m = messages[i];
            days.ensure(m.timestamp, target);
            if (m.id > state.latestGroupMessageId) state.latestGroupMessageId = m.id;

            const sender = (m.sender || "").toLowerCase();

            const next = messages[i + 1];
            const runContinues = next?.sender?.toLowerCase() === sender &&
                sameDay(next?.timestamp, m.timestamp);

            // The name shows on the FIRST message of a run - and a new day
            // starts a new run, which is why yesterday's last message used to
            // hold the name that belonged on today's first.
            const startsRun = prevSender !== sender || !sameDay(prevStamp, m.timestamp);

            const row = buildMessageRow({
                id: m.id,
                sender: sender,
                text: m.text,
                attachment: m.attachment,
                when: m.timestamp,
                mine: sender === username,
                group: true,
                nickname: m.nickname,
                avatarUrl: m.avatarUrl
            }, { face: !runContinues, name: startsRun });

            appendMessageRow(row, m.replyTo, m.reactions, false, target);
            prevSender = sender;
            prevStamp = m.timestamp;
        }
    }

    // --- joining the seam ----------------------------------------------------
    // A renderer only ever sees its own page, so three of its decisions are
    // made blind at the top of the pane and have to be reconciled once the
    // older page lands underneath... above it:
    //
    //   the date chip over the old top row is only right if the older page
    //   really did end on a different day;
    //
    //   the face on the older page's last received row belongs to the END of a
    //   run, and the run may now carry on into the row below it;
    //
    //   and a group name marks the START of a run, so the old top row loses
    //   its name if that run now begins higher up.
    export function prependPage(fragment) {
        // Measured first, before anything is added OR taken away. Everything
        // this function touches is above the reader, so their distance from
        // the bottom of the pane is the one number that should not change -
        // including when the seam fix deletes a now-redundant date chip, which
        // is 40px of content vanishing right where they are looking.
        const fromBottom = chatBox.scrollHeight - chatBox.scrollTop;

        const rows = fragment.querySelectorAll(".msg-row");
        const seamTop = rows.length ? rows[rows.length - 1] : null;   // last of the older page
        const seamBottom = chatBox.querySelector(".msg-row");         // first of what is on screen

        if (seamTop && seamBottom) {
            const sameDayAcrossSeam = !!seamTop.dataset.day &&
                seamTop.dataset.day === seamBottom.dataset.day;

            if (sameDayAcrossSeam) {
                const chip = chatBox.querySelector(".day-separator");
                if (chip) chip.remove();
            }

            const runsOn = sameDayAcrossSeam &&
                !!seamTop.dataset.sender &&
                seamTop.dataset.sender === seamBottom.dataset.sender;

            if (runsOn) {
                const face = seamTop.querySelector("img.chat-avatar-small");
                if (face) face.replaceWith(avatarSpacer());

                const bubble = seamBottom.querySelector(".received-group-message");
                const name = bubble ? bubble.querySelector(":scope > strong") : null;
                if (name) name.remove();
            }
        }

        // Put the reader back where they were.
        //
        // The pane is scroll-behavior:smooth, and that applies to assigning
        // scrollTop as much as to scrollIntoView. If a page lands while a
        // programmatic scroll is still easing - jumping to a quoted message,
        // say - the browser animates towards the restored position and the
        // in-flight animation drags it somewhere else entirely. The correction
        // has to be instant, so smoothness is switched off for the one line
        // that matters.
        chatBox.insertBefore(fragment, chatBox.firstChild);

        const easing = chatBox.style.scrollBehavior;
        chatBox.style.scrollBehavior = "auto";
        chatBox.scrollTop = chatBox.scrollHeight - fromBottom;
        chatBox.style.scrollBehavior = easing;
    }

    export function showOlderSpinner() {
        if (chatBox.querySelector(".history-more")) return;
        const el = document.createElement("div");
        el.className = "history-more";
        el.textContent = "Loading earlier messages…";
        chatBox.insertBefore(el, chatBox.firstChild);
    }

    export function hideOlderSpinner() {
        const el = chatBox.querySelector(".history-more");
        if (el) el.remove();
    }

    export async function loadOlderMessages() {
        if (history.loading || !history.hasMore || history.oldest == null) return;

        const key = conversationKey();
        if (history.key !== key) return;

        history.loading = true;
        showOlderSpinner();

        try {
            const url = state.selectedGroupId
                ? `/api/group/messages?groupId=${encodeURIComponent(state.selectedGroupId)}&before=${history.oldest}`
                : `/api/messages/history?withUser=${encodeURIComponent(state.selectedUser)}&before=${history.oldest}`;

            const res = await fetch(url);
            if (!res.ok) throw new Error("history request failed: " + res.status);

            const page = await res.json();

            // They may have opened another conversation while this was in the
            // air. Dropping it is the whole reason the key is carried along.
            if (conversationKey() !== key) return;

            const messages = pageMessages(page);
            hideOlderSpinner();          // before measuring, or it skews the anchor

            if (messages.length) {
                const frag = document.createDocumentFragment();
                const days = makeDayTracker();
                if (state.selectedGroupId) renderGroupPage(messages, frag, days);
                else renderPrivatePage(messages, frag, days);
                prependPage(frag);
            }

            rememberCursor(page);
        } catch (err) {
            console.error("Could not load older messages:", err);
        } finally {
            hideOlderSpinner();
            history.loading = false;
        }
    }

    // Near the top rather than at it: on a touch screen the momentum runs out
    // before scrollTop reaches zero, and waiting for exactly 0 means the page
    // never loads.
    chatBox.addEventListener("scroll", () => {
        if (chatBox.scrollTop <= 120) loadOlderMessages();
    });
