// ---------------------------------------------------------------------------
//  ChatApp - chat-composer.js
//
//  The composer: the + menu, auto-growing the field, and keeping the app
//  inside the visible viewport.
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
import { chatBox, connection } from './chat-core.js';
import { MAX_ATTACHMENT_BYTES, formatBytes } from './chat-attachments.js';
import { history } from './chat-history.js';
import { updateComposerState } from './chat-ui.js';

    // --- the composer's + menu ----------------------------------------------
    export const attachBtn = document.getElementById("attachBtn");
    export const attachMenu = document.getElementById("attachMenu");
    export const photoPicker = document.getElementById("photoPicker");
    export const filePicker = document.getElementById("filePicker");
    export const composerAttachment = document.getElementById("composerAttachment");

    export function closeAttachMenu() {
        if (!attachMenu) return;
        attachMenu.classList.remove("open");
        if (attachBtn) attachBtn.setAttribute("aria-expanded", "false");
    }

    if (attachBtn) {
        attachBtn.addEventListener("click", (e) => {
            e.stopPropagation();
            if (attachBtn.disabled) return;
            const open = attachMenu.classList.toggle("open");
            attachBtn.setAttribute("aria-expanded", open ? "true" : "false");
        });
    }

    if (attachMenu) {
        attachMenu.addEventListener("click", (e) => {
            const item = e.target.closest(".attach-menu-item");
            if (!item) return;
            closeAttachMenu();
            (item.dataset.attach === "photo" ? photoPicker : filePicker)?.click();
        });
    }

    document.addEventListener("click", (e) => {
        if (attachMenu && !attachMenu.contains(e.target) && e.target !== attachBtn) {
            closeAttachMenu();
        }
    });

    document.addEventListener("keydown", (e) => {
        if (e.key === "Escape") closeAttachMenu();
    });

    export function renderPendingAttachment(kind, label, detail) {
        if (!composerAttachment) return;
        composerAttachment.innerHTML = "";
        if (!kind) {
            composerAttachment.classList.remove("show", "error");
            return;
        }

        composerAttachment.classList.add("show");
        composerAttachment.classList.toggle("error", kind === "error");

        const chip = document.createElement("div");
        chip.className = "attachment-chip";

        if (kind === "uploading") {
            const spin = document.createElement("span");
            spin.className = "attachment-chip-spinner";
            chip.appendChild(spin);
        }

        const text = document.createElement("span");
        text.className = "attachment-chip-name";
        text.textContent = label;
        chip.appendChild(text);

        if (detail) {
            const d = document.createElement("span");
            d.className = "attachment-chip-size";
            d.textContent = detail;
            chip.appendChild(d);
        }

        if (kind !== "uploading") {
            const remove = document.createElement("button");
            remove.type = "button";
            remove.className = "attachment-chip-remove";
            remove.setAttribute("aria-label", "Remove attachment");
            remove.addEventListener("click", clearPendingAttachment);
            chip.appendChild(remove);
        }

        composerAttachment.appendChild(chip);
    }

    export function clearPendingAttachment() {
        state.pendingAttachment = null;
        state.attachmentUploading = false;
        if (photoPicker) photoPicker.value = "";
        if (filePicker) filePicker.value = "";
        renderPendingAttachment(null);
        updateComposerState();
    }

    export async function uploadPickedFile(file) {
        if (!file) return;

        if (file.size > MAX_ATTACHMENT_BYTES) {
            renderPendingAttachment("error", `"${file.name}" is larger than 10 MB`);
            return;
        }

        state.pendingAttachment = null;
        state.attachmentUploading = true;
        renderPendingAttachment("uploading", file.name, formatBytes(file.size));
        updateComposerState();

        try {
            const body = new FormData();
            body.append("file", file);

            const res = await fetch("/api/attachments/upload", { method: "POST", body });
            if (!res.ok) {
                let reason = "Upload failed.";
                try {
                    const err = await res.json();
                    if (err && err.error) reason = err.error;
                } catch { /* the response wasn't json - keep the generic message */ }
                state.attachmentUploading = false;
                renderPendingAttachment("error", reason);
                updateComposerState();
                return;
            }

            state.pendingAttachment = await res.json();
            state.attachmentUploading = false;
            renderPendingAttachment("ready", state.pendingAttachment.fileName, formatBytes(state.pendingAttachment.size));
        } catch (error) {
            console.error("Attachment upload failed:", error);
            state.attachmentUploading = false;
            renderPendingAttachment("error", "Upload failed. Check your connection and try again.");
        }

        // The pickers keep the previous selection otherwise, so choosing the
        // same file twice in a row would not fire "change".
        if (photoPicker) photoPicker.value = "";
        if (filePicker) filePicker.value = "";
        updateComposerState();
    }

    if (photoPicker) photoPicker.addEventListener("change", (e) => uploadPickedFile(e.target.files[0]));
    if (filePicker) filePicker.addEventListener("change", (e) => uploadPickedFile(e.target.files[0]));

    // --- history loading indicator ------------------------------------------
    // Between picking a chat and its history arriving, the message pane was
    // empty, so the "no messages yet" hint flashed up for every conversation
    // even when it had hundreds. The spinner covers that gap; the hint is
    // suppressed by .history-loading in chat.css while it is up.
    export function showChatLoading() {
        document.body.classList.add("history-loading");
    }

    export function hideChatLoading() {
        document.body.classList.remove("history-loading");
    }

    // --- composer auto-grow -------------------------------------------------
    // A bare <textarea> is a fixed two-row box. The CSS rests it at one line
    // (the same height as the send button); this grows it line by line as the
    // message gets longer and stops at the CSS max-height, after which the
    // textarea scrolls instead of pushing the messages off screen.
    export const composerInput = document.getElementById("messageInput");

    export function autoGrowComposer() {
        if (!composerInput) return;
        const cs = getComputedStyle(composerInput);
        // Collapse first: scrollHeight only ever reports the content height of
        // the box as it currently is, so without this the field could grow but
        // never shrink back when text is deleted. Collapse to 0 rather than
        // "auto" - auto falls back to the rows attribute, not to one line.
        composerInput.style.height = "0px";
        // box-sizing is border-box here and scrollHeight excludes the border.
        const border = parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
        const max = parseFloat(cs.maxHeight) || 150;
        composerInput.style.height = Math.min(composerInput.scrollHeight + border, max) + "px";
    }

    export function resetComposerHeight() {
        if (composerInput) composerInput.style.height = "";   // back to the CSS one-line height
    }

    if (composerInput) {
        composerInput.addEventListener("input", autoGrowComposer);
    }

    // --- keep the app inside the *visible* viewport --------------------------
    // On a phone the on-screen keyboard covers the bottom of the window but
    // does not change what 100vh means, so a composer pinned to the bottom of
    // the shell ends up behind the keyboard. visualViewport reports the part of
    // the page that is actually visible; feeding that into --app-h (which the
    // shell and .row1 are sized from in chat.css) shrinks the app to sit on top
    // of the keyboard instead.
    (function trackVisibleViewport() {
        const vv = window.visualViewport;
        if (!vv) return;                       // desktop fallback: CSS vh/dvh
        const root = document.documentElement;
        const chatBoxEl = document.getElementById("chatBox");
        let frame = 0;

        function apply(keepBottom) {
            cancelAnimationFrame(frame);
            frame = requestAnimationFrame(() => {
                root.style.setProperty("--app-h", vv.height + "px");
                // as the box shrinks, keep the newest message in view
                if (keepBottom && chatBoxEl) chatBoxEl.scrollTop = chatBoxEl.scrollHeight;
            });
        }

        vv.addEventListener("resize", () => apply(true));
        // the visual viewport also pans on iOS when the keyboard opens
        vv.addEventListener("scroll", () => apply(false));
        apply(false);
    })();

    // Open a private chat with a user and load message history
