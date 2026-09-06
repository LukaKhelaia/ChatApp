// ---------------------------------------------------------------------------
//  ChatApp - chat-ui.js
//
//  The rest of the shell: collapsible sections, which buttons are on show,
//  the chat menu and the mobile sidebar.
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
import { chatBox } from './chat-core.js';
import { myFriends } from './chat-friends.js';
import { blockedByMe, blockedMe, closeContactModal } from './chat-blocking.js';
import { resetDayTracking } from './chat-days.js';
import { clearReply, closeMessagePopup } from './chat-actions.js';
import { attachBtn, closeAttachMenu } from './chat-composer.js';
import { history } from './chat-history.js';
import { closeSidebar } from './chat-conversations.js';

    //  The three section headers used to carry onclick="toggleSection('x')"
    //  straight in the markup. An inline handler is evaluated in the GLOBAL
    //  scope, and once this file became a module toggleSection stopped being
    //  global - so clicking a header threw ReferenceError and the lists never
    //  opened. They carry data-section now and are wired up here, which is
    //  where the behaviour belonged anyway.
    document.querySelectorAll(".chat-toggle[data-section]").forEach(header => {
        header.addEventListener("click", () => toggleSection(header.dataset.section));
    });

    //  Toggle visibility of a section by ID
    // Runs `fn` with the element's transition switched off, so a style change
    // inside it takes effect immediately instead of animating. The layout
    // flush before the transition comes back is what stops the browser
    // collapsing the whole thing into one animated step.
    export function withoutTransition(section, fn) {
        const previous = section.style.transition;
        section.style.transition = "none";

        const result = fn();

        void section.offsetHeight;
        section.style.transition = previous;
        return result;
    }

    export function toggleSection(id) {
        const section = document.getElementById(id);
        if (!section) return;

        // The open/close animation runs on max-height, from 0 up to the
        // five-and-a-half-row cap. Part way through, the box is briefly
        // shorter than its own content, and since the open state is
        // overflow-y:auto the browser flashed a scrollbar on the way past -
        // even for a single row. Suppressing overflow for the length of the
        // transition removes the flicker; the real scrollbar is back as soon
        // as it settles.
        section.classList.add("animating");
        const opening = !section.classList.contains("open");
        section.classList.toggle("open");

        // The header sits immediately before its list, and CSS has no
        // previous-sibling selector, so the open state is mirrored onto the
        // header here - that is what flips the chevron.
        const header = section.previousElementSibling;
        if (header && header.classList.contains("chat-toggle")) {
            header.classList.toggle("open", opening);
        }

        if (opening) {
            // The cap has to come from the STYLESHEET, and getComputedStyle
            // returns the current value of a property mid-transition - which
            // is what this is, one line after the class that starts it. Read
            // that way it came back as ~0, the old `|| 0` turned that into "no
            // cap at all", and a nine-row list animated to its full 576px
            // before snapping back to 352 when the inline height was released.
            // That snap was the shrink.
            //
            // Clearing the inline height and taking the reading with the
            // transition off makes the stylesheet answer instead. The starting
            // 0 is set in the same breath, or the list would animate backwards
            // from the cap first.
            const cap = withoutTransition(section, () => {
                section.style.maxHeight = "";
                const fromCss = parseFloat(getComputedStyle(section).maxHeight);
                section.style.maxHeight = "0px";
                return Number.isFinite(fromCss) ? fromCss : Infinity;
            });

            section.style.maxHeight = Math.min(section.scrollHeight, cap) + "px";
        } else {
            // Collapse from what is ON SCREEN, not from scrollHeight: for a
            // list longer than the cap those differ, and starting at the full
            // content height made it jump open for a frame before sliding shut.
            const onScreen = Math.round(section.getBoundingClientRect().height);
            withoutTransition(section, () => { section.style.maxHeight = onScreen + "px"; });
            section.style.maxHeight = "0px";
        }

        clearTimeout(section._animTimer);
        const settle = () => {
            clearTimeout(section._animTimer);
            section.removeEventListener("transitionend", onEnd);
            section.classList.remove("animating");
            // hand the height back to the stylesheet so the list keeps working
            // when rows are added or removed later
            section.style.maxHeight = "";
        };
        function onEnd(e) {
            if (e.target === section && e.propertyName === "max-height") settle();
        }
        section.addEventListener("transitionend", onEnd);
        // fallback in case the transition never fires (reduced motion, tab in
        // the background); the CSS duration is .32s
        section._animTimer = setTimeout(settle, 400);
    }

    // Locks the composer when the open private chat is not (or no longer) a
    // friend. The history stays on screen and readable - only sending stops.
    export function updateComposerState() {
        const input = document.getElementById("messageInput");
        const send = document.getElementById("sendButton");
        const notice = document.getElementById("composerNotice");
        if (!input || !send || !notice) return;

        const notFriends = !!state.selectedUser && !myFriends.has(state.selectedUser.toLowerCase());
        const blocked = !!state.selectedUser && (notFriends || blockedByMe || blockedMe);

        // The reason matters: "you blocked them" is fixable from the menu,
        // "they blocked you" is not, and neither is the same as unfriended.
        if (blockedByMe) {
            notice.textContent = "You blocked this user. Unblock them from the ⋯ menu to start messaging again.";
        } else if (blockedMe) {
            notice.textContent = "You can't send messages to this user.";
        } else {
            notice.textContent = "You are not friends with this user, so you can't send messages.";
        }

        input.disabled = blocked;
        input.placeholder = blocked ? "You can't message this user" : "Type a message";
        notice.classList.toggle("show", blocked);

        const attach = document.getElementById("attachBtn");
        if (attach) attach.disabled = blocked;
        if (blocked) closeAttachMenu();

        // Sending is also off while an upload is still in flight, so the id
        // can't be missed off the message.
        send.disabled = blocked || state.attachmentUploading;
    }

    //  Update visibility of buttons depending on chat state
    export function updateDeleteButtonVisibility() {
        const btn = document.getElementById("deleteChatBtn");
        const leaveGroupBtn = document.getElementById("leaveGroupBtn");
        const chatOptionsBtn = document.getElementById("chatOptionsBtn");
        const chatHeader = document.getElementById("chatHeader");
        const addMembersBtn = document.getElementById("addMemberBtn");
        const viewMembersBtn = document.getElementById("viewMembersBtn");
        const unfriendBtn = document.getElementById("unfriendBtn");

        const isGroupChat = !!state.selectedGroupId;
        const isPrivateChat = !!state.selectedUser;
        const isChatOpen = isGroupChat || isPrivateChat;

        // Show delete button if any chat is open
        btn.style.display = isChatOpen ? "flex" : "none";

        // Show leave group only for group chats
        leaveGroupBtn.style.display = isGroupChat ? "flex" : "none";

        // Show options button if chat is open
        chatOptionsBtn.style.display = isChatOpen ? "inline-block" : "none";

        // "Add Members" is for admins only - ordinary members can no longer
        // pull people into a group.
        if (addMembersBtn) {
            addMembersBtn.style.display = (isGroupChat && state.groupIAmAdmin) ? "flex" : "none";
        }

        // "Members" is a group concept - it used to show in private chats too
        if (viewMembersBtn) {
            viewMembersBtn.style.display = isGroupChat ? "flex" : "none";
        }

        // Unfriend and Block moved into the Manage contact modal, which only
        // makes sense in a one-to-one chat.
        const manageBtn = document.getElementById("manageContactBtn");
        if (manageBtn) manageBtn.style.display = isPrivateChat ? "flex" : "none";

        // leaving a group or switching chats must not leave the modal up
        if (!isPrivateChat) closeContactModal();

        // Show chat header only if chat is open
        chatHeader.style.display = isChatOpen ? "flex" : "none";

        // With nothing open the message list and the composer are hidden and
        // the welcome panel takes the pane instead - see .no-chat-open in
        // chat.css. One class drives all of it.
        document.body.classList.toggle("no-chat-open", !isChatOpen);
    }

    // Run once on load so a fresh page (or a reload with no chat restored)
    // starts on the welcome panel rather than an empty composer.
    updateDeleteButtonVisibility();

    //  Clear selected chat (private or group)
    export function clearChatSelection() {
        state.selectedUser = null;
        state.selectedGroupId = null;
        document.getElementById("chatWith").textContent = "";
        chatBox.innerHTML = "";
        resetDayTracking();
        clearReply();
        closeMessagePopup();
        updateDeleteButtonVisibility();
    }

    //  Toggle chat options menu
    document.getElementById("chatOptionsBtn").addEventListener("click", () => {
        const menu = document.getElementById("chatOptionsMenu");
        menu.style.display = menu.style.display === "block" ? "none" : "block";
    });

    //  Close chat options menu if clicking outside
    document.addEventListener("click", (e) => {
        if (!document.getElementById("chatOptionsBtn").contains(e.target)) {
            document.getElementById("chatOptionsMenu").style.display = "none";
        }
    });

    //  Toggle sidebar visibility (mobile)
    document.addEventListener("DOMContentLoaded", function () {
        const toggleBtn = document.getElementById("toggleSidebarBtn");
        const sidebar = document.querySelector(".chat-list");
        const overlay = document.getElementById("sidebarOverlay");

        toggleBtn.addEventListener("click", function () {
            sidebar.classList.toggle("active");
            overlay.classList.toggle("active");
        });

        overlay.addEventListener("click", closeSidebar);
    });

    