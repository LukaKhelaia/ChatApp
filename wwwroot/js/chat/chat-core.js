// ---------------------------------------------------------------------------
//  ChatApp - chat-core.js
//
//  Connection, page-wide state, and the scroll rules the whole pane obeys.
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



 // Get the logged-in username from Razor (ASP.NET server side)
  // Razor cannot reach a static .js file, so the one server value this whole
  // script needs is stamped on an element in the view and read back here.
  export const rawUsername = document.getElementById("chatBootstrap")?.dataset.username ?? "";
// Normalize: trim spaces and lowercase → canonical format
  export const username = rawUsername.trim().toLowerCase();
// Keep alias for backward compatibility with old code
  export const currentUser = username;

// Track unread private messages (by email/username)
  export const unreadPrivate = new Set(); // 🔔 used for notifications

  // The same for groups. Both are seeded from the server every time the
  // sidebar loads, so a dot survives a reload instead of living only in this
  // page's memory.
  export const unreadGroups = new Set();

// Flags & state
  // NOTE: the last open chat used to be remembered in sessionStorage and
  // re-opened a second or two after load, which meant a reload never landed on
  // the welcome screen. Every conversation is now opened by a click only.
  // Creator, or someone the creator has appointed. Decides whether Add
  // members shows and whether the member rows offer a Kick button.

// Chat container element
  export const chatBox = document.getElementById("chatBox");

  // --- keeping the newest message in view ---------------------------------
  // Attachment photos have no intrinsic size until they load, so immediately
  // after rendering a conversation the pane's scrollHeight is far short of its
  // final value. Scrolling once at that moment lands you near the TOP once the
  // pictures arrive - which is exactly why the first visit to a chat looked
  // wrong and later ones, with the images cached, looked right.
  //
  // So: pin on render, then re-pin as each image finishes, and stop the moment
  // the reader scrolls up so this can never fight them.
  export let stickToBottom = true;

  if (chatBox) {
      chatBox.addEventListener("scroll", () => {
          const fromBottom = chatBox.scrollHeight - chatBox.scrollTop - chatBox.clientHeight;
          stickToBottom = fromBottom < 48;
      });
  }

  export function scrollChatToBottom(force) {
      if (!chatBox) return;
      if (force) stickToBottom = true;
      if (!stickToBottom) return;

      // #chatBox sets scroll-behavior:smooth, which turns this assignment into
      // an ANIMATION. That was quietly poisoning the whole mechanism: the
      // scroll listener above saw the intermediate positions, read them as the
      // reader scrolling away, and cleared stickToBottom - so the next photo to
      // load would not re-pin. Suspend smooth for the jump itself.
      const previous = chatBox.style.scrollBehavior;
      chatBox.style.scrollBehavior = "auto";
      chatBox.scrollTop = chatBox.scrollHeight;
      chatBox.style.scrollBehavior = previous;
  }

  // Binds once per image; ones already complete need no listener.
  export function pinWhileMediaLoads() {
      if (!chatBox) return;
      chatBox.querySelectorAll("img").forEach(img => {
          if (img.dataset.pinBound || img.complete) return;
          img.dataset.pinBound = "1";
          const settle = () => scrollChatToBottom();
          img.addEventListener("load", settle, { once: true });
          img.addEventListener("error", settle, { once: true });
      });
  }


// Fetch current user nickname + avatar from server
   fetch('/Chat/GetNickname')
    .then(response => response.json())
    .then(data => {
        // Display nickname in UI
        document.getElementById('currentUserNickname').innerText = data.nickname;
        // Display avatar in UI
        document.getElementById('currentUserAvatar').src = data.avatarUrl;
    })
    .catch(error => {
        console.error('Failed to load user nickname/avatar:', error);
    });

// Setup SignalR connection
  export const connection = new signalR.HubConnectionBuilder()
    .withUrl("/chathub") // connect to /chathub endpoint
    .withAutomaticReconnect([0, 2000, 5000, 10000, 20000])
    .build();

  // The reconnect and window-focus refreshes used to live here, and they are
  // the reason this file imported from chat-friends and chat-conversations.
  // That import made the base module depend on modules that depend on IT, and
  // a cycle like that decides evaluation order for you: something else ran
  // first and touched `connection` while this const was still in its dead
  // zone. Both handlers now hang off chat-boot.js, which is evaluated last and
  // is allowed to know about everything. This file must stay dependency-free.


