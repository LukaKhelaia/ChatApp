// ---------------------------------------------------------------------------
//  ChatApp - chat-actions.js
//
//  The hover toolbar: reactions, replies, and deleting a message for yourself.
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
import { chatBox, connection, scrollChatToBottom, username } from './chat-core.js';
import { isTrailing, trailingNode } from './chat-days.js';
import { history } from './chat-history.js';

    // ======================================================================
    //  MESSAGE ACTIONS - react, reply, delete
    //  Hovering a message reveals two buttons beside it: an emoji face that
    //  opens the reaction palette, and a three-dot menu holding Reply and
    //  Delete. The buttons are mounted with the row, not on hover, so the
    //  layout does not shift the moment the pointer arrives.
    // ======================================================================
    export const REACTION_PALETTE = ["\u{1F44D}", "❤️", "\u{1F602}", "\u{1F62E}", "\u{1F622}", "\u{1F64F}"];

    // Whichever chat is open decides which table an id belongs to. Private and
    // group ids are independent sequences, so this has to be checked before
    // acting on any id that arrived over the wire.
    export function currentScopeIsGroup() {
        return !!state.selectedGroupId;
    }

    export function bubbleOfMessage(id) {
        if (!chatBox || !id) return null;
        return chatBox.querySelector('[data-message-id="' + String(id).replace(/"/g, '\\"') + '"]');
    }

    export function rowOfMessage(id) {
        const bubble = bubbleOfMessage(id);
        return bubble ? bubble.parentElement : null;
    }

    // --- growing things in and out -------------------------------------------
    // A message used to be appended at full height and then faded in, so the
    // SPACE for it appeared in one frame and only the ink was animated. That
    // is the jump. Animating the element's own height means the conversation
    // opens up as the message arrives, which is what "smooth" actually means
    // here.
    export const REDUCED_MOTION = window.matchMedia
        ? window.matchMedia("(prefers-reduced-motion: reduce)")
        : { matches: false };

    export const GROW_MS = 260;
    export const GROW_EASE = "cubic-bezier(.4, 0, .2, 1)";

    // Work that must not run while a message is easing in. The seen-by avatar
    // is the case that forced this: it moves out of one row and into another,
    // and doing that mid-arrival yanked ~26px out from above the new message
    // and put it back below - the message appeared to shoot upward and the
    // whole arrival read as broken. Queued here, it happens a quarter of a
    // second later, on its own, where it looks deliberate.
    export let arrivalsInFlight = 0;
    export const waitingOnArrivals = [];

    export function whenArrivalsSettle(fn) {
        if (arrivalsInFlight === 0) { fn(); return; }
        waitingOnArrivals.push(fn);
    }

    export function arrivalStarted() { arrivalsInFlight += 1; }

    export function arrivalFinished() {
        arrivalsInFlight = Math.max(0, arrivalsInFlight - 1);
        if (arrivalsInFlight > 0) return;
        waitingOnArrivals.splice(0).forEach(fn => {
            try { fn(); } catch (error) { console.error("deferred layout work failed:", error); }
        });
    }

    // #chatBox is pinned to the bottom, and a growing row pushes the bottom
    // down every frame - so the pin has to be re-applied for the length of the
    // animation or the new message ends up half below the fold.
    export function keepPinnedDuring(anim) {
        const step = () => {
            scrollChatToBottom();
            if (anim.playState === "running") requestAnimationFrame(step);
        };
        requestAnimationFrame(step);
    }

    // direction "in" grows from nothing to natural height, "out" is the
    // reverse. Height is animated together with the margins, or the gap the
    // element sits in would still snap.
    export function animateBox(el, direction, done) {
        if (!el) { if (done) done(); return null; }

        if (REDUCED_MOTION.matches) {
            if (done) done();
            return null;
        }

        const height = el.getBoundingClientRect().height;
        if (!height) { if (done) done(); return null; }

        const cs = getComputedStyle(el);

        // margin-TOP is deliberately left out of the animation. The first row
        // in a chat carries margin-top:auto - the rule that pushes a short
        // conversation down to the bottom of the pane - and getComputedStyle
        // resolves that to the whole empty height above it. Animating it from
        // 0 dropped the first message in at the TOP of the pane and slid it
        // down, which is the "black box moving up" you saw. Left alone, the
        // auto margin recomputes every frame and the message grows upward out
        // of the bottom edge, which is the point.
        const collapsed = { height: "0px", marginBottom: "0px", opacity: 0 };
        const natural = { height: height + "px", marginBottom: cs.marginBottom, opacity: 1 };

        const prevOverflow = el.style.overflow;
        const prevFlex = el.style.flex;
        const prevAlign = el.style.alignItems;

        // overflow:hidden is what makes a height animation look like an
        // expansion rather than a squash - and it hands the element an
        // automatic minimum size of 0, so flex:0 0 auto has to go on with it
        // or an overflowing conversation squeezes the row flat.
        el.style.overflow = "hidden";
        el.style.flex = "0 0 auto";
        // clipped from the top, so the message rises out of the bottom edge
        if (el.classList.contains("msg-row")) el.style.alignItems = "flex-end";

        const anim = el.animate(
            direction === "in" ? [collapsed, natural] : [natural, collapsed],
            { duration: GROW_MS, easing: GROW_EASE, fill: "backwards" });

        // The row opening up is only half of it. On its own the bubble was
        // simply uncovered by the growing row, which is what still read as a
        // jump - so the bubble itself scales up out of the corner it came
        // from: the send button's for my messages, the avatar's for theirs.
        if (direction === "in") growBubble(el);

        keepPinnedDuring(anim);

        // Only a message counts as an arrival to wait for. A seen-by row
        // opening is itself deferred work, and counting it would make the
        // queue wait on its own output.
        const isArrival = direction === "in" &&
            (el.classList.contains("msg-row") || el.classList.contains("message-quote"));
        if (isArrival) arrivalStarted();

        let settled = false;
        const restore = () => {
            if (settled) return;
            settled = true;
            el.style.overflow = prevOverflow;
            el.style.flex = prevFlex;
            el.style.alignItems = prevAlign;
            if (isArrival) arrivalFinished();
            if (done) done();
        };
        anim.onfinish = restore;
        anim.oncancel = restore;
        return anim;
    }

    export function growBubble(row) {
        const bubble = row.querySelector ? row.querySelector("[data-message-id]") : null;
        if (!bubble) return;

        // The corner it grows from. Mine sit on the right, theirs on the
        // left, and both arrive from below.
        bubble.style.transformOrigin = row.classList.contains("text-end")
            ? "right bottom"
            : "left bottom";

        const anim = bubble.animate(
            [{ transform: "scale(.55)", opacity: 0 },
             { transform: "scale(1)", opacity: 1 }],
            { duration: GROW_MS, easing: GROW_EASE, fill: "backwards" });

        const clear = () => { bubble.style.transformOrigin = ""; };
        anim.onfinish = clear;
        anim.oncancel = clear;
    }

    // --- the hover toolbar ---------------------------------------------------
    export const ICON_REACT =
        '<svg viewBox="0 0 24 24" width="17" height="17" aria-hidden="true">' +
        '<circle cx="12" cy="12" r="9" fill="none" stroke="currentColor" stroke-width="1.8"/>' +
        '<circle cx="9" cy="10" r="1.15" fill="currentColor"/>' +
        '<circle cx="15" cy="10" r="1.15" fill="currentColor"/>' +
        '<path d="M8.2 14.2a4.6 4.6 0 0 0 7.6 0" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/></svg>';

    export const ICON_MORE =
        '<svg viewBox="0 0 24 24" width="17" height="17" aria-hidden="true">' +
        '<circle cx="5.5" cy="12" r="1.6" fill="currentColor"/>' +
        '<circle cx="12" cy="12" r="1.6" fill="currentColor"/>' +
        '<circle cx="18.5" cy="12" r="1.6" fill="currentColor"/></svg>';

    export function mountMessageActions(row) {
        if (!row || row.dataset.actionsMounted) return;
        const bubble = row.querySelector("[data-message-id]");
        if (!bubble) return;

        row.dataset.actionsMounted = "1";
        row.classList.add("msg-row");

        const tools = document.createElement("div");
        tools.className = "msg-actions";

        const react = document.createElement("button");
        react.type = "button";
        react.className = "msg-action msg-action-react";
        react.title = "React";
        react.setAttribute("aria-label", "React to this message");
        react.innerHTML = ICON_REACT;          // a fixed icon, no user input

        const more = document.createElement("button");
        more.type = "button";
        more.className = "msg-action msg-action-more";
        more.title = "More";
        more.setAttribute("aria-label", "More actions");
        more.innerHTML = ICON_MORE;

        // My own rows are right-aligned blocks, so the tools go before the
        // bubble; received rows are flex, so they go after it. Either way the
        // buttons end up on the inside edge, away from the wall - and the pair
        // is mirrored with the row, so the emoji is always the button next to
        // the bubble and the ... is always the one further out.
        const mine = row.classList.contains("text-end");

        if (mine) {
            tools.appendChild(more);
            tools.appendChild(react);
            row.insertBefore(tools, bubble);
        } else {
            tools.appendChild(react);
            tools.appendChild(more);
            bubble.after(tools);
        }
    }

    // Every render path ends here, which is what keeps the four of them from
    // drifting apart again.
    //
    // `animate` is on for messages that arrive while you are watching and off
    // for history: forty rows all easing in at once when a chat opens is not a
    // nice touch, it is a wave.
    export function appendMessageRow(row, replyTo, reactions, animate, target) {
        (target || chatBox).appendChild(row);
        attachQuote(row, replyTo);
        mountMessageActions(row);
        if (reactions && reactions.length) renderReactions(row, reactions, false);

        if (animate) {
            const quote = row.previousElementSibling;
            if (quote && quote.classList.contains("message-quote")) animateBox(quote, "in");
            animateBox(row, "in");
        }
    }

    // --- popups (the palette and the three-dot menu) -------------------------
    // Both hang off <body> rather than the row: inside #chatBox they would be
    // clipped by its own scrolling at the top and bottom of the pane.
    export let openPopup = null;

    export function closeMessagePopup() {
        if (!openPopup) return;
        openPopup.remove();
        openPopup = null;
    }

    export function showMessagePopup(node, anchor) {
        closeMessagePopup();
        node.classList.add("msg-popup");
        document.body.appendChild(node);

        const r = anchor.getBoundingClientRect();
        const w = node.offsetWidth;
        const h = node.offsetHeight;

        let left = r.left + r.width / 2 - w / 2;
        left = Math.max(8, Math.min(left, window.innerWidth - w - 8));

        // above the button by default, below it when there is no room
        let top = r.top - h - 8;
        if (top < 8) top = Math.min(r.bottom + 8, window.innerHeight - h - 8);

        node.style.left = left + "px";
        node.style.top = top + "px";
        requestAnimationFrame(() => node.classList.add("open"));
        openPopup = node;
    }

    document.addEventListener("click", closeMessagePopup);
    document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeMessagePopup(); });
    window.addEventListener("resize", closeMessagePopup);
    if (chatBox) chatBox.addEventListener("scroll", closeMessagePopup);

    export function openReactionPicker(anchor, id) {
        const box = document.createElement("div");
        box.className = "reaction-picker";

        REACTION_PALETTE.forEach(emoji => {
            const btn = document.createElement("button");
            btn.type = "button";
            btn.className = "reaction-choice";
            btn.textContent = emoji;
            btn.addEventListener("click", (e) => {
                e.stopPropagation();
                closeMessagePopup();
                toggleReaction(id, emoji);
            });
            box.appendChild(btn);
        });

        showMessagePopup(box, anchor);
    }

    export function openMessageMenu(anchor, id) {
        const box = document.createElement("div");
        box.className = "message-menu";

        const reply = document.createElement("button");
        reply.type = "button";
        reply.className = "message-menu-item";
        reply.innerHTML =
            '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" class="menu-icon">' +
            '<path d="M10 8V4L3 11l7 7v-4.1c4.2 0 7 1.2 9 4.1-.8-4.6-3.6-9-9-10z" fill="none" ' +
            'stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/></svg>';
        reply.appendChild(document.createTextNode("Reply"));
        reply.addEventListener("click", (e) => {
            e.stopPropagation();
            closeMessagePopup();
            startReply(id);
        });

        const del = document.createElement("button");
        del.type = "button";
        del.className = "message-menu-item danger";
        del.innerHTML =
            '<svg viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" class="menu-icon">' +
            '<path d="M4 7h16M9.5 7V5.2A1.2 1.2 0 0 1 10.7 4h2.6a1.2 1.2 0 0 1 1.2 1.2V7M6.5 7l.8 12a1.6 1.6 0 0 0 1.6 1.5h6.2a1.6 1.6 0 0 0 1.6-1.5l.8-12" ' +
            'fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/></svg>';
        del.appendChild(document.createTextNode("Delete for me"));
        del.addEventListener("click", (e) => {
            e.stopPropagation();
            closeMessagePopup();
            deleteMessage(id);
        });

        box.appendChild(reply);
        box.appendChild(del);
        showMessagePopup(box, anchor);
    }

    // --- reactions -----------------------------------------------------------
    // Groups the raw list into one entry per emoji: how many chose it, who
    // they were, and whether I am one of them.
    export function groupReactions(list) {
        const groups = new Map();
        (list || []).forEach(r => {
            let g = groups.get(r.emoji);
            if (!g) {
                g = { emoji: r.emoji, count: 0, names: [], mine: false };
                groups.set(r.emoji, g);
            }
            g.count += 1;
            g.names.push(r.nickname || r.userName);
            if ((r.userName || "").toLowerCase() === username) g.mine = true;
        });
        return groups;
    }

    export function buildReactionPill(g) {
        const pill = document.createElement("button");
        pill.type = "button";
        pill.className = "reaction-pill";
        pill.dataset.emoji = g.emoji;

        const face = document.createElement("span");
        face.className = "reaction-emoji";
        face.textContent = g.emoji;
        pill.appendChild(face);

        const count = document.createElement("span");
        count.className = "reaction-count";
        pill.appendChild(count);

        fillReactionPill(pill, g);
        return pill;
    }

    export function fillReactionPill(pill, g) {
        pill.classList.toggle("mine", g.mine);
        pill.title = g.names.join(", ");        // title, not innerHTML - user nicknames
        const count = pill.querySelector(".reaction-count");
        count.textContent = g.count > 1 ? g.count : "";
        count.style.display = g.count > 1 ? "" : "none";
    }

    // Shrinks a pill back into its own middle and then drops it.
    export function removeReactionPill(pill, animate, done) {
        const finish = () => {
            pill.remove();
            if (done) done();
        };

        if (!animate || REDUCED_MOTION.matches) {
            finish();
            return;
        }

        const anim = pill.animate(
            [{ transform: "scale(1)", opacity: 1 }, { transform: "scale(.3)", opacity: 0 }],
            { duration: 180, easing: GROW_EASE, fill: "forwards" });
        anim.onfinish = finish;
        anim.oncancel = finish;
    }

    // Diffed, not rebuilt. Rebuilding meant every pill was a brand new node on
    // every update, so nothing could animate out - and the ones that stayed
    // flickered through their entrance again.
    export function renderReactions(row, list, animate) {
        if (!row) return;
        const shouldAnimate = animate !== false;
        let node = trailingNode(row, "message-reactions");
        const groups = groupReactions(list);

        if (!groups.size) {
            if (!node) return;
            node.querySelectorAll(".reaction-pill")
                .forEach(pill => removeReactionPill(pill, shouldAnimate));
            // the row of pills collapses with them, so the space closes too
            if (shouldAnimate) animateBox(node, "out", () => node.remove());
            else node.remove();
            return;
        }

        let created = false;
        if (!node) {
            node = document.createElement("div");
            node.className = "message-reactions " +
                (row.classList.contains("text-end") ? "sent" : "received");
            row.after(node);                    // first in the trailing block
            created = true;
        }

        const existing = new Map();
        node.querySelectorAll(".reaction-pill").forEach(p => existing.set(p.dataset.emoji, p));

        // gone: shrink away
        existing.forEach((pill, emoji) => {
            if (!groups.has(emoji)) removeReactionPill(pill, shouldAnimate);
        });

        // kept: update in place. new: grow out of the middle.
        groups.forEach(g => {
            const pill = existing.get(g.emoji);
            if (pill) {
                fillReactionPill(pill, g);
                return;
            }
            const fresh = buildReactionPill(g);
            if (shouldAnimate) fresh.classList.add("reaction-appear");
            node.appendChild(fresh);
        });

        // The first reaction on a message brings the whole row into being, so
        // it is the row that opens up rather than each pill separately.
        if (created && shouldAnimate) animateBox(node, "in");
    }

    export function applyReactions(id, list) {
        const row = rowOfMessage(id);
        if (row) renderReactions(row, list || []);
    }

    export async function toggleReaction(id, emoji) {
        const isGroup = currentScopeIsGroup();
        try {
            const res = await fetch("/api/reactions/toggle", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ messageId: id, isGroup, emoji })
            });
            if (!res.ok) return;
            const data = await res.json();
            applyReactions(id, data.reactions);
        } catch (error) {
            console.error("Reaction failed:", error);
        }
    }

    // The server echoes the whole list to everyone who can see the message, so
    // two people reacting at the same moment converge instead of each applying
    // half of the other's change.
    connection.on("ReactionsUpdated", (isGroup, messageId, reactions) => {
        if (!!isGroup !== currentScopeIsGroup()) return;
        applyReactions(messageId, reactions);
    });

    // --- replies -------------------------------------------------------------
    // The quote sits ABOVE the row rather than inside the bubble: a bubble is a
    // pill, and a quote box inside one either fights the radius or has to
    // cancel it.
    export function attachQuote(row, replyTo) {
        if (!row || !replyTo) return;

        const quote = document.createElement("div");
        quote.className = "message-quote " +
            (row.classList.contains("text-end") ? "sent" : "received");
        quote.dataset.target = replyTo.id;
        quote.title = "Go to the message this replies to";

        const who = document.createElement("span");
        who.className = "quote-name";
        who.textContent = replyTo.nickname || replyTo.userName || "";

        const text = document.createElement("span");
        text.className = "quote-text";
        const mark = replyTo.kind === "image" ? "\u{1F4F7} " : (replyTo.kind === "file" ? "\u{1F4CE} " : "");
        text.textContent = mark + (replyTo.text || "");

        quote.appendChild(who);
        quote.appendChild(text);
        row.before(quote);
    }

    export let replyingTo = null;              // {id, who, text}
    export const composerReply = document.getElementById("composerReply");

    // Everything the strip needs is already on screen, so the quote is read
    // back out of the DOM instead of costing a round trip.
    export function describeMessage(id) {
        const bubble = bubbleOfMessage(id);
        if (!bubble) return null;
        const row = bubble.parentElement;
        const mine = row.classList.contains("text-end");

        let who;
        if (mine) who = "yourself";
        else if (currentScopeIsGroup()) who = row.querySelector("strong")?.textContent || row.dataset.sender || "";
        else who = state.selectedUserNickname || state.selectedUser || "";

        const textEl = bubble.querySelector(".message-text");
        let text = textEl ? textEl.textContent.trim() : "";
        if (!text) {
            if (bubble.querySelector(".attachment-image")) text = "\u{1F4F7} Photo";
            else {
                const name = bubble.querySelector(".attachment-file-name");
                if (name) text = "\u{1F4CE} " + name.textContent;
            }
        }

        return { id, who, text };
    }

    export function startReply(id) {
        const described = describeMessage(id);
        if (!described) return;
        replyingTo = described;
        renderReplyStrip();
        const input = document.getElementById("messageInput");
        if (input) input.focus();
    }

    export function clearReply() {
        replyingTo = null;
        renderReplyStrip();
    }

    export function renderReplyStrip() {
        if (!composerReply) return;
        while (composerReply.firstChild) composerReply.removeChild(composerReply.firstChild);

        if (!replyingTo) {
            composerReply.classList.remove("show");
            return;
        }

        composerReply.classList.add("show");

        const chip = document.createElement("div");
        chip.className = "reply-chip";

        const bar = document.createElement("span");
        bar.className = "reply-bar";

        const body = document.createElement("div");
        body.className = "reply-body";

        const name = document.createElement("span");
        name.className = "reply-name";
        name.textContent = "Replying to " + replyingTo.who;

        const text = document.createElement("span");
        text.className = "reply-text";
        text.textContent = replyingTo.text;

        body.appendChild(name);
        body.appendChild(text);

        const cancel = document.createElement("button");
        cancel.type = "button";
        cancel.className = "attachment-chip-remove";   // same two-bar cross as the file chip
        cancel.setAttribute("aria-label", "Cancel reply");
        cancel.addEventListener("click", clearReply);

        chip.appendChild(bar);
        chip.appendChild(body);
        chip.appendChild(cancel);
        composerReply.appendChild(chip);
    }

    // --- delete for me -------------------------------------------------------
    export async function deleteMessage(id) {
        const isGroup = currentScopeIsGroup();
        const url = isGroup ? "/api/group/delete-message" : "/api/messages/delete-message";

        try {
            const res = await fetch(url, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ messageId: id })
            });
            if (!res.ok) {
                console.error("Delete failed:", res.status);
                return;
            }
            removeMessageFromView(id);
        } catch (error) {
            console.error("Delete failed:", error);
        }
    }

    // Takes the row and everything hanging off it: its quote above, its
    // reactions, its timestamp line and any seen-by avatars below.
    export function removeMessageFromView(id) {
        const row = rowOfMessage(id);
        if (!row) return;

        let n = row.nextElementSibling;
        while (isTrailing(n)) {
            const next = n.nextElementSibling;
            n.remove();
            n = next;
        }

        const prev = row.previousElementSibling;
        if (prev && prev.classList.contains("message-quote")) prev.remove();

        row.remove();
        if (replyingTo && replyingTo.id === id) clearReply();
    }

    // Their own other tabs. The other side is never told - this is "remove for
    // me", not "unsend".
    connection.on("MessageDeleted", (isGroup, messageId) => {
        if (!!isGroup !== currentScopeIsGroup()) return;
        removeMessageFromView(messageId);
    });

    // --- one delegated listener for the whole pane ---------------------------
    if (chatBox) {
        chatBox.addEventListener("click", (e) => {
            const action = e.target.closest(".msg-action");
            if (action) {
                e.preventDefault();
                e.stopPropagation();        // or the document listener closes it again
                const row = action.closest(".msg-row");
                const bubble = row ? row.querySelector("[data-message-id]") : null;
                if (!bubble) return;
                const id = parseInt(bubble.dataset.messageId, 10);
                if (!id) return;

                if (action.classList.contains("msg-action-react")) openReactionPicker(action, id);
                else openMessageMenu(action, id);
                return;
            }

            const pill = e.target.closest(".reaction-pill");
            if (pill) {
                e.preventDefault();
                const holder = pill.parentElement;
                const row = holder ? holder.previousElementSibling : null;
                const bubble = row ? row.querySelector("[data-message-id]") : null;
                if (bubble) toggleReaction(parseInt(bubble.dataset.messageId, 10), pill.dataset.emoji);
                return;
            }

            const quote = e.target.closest(".message-quote");
            if (quote) {
                const target = rowOfMessage(quote.dataset.target);
                if (!target) return;
                target.scrollIntoView({ block: "center", behavior: "smooth" });
                target.classList.remove("quote-flash");
                void target.offsetWidth;         // restart the animation
                target.classList.add("quote-flash");
                setTimeout(() => target.classList.remove("quote-flash"), 1400);
            }
        });
    }

