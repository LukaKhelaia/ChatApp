// ---------------------------------------------------------------------------
//  ChatApp - chat-render.js
//
//  The one place a message row is built.
//
//  There used to be two: the live path in chat-messages.js drew an arriving
//  message, and the page renderers in chat-history.js drew a fetched one, and
//  between them they carried four bubble types twice over. Every class name,
//  every fillBubble option, the avatar, the group name - all of it written out
//  in both places. Change a bubble and you had to remember to change it twice,
//  and the second one is the one you forget.
//
//  The two callers still differ in the one thing that genuinely IS different,
//  and it is worth naming. Run grouping needs to know whether the message
//  above is from the same person on the same day. History has the whole page
//  in an array, so it looks at the NEXT message. A live arrival has nothing
//  after it, so it looks at the previous ROW already on screen, and hands the
//  avatar down. That decision stays with each caller; only the drawing is
//  shared.
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

import { applyOwnStatus } from './chat-receipts.js';
import { dayKey } from './chat-days.js';
import { appendMessageRow } from './chat-actions.js';
import { fillBubble } from './chat-attachments.js';

    export function avatarFace(src, alt) {
        const img = document.createElement("img");
        img.src = src || "/images/default-avatar.png";
        img.className = "chat-avatar-small";
        img.alt = alt || "";
        return img;
    }

    export function avatarSpacer() {
        const gap = document.createElement("div");
        gap.className = "chat-avatar-spacer";
        return gap;
    }

    // The row remembers its own day. Joining two pages together needs to know
    // whether the seam crosses midnight, and re-parsing the timestamp off the
    // bubble later would mean trusting formatting to round-trip.
    export function stampDay(row, when) {
        const d = when ? new Date(when) : null;
        if (d && !isNaN(d.getTime())) row.dataset.day = dayKey(d);
    }

    /**
     * Builds one message row - the bubble, and for a received message the face
     * beside it. Does NOT attach it to anything: appendMessageRow does that,
     * along with the quote, the reactions and the arrival animation.
     *
     * `m` is a message flattened to what drawing actually needs:
     *   id, sender, text, attachment, when   - the message
     *   mine                                 - right-aligned, no face
     *   group                                - group styling, and a name on a
     *                                          run's first message
     *   nickname, avatarUrl                  - who it came from
     *   status, readAt                       - own private bubbles only
     *
     * `show.face` - this is the last message of its run, so it wears the face.
     * `show.name` - this is the first of its run, so it carries the name.
     */
    export function buildMessageRow(m, show) {
        const row = document.createElement("div");

        // a group bubble puts its text in a <p> so the sender's name above it
        // sits on its own line; everywhere else a span is enough
        const bubbleOpts = (m.group && !m.mine)
            ? { tag: "p", cls: "mb-0", time: m.when, id: m.id }
            : { time: m.when, id: m.id };

        if (m.mine) {
            row.className = "text-end mb-2";

            const bubble = document.createElement("div");
            bubble.className = m.group
                ? "sent-message p-2 d-inline-block"
                : "bg-primary text-white d-inline-block p-2 rounded-pill";

            fillBubble(bubble, m.text, m.attachment, bubbleOpts);

            // Only private messages carry sent/delivered/seen. A group has many
            // readers, so it uses the reader avatars instead - see chat-receipts.
            if (!m.group) applyOwnStatus(bubble, m.status || "sent", m.readAt || null);

            row.appendChild(bubble);
        } else {
            row.className = m.group
                ? "d-flex align-items-end"
                : "d-flex align-items-end gap-2 mb-2";
            row.dataset.sender = (m.sender || "").toLowerCase();

            row.appendChild(show && show.face
                ? avatarFace(m.avatarUrl, m.nickname || m.sender)
                : avatarSpacer());

            const bubble = document.createElement("div");
            bubble.className = m.group
                ? "received-group-message p-2 d-inline-block"
                : "received-message p-2 d-inline-block";

            if (m.group && show && show.name) {
                const nameEl = document.createElement("strong");
                nameEl.textContent = m.nickname;
                bubble.appendChild(nameEl);
            }

            fillBubble(bubble, m.text, m.attachment, bubbleOpts);
            row.appendChild(bubble);
        }

        stampDay(row, m.when);
        return row;
    }
