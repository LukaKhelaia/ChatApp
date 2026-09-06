// ---------------------------------------------------------------------------
//  ChatApp - chat-blocking.js
//
//  Blocking, which is directional and independent of friendship.
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
import { updateComposerState } from './chat-ui.js';

    // ======================================================================
    //  BLOCKING
    // ======================================================================
    // Directional and independent of friendship. A block in either direction
    // stops messages; the conversation itself stays in both chat lists so it
    // can be unblocked from the same menu.
    export let blockedByMe = false;    // I blocked them
    export let blockedMe = false;      // they blocked me

    export function resetBlockState() {
        blockedByMe = false;
        blockedMe = false;
        updateBlockButton();
    }

    export function updateBlockButton() {
        const btn = document.getElementById("blockBtn");
        const label = document.getElementById("blockBtnLabel");
        if (!btn || !label) return;
        label.textContent = blockedByMe ? "Unblock" : "Block";
        btn.classList.toggle("is-unblock", blockedByMe);
    }

    export async function loadBlockState(userName) {
        blockedByMe = false;
        blockedMe = false;
        try {
            const res = await fetch(`/api/blocks/status?withUser=${encodeURIComponent(userName)}`);
            if (res.ok) {
                const blockState = await res.json();
                blockedByMe = !!blockState.blockedByMe;
                blockedMe = !!blockState.blockedMe;
            }
        } catch (error) {
            console.error("Failed to load block blockState:", error);
        }
        updateBlockButton();
        updateComposerState();
    }

    export async function setBlocked(shouldBlock) {
        if (!state.selectedUser) return;
        const target = state.selectedUser;
        try {
            const res = await fetch(`/api/blocks/${shouldBlock ? "block" : "unblock"}`, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ userName: target })
            });
            if (!res.ok) {
                console.error("Block request failed:", res.status);
                return;
            }
            if (state.selectedUser === target) {
                blockedByMe = shouldBlock;
                updateBlockButton();
                updateComposerState();
            }
        } catch (error) {
            console.error("Block request failed:", error);
        }
    }

    export function openContactModal() {
        if (!state.selectedUser) return;
        document.getElementById("chatOptionsMenu").style.display = "none";
        document.getElementById("contactModalName").textContent = state.selectedUserNickname || state.selectedUser;
        document.getElementById("contactModalAvatar").src = state.selectedUserAvatar || "/images/default-avatar.png";
        updateBlockButton();
        document.getElementById("contactModal").style.display = "flex";
    }

    export function closeContactModal() {
        document.getElementById("contactModal").style.display = "none";
    }

    document.getElementById("manageContactBtn")?.addEventListener("click", openContactModal);
    document.getElementById("closeContactModal")?.addEventListener("click", closeContactModal);

    // click the dark backdrop to dismiss, but not the card itself
    document.getElementById("contactModal")?.addEventListener("click", (e) => {
        if (e.target.id === "contactModal") closeContactModal();
    });

    document.addEventListener("keydown", (e) => {
        if (e.key === "Escape") closeContactModal();
    });

    document.getElementById("blockBtn")?.addEventListener("click", () => {
        if (!state.selectedUser) return;
        if (blockedByMe) {
            setBlocked(false);
            closeContactModal();
            return;
        }
        if (confirm("Block this user? Neither of you will be able to send messages until you unblock them.")) {
            setBlocked(true);
            closeContactModal();
        }
    });

    // Either side changing the block state re-checks, so the composer locks or
    // unlocks without waiting for a reload.
    connection.on("BlockStateChanged", (withUser) => {
        const who = (withUser || "").toLowerCase();
        if (state.selectedUser && state.selectedUser.toLowerCase() === who) {
            loadBlockState(state.selectedUser);
        }
    });

