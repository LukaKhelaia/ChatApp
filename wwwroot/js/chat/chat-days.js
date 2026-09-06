// ---------------------------------------------------------------------------
//  ChatApp - chat-days.js
//
//  Date chips and message clocks.
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

import { chatBox } from './chat-core.js';
import { statusLabel } from './chat-receipts.js';
import { history } from './chat-history.js';

    // ======================================================================
    //  DAY SEPARATORS AND MESSAGE TIMES
    // ======================================================================
    // A day tracker remembers the day of the last message it saw, so it can
    // tell when a new chip is due. The pane has one; a page of older history
    // being drawn off-screen gets its own, because it is a separate run of
    // messages and must not be judged against what is already on screen.
    export function makeDayTracker() {
        let last = null;

        return {
            reset() { last = null; },

            // Appends a centred date chip to `target`, but only when the day
            // has actually changed - so it shows once per day, not once per
            // message. Call it immediately before appending a message row.
            ensure(when, target) {
                const d = when ? new Date(when) : new Date();
                if (isNaN(d.getTime())) return;

                const key = dayKey(d);
                if (key === last) return;
                last = key;

                const row = document.createElement("div");
                row.className = "day-separator";
                row.dataset.day = key;
                const chip = document.createElement("span");
                chip.textContent = dayLabel(d);
                row.appendChild(chip);
                (target || chatBox).appendChild(row);
            }
        };
    }

    // The pane's own tracker. The live path and the newest page share it;
    // clearing the pane resets it, otherwise the first message of a newly
    // opened chat would be judged against the previous conversation's last day.
    export const paneDays = makeDayTracker();

    export function resetDayTracking() {
        paneDays.reset();
    }

    export function dayKey(d) {
        return d.getFullYear() + "-" + d.getMonth() + "-" + d.getDate();
    }

    export function dayLabel(d) {
        const today = new Date();
        const yesterday = new Date();
        yesterday.setDate(today.getDate() - 1);

        if (dayKey(d) === dayKey(today)) return "Today";
        if (dayKey(d) === dayKey(yesterday)) return "Yesterday";

        // Within the last week the weekday name is easier to place than a date.
        // Compare CALENDAR days, not raw milliseconds: a message from 7 days
        // ago at 23:00 is only 6.4 raw days old at 08:00 this morning, and
        // labelling it with a weekday would print today's own weekday name.
        const startOfDay = x => new Date(x.getFullYear(), x.getMonth(), x.getDate());
        const days = Math.round((startOfDay(today) - startOfDay(d)) / 86400000);
        if (days > 1 && days < 7) return d.toLocaleDateString(undefined, { weekday: "long" });

        return d.toLocaleDateString(undefined, {
            day: "numeric",
            month: "long",
            year: d.getFullYear() === today.getFullYear() ? undefined : "numeric"
        });
    }

    export function ensureDaySeparator(when, target) {
        paneDays.ensure(when, target);
    }

    export function formatClock(when) {
        const d = when ? new Date(when) : null;
        if (!d || isNaN(d.getTime())) return "";
        return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
    }

    // The row's trailing block is [message row][.message-meta][.read-receipts].
    // Both extras are siblings of the row rather than children, because the
    // received rows are flex containers - a child would land beside the bubble
    // instead of under it.
    // Everything that trails a message row, in the order it is stacked:
    // [row][.message-reactions][.message-meta][.read-receipts]. Kept as one
    // list so a new member can never be forgotten by one of the three walks
    // below - which is exactly how the reactions row would have ended up
    // orphaned between a message and its own timestamp.
    export const TRAILING_CLASSES = ["message-reactions", "message-meta", "read-receipts"];

    export function isTrailing(node) {
        return !!node && TRAILING_CLASSES.some(c => node.classList.contains(c));
    }

    // The row's own trailing element of that class, if it has one.
    export function trailingNode(row, cls) {
        let n = row ? row.nextElementSibling : null;
        while (isTrailing(n)) {
            if (n.classList.contains(cls)) return n;
            n = n.nextElementSibling;
        }
        return null;
    }

    // The last element of the row's trailing block - what a new one goes after.
    export function trailingTail(row) {
        let tail = row;
        let n = row ? row.nextElementSibling : null;
        while (isTrailing(n)) {
            tail = n;
            n = n.nextElementSibling;
        }
        return tail;
    }

    export function metaFor(bubble) {
        const row = bubble.parentElement;
        if (!row) return null;
        return trailingNode(row, "message-meta");
    }

    // "20:12" for anyone else's message, "20:12 - Seen 20:13" for one of mine.
    export function metaText(bubble) {
        const parts = [formatClock(bubble.dataset.sentAt)];
        if (bubble.dataset.status) {
            parts.push(statusLabel(bubble.dataset.status));
        }
        return parts.filter(Boolean).join(" · ");
    }

    export function closeMeta(node) {
        node.classList.remove("open");
        // drop it once the collapse has finished, so nothing accumulates
        const done = () => node.remove();
        node.addEventListener("transitionend", done, { once: true });
        setTimeout(done, 400);              // fallback if the transition is skipped
    }

    export function toggleMeta(bubble) {
        const existing = metaFor(bubble);
        if (existing) {
            closeMeta(existing);
            return;
        }

        const row = bubble.parentElement;
        if (!row) return;

        const node = document.createElement("div");
        node.className = "message-meta " + (row.classList.contains("text-end") ? "sent" : "received");
        node.textContent = metaText(bubble);
        // below the reactions if there are any, so the stack reads
        // message -> reactions -> time -> who has seen it
        (trailingNode(row, "message-reactions") || row).after(node);

        // one frame collapsed, so the transition to .open actually runs
        requestAnimationFrame(() => node.classList.add("open"));
    }

    // Clicking a bubble reveals when it was sent. Delegated, so it covers every
    // message including ones that arrive later; clicks on an attachment link
    // are left alone so opening a photo still works.
    if (chatBox) {
        chatBox.addEventListener("click", (e) => {
            if (e.target.closest("a")) return;
            const bubble = e.target.closest("[data-sent-at]");
            if (!bubble) return;
            toggleMeta(bubble);
        });
    }

