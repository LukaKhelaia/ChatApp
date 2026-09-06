// ---------------------------------------------------------------------------
//  ChatApp - chat-boot.js
//
//  The entry point. The <script type="module"> tag in Chat.cshtml names this
//  file and nothing else; everything else is reached through imports.
//
//  Two jobs. First, pull in the modules that exist for their side effects -
//  the ones that register SignalR handlers and DOM listeners at load and that
//  nothing else imports from. Without a mention here they would simply never
//  be evaluated, and the feature would be silently absent.
//
//  Second, start the connection. That has to happen last, after every module
//  has been evaluated, because startConnectionWithRetry reaches across most of
//  them on success.
// ---------------------------------------------------------------------------

// evaluated for their side effects: handlers, listeners, one-time setup
import './chat-core.js';
import './chat-render.js';
import './chat-messages.js';
import './chat-notify.js';
import './chat-blocking.js';
import './chat-receipts.js';
import './chat-days.js';
import './chat-actions.js';
import './chat-attachments.js';
import './chat-text.js';
import './chat-composer.js';
import './chat-presence.js';
import './chat-typing.js';
import './chat-history.js';
import './chat-conversations.js';
import './chat-groups.js';
import './chat-ui.js';

import { connection } from './chat-core.js';
import { startConnectionWithRetry, loadFriends, loadFriendRequests } from './chat-friends.js';
import { loadChatPartners } from './chat-conversations.js';

// After a reconnect the server re-runs OnConnectedAsync, so just refresh the UI.
// This lives here rather than next to the connection it belongs to, because
// chat-core.js has to stay dependency-free - see the note in that file.
connection.onreconnected(() => {
    console.log("SignalR reconnected");
    loadChatPartners();
    loadFriends();
    loadFriendRequests();
});

// A dropped socket while the tab was in the background can lose an event, so
// re-sync whenever the window comes back to the foreground.
window.addEventListener("focus", () => {
    loadFriends();
    loadFriendRequests();
});

// Connect, then fill the sidebar.
startConnectionWithRetry();
