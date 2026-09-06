// ---------------------------------------------------------------------------
//  ChatApp - chat-receipts.js
//
//  Sent / delivered / seen, and the reader avatars a group hangs off the
//  last message each member has read.
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
import { chatBox, connection, username } from './chat-core.js';
import { metaFor, metaText, trailingNode, trailingTail } from './chat-days.js';
import { animateBox, whenArrivalsSettle } from './chat-actions.js';
import { loadMembersAndRender } from './chat-groups.js';
import { updateDeleteButtonVisibility } from './chat-ui.js';

    // ======================================================================
    //  DELIVERY AND READ RECEIPTS
    // ======================================================================
    // "Seen" is only claimed when the conversation is open AND the window is
    // actually focused, so a message sitting in a background tab stays at
    // "Delivered". Anything looser would be lying to the sender.

    export function windowIsFocused() {
        return document.visibilityState === "visible" && document.hasFocus();
    }

    export function statusLabel(status) {
        if (status === "seen") return "Seen";
        if (status === "delivered") return "Delivered";
        return "Sent";
    }

    // Writes the little Sent / Delivered / Seen line into one of my own
    // bubbles. Hidden until the bubble is clicked, same as the timestamp.
    export function applyOwnStatus(bubble, status, when) {
        if (!bubble) return;
        bubble.dataset.status = status;

        // if the line happens to be open, keep it current
        const open = metaFor(bubble);
        if (open) open.textContent = metaText(bubble);
    }

    export function ownBubbles() {
        return Array.from(chatBox.querySelectorAll(".text-end > [data-sent-at]"));
    }

    // The row holding the newest message with an id at or below `id`.
    //
    // `exceptSender` skips that person's own messages. Sending is reading, so
    // without it a reader's face simply trailed their own messages down the
    // pane - which tells you nothing. What it should mark is the last thing
    // SOMEONE ELSE said that they have seen.
    export function rowForMessageId(id, exceptSender) {
        const skip = (exceptSender || "").toLowerCase();
        let found = null;
        chatBox.querySelectorAll("[data-message-id]").forEach(b => {
            const mid = parseInt(b.dataset.messageId, 10);
            if (!mid || mid > id) return;

            const row = b.parentElement;
            const sender = (row.dataset.sender || "").toLowerCase();
            if (skip && sender === skip) return;

            found = row;
        });
        return found;
    }

    // Moves one reader's little avatar to sit under the last message they have
    // read - the Messenger behaviour. Removing first means a reader only ever
    // appears in one place.
    export function placeReaderAvatar(userName, nickname, avatarUrl, anchorRow) {
        // never mid-arrival - see whenArrivalsSettle()
        whenArrivalsSettle(() => placeReaderAvatarNow(userName, nickname, avatarUrl, anchorRow));
    }

    export function placeReaderAvatarNow(userName, nickname, avatarUrl, anchorRow) {
        chatBox.querySelectorAll(".read-avatar").forEach(node => {
            if (node.dataset.user !== userName) return;
            const holder = node.parentElement;
            node.remove();
            if (holder && holder.classList.contains("read-receipts") && !holder.childElementCount) {
                // collapse the empty row instead of yanking it out, so the
                // messages above do not jump the height of a face
                animateBox(holder, "out", () => holder.remove());
            }
        });

        // the row may have been deleted while this was waiting its turn
        if (!anchorRow || !anchorRow.isConnected) return;

        // find an existing receipts row anywhere in this row's trailing block
        let holder = trailingNode(anchorRow, "read-receipts");
        let opened = false;
        if (!holder) {
            holder = document.createElement("div");
            holder.className = "read-receipts";
            trailingTail(anchorRow).after(holder);
            opened = true;
        }

        // Under one of MY messages the faces sit below the bubble in a row of
        // their own, the way they always did - my bubbles run to the right
        // edge, so a weightless overlay would land on top of them. Under
        // someone else's, that side of the pane is empty, so they hang there
        // and cost no space at all.
        const underMine = anchorRow.classList.contains("text-end");
        holder.classList.toggle("after-sent", underMine);
        holder.classList.toggle("after-received", !underMine);

        const img = document.createElement("img");
        img.className = "read-avatar";
        img.dataset.user = userName;
        img.src = avatarUrl || "/images/default-avatar.png";
        img.alt = nickname || userName;
        img.title = "Seen by " + (nickname || userName);
        holder.appendChild(img);

        if (opened) animateBox(holder, "in");
    }

    // Tell the server we have read what is on screen. Safe to call often - the
    // hub ignores it when nothing has actually moved.
    export function markConversationRead() {
        if (!windowIsFocused()) return;
        if (connection.state !== signalR.HubConnectionState.Connected) return;

        if (state.selectedUser) {
            connection.invoke("MarkPrivateRead", state.selectedUser).catch(err =>
                console.error("MarkPrivateRead failed:", err));
        } else if (state.selectedGroupId && state.latestGroupMessageId > 0) {
            connection.invoke("MarkGroupRead", state.selectedGroupId, state.latestGroupMessageId).catch(err =>
                console.error("MarkGroupRead failed:", err));
        }
    }

    window.addEventListener("focus", markConversationRead);
    document.addEventListener("visibilitychange", markConversationRead);

    // Load where everyone else has read up to in this group.
    export async function loadGroupReads(groupId) {
        try {
            const res = await fetch(`/api/group/reads?groupId=${encodeURIComponent(groupId)}`);
            if (!res.ok) return;
            const reads = await res.json();
            reads.forEach(r =>
                placeReaderAvatar(r.userName, r.nickname, r.avatarUrl,
                                  rowForMessageId(r.lastReadMessageId, r.userName)));
        } catch (error) {
            console.error("Failed to load group read state:", error);
        }
    }

    // The other side read the private conversation. Reading clears everything
    // outstanding at once, so every one of my bubbles here becomes Seen.
    connection.on("PrivateMessagesRead", (reader, readAt) => {
        const who = (reader || "").toLowerCase();
        if (!state.selectedUser || state.selectedUser.toLowerCase() !== who) return;

        const mine = ownBubbles();
        mine.forEach(b => applyOwnStatus(b, "seen", readAt));

        const last = mine[mine.length - 1];
        placeReaderAvatar(who, state.selectedUserNickname, state.selectedUserAvatar,
                          last ? last.parentElement : null);
    });

    // Someone's role changed. Everyone in the group hears it: the member list
    // may be open on any screen, and the person themselves needs their own
    // buttons to appear without reloading the page.
    connection.on("GroupAdminChanged", (groupId, userName, isAdmin) => {
        if (state.selectedGroupId !== groupId) return;

        if ((userName || "").toLowerCase() === username) {
            state.groupIAmAdmin = !!isAdmin;
            updateDeleteButtonVisibility();
        }

        const modal = document.getElementById("membersModal");
        if (modal && modal.style.display === "flex") loadMembersAndRender(groupId);
    });

    connection.on("GroupMessagesRead", (groupId, userName, lastMessageId, nickname, avatarUrl) => {
        if (state.selectedGroupId !== groupId) return;
        const who = (userName || "").toLowerCase();
        if (who === username) return;          // my own face on my own messages
        placeReaderAvatar(who, nickname, avatarUrl, rowForMessageId(lastMessageId, who));
    });

