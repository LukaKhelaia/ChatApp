// ---------------------------------------------------------------------------
//  ChatApp - chat-messages.js
//
//  Rendering an arriving message, and the run-grouping that decides which
//  of them wears a face and which carries a name.
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
import { chatBox, connection, pinWhileMediaLoads, scrollChatToBottom, unreadGroups, unreadPrivate, username } from './chat-core.js';
import { buildMessageRow } from './chat-render.js';
import { bumpGroupToTop, bumpPrivateToTop } from './chat-friends.js';
import { playMessageChime } from './chat-notify.js';
import { markConversationRead } from './chat-receipts.js';
import { dayKey, ensureDaySeparator } from './chat-days.js';
import { appendMessageRow } from './chat-actions.js';
import { withPresenceDot } from './chat-presence.js';
import { stopTypingFor } from './chat-typing.js';
import { loadChatPartners, openPrivateChat } from './chat-conversations.js';

  // ======================================================================
  //  RUNS OF MESSAGES
  //  A run is consecutive messages from one person on one day. Only the first
  //  of a run carries the nickname, only the last wears the avatar. A new day
  //  breaks the run even when the sender has not changed - otherwise
  //  yesterday's last message keeps the name that belongs on today's first.
  // ======================================================================

  // The previous message row, skipping everything that is not one: day chips,
  // reply quotes, reaction strips, timestamps, seen-by avatars.
  export function lastMessageRow() {
      let n = chatBox ? chatBox.lastElementChild : null;
      while (n && !n.classList.contains("msg-row")) n = n.previousElementSibling;
      return n;
  }

  // Two timestamps on the same calendar day. A missing or unparseable stamp
  // counts as "no", so it breaks the run rather than silently joining it.
  export function sameDay(a, b) {
      if (!a || !b) return false;
      const x = new Date(a);
      const y = new Date(b);
      if (isNaN(x.getTime()) || isNaN(y.getTime())) return false;
      return dayKey(x) === dayKey(y);
  }

  export function rowDayKey(row) {
      const stamp = row ? row.querySelector("[data-sent-at]") : null;
      if (!stamp) return null;
      const d = new Date(stamp.dataset.sentAt);
      return isNaN(d.getTime()) ? null : dayKey(d);
  }

  // True when an arriving message starts a fresh run.
  export function startsNewRun(sender, when) {
      const prev = lastMessageRow();
      if (!prev) return true;

      // my own rows carry no data-sender, so they break anyone's run
      if ((prev.dataset.sender || "").toLowerCase() !== (sender || "").toLowerCase()) return true;

      const previousDay = rowDayKey(prev);
      const d = new Date(when);
      return !previousDay || isNaN(d.getTime()) || previousDay !== dayKey(d);
  }

  // The face moves down to the newest message of the run, so the one above
  // gives it up and keeps its place with a spacer.
  export function takeAvatarFromPrevious() {
      const prev = lastMessageRow();
      const avatar = prev ? prev.querySelector("img.chat-avatar-small") : null;
      if (!avatar) return;

      const spacer = document.createElement("div");
      spacer.className = "chat-avatar-spacer";
      prev.replaceChild(spacer, avatar);
  }

  export function handleSignalRMessage({ from, message, time, avatarUrl, attachment, messageId, deliveredAt, replyTo }) {
    // Their message landing is proof they have stopped typing. Waiting for the
    // timer would leave "Luka is typing" sitting under the thing Luka just
    // said.
    stopTypingFor(from);

    const fromLower = from.toLowerCase();
    const isSelf = fromLower === username; // true if current user sent this message
    const isChattingWithSender = state.selectedUser?.toLowerCase() === fromLower;

    // Ignore messages that are not from self or the currently selected user
    if (!isSelf && !isChattingWithSender) return;

    ensureDaySeparator(time);

    // A received message is the newest of its run, so it takes the avatar -
    // and if it continues a run, the message above hands it over. Decided
    // before the new row is appended, because it reads the row currently last.
    if (!isSelf && !startsNewRun(from, time)) takeAvatarFromPrevious();

    const row = buildMessageRow({
        id: messageId,
        sender: fromLower,
        text: message,
        attachment: attachment,
        when: time,
        mine: isSelf,
        group: false,
        nickname: from,
        avatarUrl: avatarUrl,
        status: deliveredAt ? "delivered" : "sent",
        readAt: null
    }, { face: true });

    appendMessageRow(row, replyTo, null, true);

    // Always scroll chat to bottom after adding a message
    scrollChatToBottom(true);
            pinWhileMediaLoads();
}

    //  Handle incoming private messages
    connection.on("ReceivePrivateMessage", function (from, message, time, avatarUrl, attachment, messageId, deliveredAt, replyTo, toUser) {
        // Normalize sender username
        const fromLower = (from || "").trim().toLowerCase();
        const selLower = state.selectedUser ? state.selectedUser.toLowerCase() : null;

        console.log("ReceivePrivateMessage fired:", { from, fromLower, message, time, avatarUrl, selectedUser: selLower, username });

        // A sound for every arrival, open chat or not - but never for the echo
        // of my own message coming back from the hub.
        if (fromLower !== username) playMessageChime();

        // Which conversation this belongs to: theirs when they wrote it, the
        // recipient's when the hub is echoing back something I sent. The
        // sender's own copy is why the hub has to name the other party - the
        // payload alone says "from me", which is not a conversation.
        const partnerKey = (fromLower === username)
            ? (toUser || "").trim().toLowerCase()
            : fromLower;
        if (partnerKey) bumpPrivateToTop(partnerKey);

        // The queue exists for ONE case: a message for the conversation that is
        // still being fetched. It used to swallow everything - so with a group
        // open, or nothing open at all, state.isHistoryLoaded was false and every
        // private message went into the queue instead of lighting the unread
        // dot. That is why the dot never appeared until a private chat had
        // been opened at least once.
        const forOpenChat = (fromLower === selLower || fromLower === username);

        if (forOpenChat && !state.isHistoryLoaded) {
            state.messageQueue.push({ from: fromLower, message, time, avatarUrl, attachment, messageId, deliveredAt, replyTo });
            return;
        }

        // If current chat is with this user (or self) → render message immediately
        if (forOpenChat) {
            handleSignalRMessage({ from: fromLower, message, time, avatarUrl, attachment, messageId, deliveredAt, replyTo });
            markConversationRead();
        } else {
            // Otherwise, treat as unread
            unreadPrivate.add(fromLower);

            // Build an ID for the unread dot element
            let dotId = `unread-private-${fromLower}`;
            let dot = document.getElementById(dotId);

            if (!dot) {
                // Sender not in sidebar list yet → create new list item in private chats
                const privateList = document.getElementById('privateChats');
                const li = document.createElement('li');
                li.className = "list-group-item list-group-item-action d-flex align-items-center gap-2";
                li.style.cursor = "pointer";
                li.setAttribute("data-email", fromLower);

                // Avatar image
                const avatar = document.createElement('img');
                avatar.src = avatarUrl || "/images/default-avatar.png";
                avatar.alt = "avatar";
                avatar.className = "chat-avatar";

                // Display sender name (or email fallback)
                const span = document.createElement('span');
                span.textContent = from;

                // Red unread dot
                dot = document.createElement('span');
                dot.className = "unread-dot active";
                dot.id = dotId;

                // Append elements into <li>
                li.appendChild(withPresenceDot(avatar, fromLower));
                li.appendChild(span);
                li.appendChild(dot);

                // Clicking opens chat, clears unread
                li.onclick = () => {
                    openPrivateChat(fromLower, from, avatarUrl); // open chat
                    unreadPrivate.delete(fromLower);  // clear unread state
                    dot.classList.remove("active");   // remove dot
                };

                // Insert new chat entry at top of list
                privateList.insertBefore(li, privateList.firstChild);
                console.log("Dot dynamically created for new user:", dotId);
            } else {
                // User already in list → just activate unread dot
                dot.classList.add("active");
                console.log("Dot activated:", dotId);
            }
        }
    });


    // Handle incoming group messages
    connection.on("ReceiveGroupMessage", function (groupId, sender, nickname, message, time, avatarUrl, attachment, messageId, replyTo) {
        stopTypingFor(sender);

        if ((sender || "").toLowerCase() !== username) playMessageChime();

        // Newest conversation first, whoever sent it.
        bumpGroupToTop(groupId);

        // If current group is open → render directly
        if (state.selectedGroupId === groupId) {
            ensureDaySeparator(time);
            if (messageId > state.latestGroupMessageId) state.latestGroupMessageId = messageId;
            const isSelf = sender.toLowerCase() === username; // check if it's from self

            // Same run as the message above? Decided once, and used for both
            // the name and the face. It was read off chatBox.lastElementChild
            // before, which is no longer a message row at all once that
            // message has a reaction, a timestamp open or a seen-by avatar
            // under it - so every arrival looked like a new run and repeated
            // the nickname.
            const newRun = isSelf ? true : startsNewRun(sender, time);
            if (!isSelf && !newRun) takeAvatarFromPrevious();

            const row = buildMessageRow({
                id: messageId,
                sender: sender,
                text: message,
                attachment: attachment,
                when: time,
                mine: isSelf,
                group: true,
                nickname: nickname,
                avatarUrl: avatarUrl
            }, { face: true, name: newRun });

            // Append message to chat and scroll down
            appendMessageRow(row, replyTo, null, true);
            scrollChatToBottom(true);
            pinWhileMediaLoads();
            markConversationRead();
        } else {
            // If group not open → remember it, then show the dot. The set is
            // what matters: loadChatPartners() rebuilds the sidebar from
            // scratch, so a dot that only lived on the old element vanished
            // the next time anything refreshed the list.
            if ((sender || "").toLowerCase() !== username) unreadGroups.add(groupId);
            const dot = document.getElementById(`unread-group-${groupId}`);
            if (dot) dot.classList.add("active");
        }
    });


