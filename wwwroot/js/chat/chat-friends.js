// ---------------------------------------------------------------------------
//  ChatApp - chat-friends.js
//
//  Friends: the sidebar list, the request bell, and keeping the chat list
//  ordered by whoever spoke last.
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
import { resetDayTracking } from './chat-days.js';
import { clearReply, closeMessagePopup } from './chat-actions.js';
import { rememberPresence, withPresenceDot } from './chat-presence.js';
import { history } from './chat-history.js';
import { loadChatPartners, openGroupChat, openPrivateChat } from './chat-conversations.js';
import { updateComposerState, updateDeleteButtonVisibility } from './chat-ui.js';

    // ---- friend events --------------------------------------------------
    connection.on("FriendRequestReceived", () => {
        loadFriendRequests();
        const badge = document.getElementById("friendBellBadge");
        badge.classList.add("pulse");
        setTimeout(() => badge.classList.remove("pulse"), 2000);
    });

    connection.on("FriendRequestsUpdated", () => {
        loadFriendRequests();
        refreshSearchResults();
    });

    connection.on("FriendsUpdated", async () => {
        await loadFriends();
        loadFriendRequests();
        loadChatPartners();
        refreshSearchResults();

        // If the other side removed us, keep the history visible but stop
        // sending - the hub would reject the message anyway.
        updateDeleteButtonVisibility();
        updateComposerState();
    });

    connection.on("FriendRequestAccepted", async (who) => {
        console.log(`${who} accepted your friend request`);
        await loadFriends();
        loadFriendRequests();
        loadChatPartners();
        refreshSearchResults();
        updateComposerState();
    });

    connection.on("FriendRequestDeclined", async () => {
        // Their row goes back to a plain "+" instead of staying "Requested".
        await loadFriends();
        loadFriendRequests();
        refreshSearchResults();
    });

    // the hub refuses messages to non-friends
    connection.on("MessageRejected", (reason) => alert(reason));

    //Chat list refresh handler
    // Fired at a user whenever something about their chat LIST changes - a
    // message from someone new, a group being un-hidden. It rebuilds the
    // sidebar and nothing else.
    //
    // It used to also reopen whatever group was on screen, which meant that a
    // private message arriving from anyone tore down the open group
    // conversation and refetched it: the pane blanked, the spinner came up,
    // and the reader lost their place. The message itself arrives through
    // ReceivePrivateMessage / ReceiveGroupMessage, so the open chat needs
    // nothing from here.
    connection.on("ChatListUpdated", () => {
        console.log("Chat list updated event received at " + new Date().toLocaleTimeString());
        loadChatPartners();
    });


    // Handle event when server notifies the client to join a new group
    connection.on("JoinGroupSignalR", function (groupId) {
        console.log(`Joining group SignalR group-${groupId} at ${new Date().toLocaleTimeString()}`);

        // Tell the server hub to add this client to the SignalR group
        connection.invoke("AddToGroupAfterCreation", groupId).then(() => {
            console.log(`Added to SignalR group-${groupId}`);
            // Refresh the chat list so the new group appears in the UI
            loadChatPartners();
        }).catch(err => console.error(err));
    });

    // Handle event when the user is kicked from a group
    connection.on("KickedFromGroup", async function (groupId) {
        console.log(`You were kicked from group ${groupId}`);
        console.log("KickedFromGroup event fired for groupId:", groupId);

        // Remove the group from the chat list UI
        removeGroupFromGroupList(groupId);

        // If the user is currently viewing this group, clear the chat view
        if (state.selectedGroupId === groupId) {
            clearChatView();
            state.selectedGroupId = null;
        }

        // Notify the server hub that the client should leave the SignalR group
        try {
            await connection.invoke("LeaveGroup", Number(groupId));
            console.log(`Left SignalR group ${groupId} after being kicked`);
        } catch (err) {
            console.error("Error leaving SignalR group:", err);
        }
    });

    // Helper function to remove a group from the UI group list
    export function removeGroupFromGroupList(groupId) {
        console.log(`Trying to remove group from UI with ID: ${groupId}`);

        // Find the group list item in the sidebar by data attribute
        const groupElement = document.querySelector(`#groupChats li[data-group-id="${groupId}"]`);
        console.log("Found group element:", groupElement);

        if (groupElement) {
            groupElement.remove();
            console.log("Group element removed.");
        } else {
            console.warn("Group element not found, cannot remove.");
        }
    }

    // Helper function to clear the chat messages view
    export function clearChatView() {
        // The messages container is #chatBox - the old code looked for
        // #messagesContainer, which does not exist, so this never cleared.
        if (chatBox) { chatBox.innerHTML = ""; resetDayTracking(); }
        clearReply();
        closeMessagePopup();
        const chatWith = document.getElementById("chatWith");
        if (chatWith) chatWith.textContent = "";
        updateDeleteButtonVisibility();
    }

    // Start SignalR connection with retry if initial attempt fails
    export async function startConnectionWithRetry() {
        try {
            await connection.start();
            console.log("SignalR Connected");

            // Refresh chat partners immediately after connection
            loadChatPartners();
            loadFriends();
            loadFriendRequests();
        } catch (err) {
            console.warn("SignalR Connection failed, retrying in 2s", err);
            setTimeout(startConnectionWithRetry, 2000);
        }
    }

    // Started from chat-boot.js, which loads last. It used to be called right
    // here, which worked while all of this was one inline <script>: function
    // declarations hoist to the top of their script, so loadChatPartners - four
    // hundred lines further down the page - already existed. Split into files
    // that is no longer true. Hoisting stops at the file boundary, and starting
    // the connection from here means calling a function that has not been
    // parsed yet.

    // User search for starting a private chat
    export const searchBoxEl = document.getElementById("searchBox");

    // The clear button only exists while there is something to clear.
    export const clearSearchBtn = document.getElementById("clearSearch");

    export function updateSearchClear() {
        if (!clearSearchBtn) return;
        clearSearchBtn.classList.toggle("show", searchBoxEl.value.length > 0);
    }

    export function resetSearch() {
        searchBoxEl.value = "";
        const list = document.getElementById("userList");
        if (list) list.innerHTML = "";
        updateSearchClear();
        searchBoxEl.focus();
    }

    if (clearSearchBtn) clearSearchBtn.addEventListener("click", resetSearch);

    // Escape clears it too, which is what the key is for in a search field.
    searchBoxEl.addEventListener("keydown", (e) => {
        if (e.key === "Escape" && searchBoxEl.value) {
            e.preventDefault();
            resetSearch();
        }
    });

    searchBoxEl.addEventListener("input", () => {
        updateSearchClear();
        runUserSearch();
    });

    // Re-render whatever the search box currently shows. Called after any
    // friend event, so a row's button reflects the new relationship instead of
    // sitting on a stale "+" or "Requested" until the page is reloaded.
    export async function refreshSearchResults() {
        if (searchBoxEl.value.trim().length >= 2) await runUserSearch();
    }

    export async function runUserSearch() {
        const term = searchBoxEl.value.trim();
        const userList = document.getElementById("userList");

        // Require at least 2 characters to search
        if (term.length < 2) {
            userList.innerHTML = "";
            return;
        }

        // People and my own groups, asked for together rather than one after
        // the other - the two have nothing to do with each other.
        let users = [];
        let groups = [];
        try {
            const [userRes, groupRes] = await Promise.all([
                fetch(`/api/users/search?term=${encodeURIComponent(term)}`),
                fetch(`/api/group/search?term=${encodeURIComponent(term)}`)
            ]);

            if (!userRes.ok) throw new Error("users " + userRes.status);
            users = await userRes.json();

            // A failed group search should not blank the people results.
            if (groupRes.ok) groups = await groupRes.json();
            else console.warn("Group search failed:", groupRes.status);
        } catch (err) {
            console.error("Search failed:", err);
            return;
        }

        // One round trip for every result's relationship, rather than one each.
        let statuses = {};
        if (users.length) {
            try {
                const sres = await fetch("/api/friends/statuses", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(users.map(u => u.email))
                });
                if (sres.ok) statuses = await sres.json();
            } catch (err) {
                console.error("Friend status lookup failed:", err);
            }
        }

        userList.innerHTML = "";

        const clearSearchBox = () => {
            userList.innerHTML = "";
            searchBoxEl.value = "";
            updateSearchClear();
        };

        if (!users.length && !groups.length) {
            const empty = document.createElement("li");
            empty.className = "search-empty";
            empty.textContent = `No person or group found for "${term}"`;
            userList.appendChild(empty);
            return;
        }

        users.forEach(user => {
            const key = (user.email || "").toLowerCase();
            const status = statuses[key] || "none";

            const li = document.createElement("li");
            li.className = "list-group-item list-group-item-action search-result";

            const avatar = document.createElement("img");
            avatar.src = user.avatarUrl || "/images/default-avatar.png";
            avatar.alt = "avatar";
            avatar.className = "chat-avatar";
            li.appendChild(avatar);

            const name = document.createElement("span");
            name.className = "search-result-name";
            name.textContent = user.nickname || user.email;
            li.appendChild(name);

            const clearSearch = clearSearchBox;

            if (status === "friends") {
                // Already friends -> the row opens the conversation.
                li.classList.add("is-friend");
                li.style.cursor = "pointer";
                li.title = "Open chat";
                li.onclick = () => { openPrivateChat(user.email, user.nickname, user.avatarUrl); clearSearch(); };

                const tag = document.createElement("span");
                tag.className = "search-result-tag";
                tag.textContent = "Chat";
                li.appendChild(tag);
            } else if (status === "outgoing") {
                const tag = document.createElement("span");
                tag.className = "search-result-tag muted";
                tag.textContent = "Requested";
                li.appendChild(tag);
            } else if (status === "incoming") {
                // They already asked us - accept straight from the search row.
                const accept = document.createElement("button");
                accept.className = "icon-btn accept";
                accept.title = "Accept friend request";
                accept.textContent = "✓";
                accept.onclick = async (e) => {
                    e.stopPropagation();
                    await postFriend("/api/friends/accept", user.email);
                    await loadFriends();
                    loadFriendRequests();
                    loadChatPartners();
                    refreshSearchResults();
                };
                li.appendChild(accept);
            } else {
                const add = document.createElement("button");
                add.className = "icon-btn add";
                add.title = "Add friend";
                add.textContent = "+";
                add.onclick = async (e) => {
                    e.stopPropagation();
                    const ok = await postFriend("/api/friends/request", user.email);
                    if (ok) {
                        add.replaceWith(Object.assign(document.createElement("span"), {
                            className: "search-result-tag muted",
                            textContent: "Requested"
                        }));
                    }
                };
                li.appendChild(add);
            }

            userList.appendChild(li);
        });

        // People first, groups after them: the search box is mainly how you
        // find a person, and a group named like someone should not push that
        // person below the fold.
        groups.forEach(group => {
            const li = document.createElement("li");
            li.className = "list-group-item list-group-item-action search-result is-friend";
            li.style.cursor = "pointer";
            li.title = "Open group";

            const avatar = document.createElement("img");
            avatar.src = group.imageUrl || "/images/default-group.png";
            avatar.onerror = () => { avatar.onerror = null; avatar.src = "/images/default-group.png"; };
            avatar.alt = "";
            avatar.className = "chat-image";
            li.appendChild(avatar);

            const name = document.createElement("span");
            name.className = "search-result-name";
            name.textContent = group.name;
            li.appendChild(name);

            const tag = document.createElement("span");
            tag.className = "search-result-tag";
            tag.textContent = "Group";
            li.appendChild(tag);

            li.onclick = () => {
                openGroupChat(group.groupId, group.name, group.imageUrl);
                clearSearchBox();
            };

            userList.appendChild(li);
        });
    }

    // Small helper for the friend endpoints - they all take { userName }.
    export async function postFriend(url, userName) {
        try {
            const res = await fetch(url, {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ userName })
            });
            if (!res.ok) {
                alert(await res.text() || "Request failed");
                return false;
            }
            return true;
        } catch (err) {
            console.error("Friend action failed:", err);
            return false;
        }
    }

    // ---- friends list in the sidebar ------------------------------------
    // Kept in sync by loadFriends() so the UI can tell when the person you are
    // chatting with is no longer a friend.
    export let myFriends = new Set();

    export async function loadFriends() {
        try {
            const res = await fetch("/api/friends/list");
            if (!res.ok) return;
            const friends = await res.json();
            myFriends = new Set(friends.map(f => (f.email || "").toLowerCase()));
            updateComposerState();

            const list = document.getElementById("friendsList");
            list.innerHTML = "";

            if (!friends.length) {
                const empty = document.createElement("li");
                empty.className = "list-group-item empty-hint";
                empty.textContent = "No friends yet - search above to add one";
                list.appendChild(empty);
                return;
            }

            friends.forEach(f => {
                rememberPresence(f.email, f.online, f.lastSeen);

                const li = document.createElement("li");
                li.className = "list-group-item list-group-item-action d-flex align-items-center gap-2";
                li.style.cursor = "pointer";
                li.onclick = () => openPrivateChat(f.email, f.nickname, f.avatarUrl);

                const avatar = document.createElement("img");
                avatar.src = f.avatarUrl || "/images/default-avatar.png";
                avatar.alt = "avatar";
                avatar.className = "chat-avatar";

                const span = document.createElement("span");
                span.textContent = f.nickname || f.email;

                li.appendChild(withPresenceDot(avatar, f.email));
                li.appendChild(span);
                list.appendChild(li);
            });
        } catch (err) {
            console.error("Error loading friends:", err);
        }
    }

    // ---- friend requests: bell badge + dropdown -------------------------
    export async function loadFriendRequests() {
        try {
            const res = await fetch("/api/friends/requests");
            if (!res.ok) return;
            const requests = await res.json();

            const badge = document.getElementById("friendBellBadge");
            const body = document.getElementById("friendRequestsBody");

            badge.textContent = requests.length ? String(requests.length) : "";
            badge.classList.toggle("active", requests.length > 0);

            body.innerHTML = "";

            if (!requests.length) {
                const empty = document.createElement("div");
                empty.className = "friend-request-empty";
                empty.textContent = "No pending requests";
                body.appendChild(empty);
                return;
            }

            requests.forEach(r => {
                const row = document.createElement("div");
                row.className = "friend-request";

                const avatar = document.createElement("img");
                avatar.src = r.avatarUrl || "/images/default-avatar.png";
                avatar.alt = "avatar";
                avatar.className = "friend-request-avatar";

                const name = document.createElement("span");
                name.className = "friend-request-name";
                name.textContent = r.nickname || r.email;

                const accept = document.createElement("button");
                accept.className = "icon-btn accept";
                accept.title = "Accept";
                accept.textContent = "✓";
                accept.onclick = async () => {
                    if (await postFriend("/api/friends/accept", r.email)) {
                        await loadFriends();
                        loadFriendRequests();
                        loadChatPartners();
                        refreshSearchResults();
                    }
                };

                const decline = document.createElement("button");
                decline.className = "icon-btn decline";
                decline.title = "Decline";
                decline.textContent = "✕";
                decline.onclick = async () => {
                    if (await postFriend("/api/friends/decline", r.email)) {
                        loadFriendRequests();
                        refreshSearchResults();
                    }
                };

                row.appendChild(avatar);
                row.appendChild(name);
                row.appendChild(accept);
                row.appendChild(decline);
                body.appendChild(row);
            });
        } catch (err) {
            console.error("Error loading friend requests:", err);
        }
    }

    // bell open/close
    document.getElementById("friendBellBtn").addEventListener("click", (e) => {
        e.stopPropagation();
        const panel = document.getElementById("friendRequestsPanel");
        const opening = !panel.classList.contains("open");
        panel.classList.toggle("open", opening);
        if (opening) loadFriendRequests();
    });

    document.addEventListener("click", (e) => {
        const bell = document.getElementById("friendBell");
        if (bell && !bell.contains(e.target)) {
            document.getElementById("friendRequestsPanel").classList.remove("open");
        }
    });

    // ======================================================================
    //  MOST RECENT FIRST
    //  The server sorts both lists by last activity on load; these keep that
    //  true afterwards, without refetching the whole sidebar for every
    //  message that arrives.
    // ======================================================================
    export function moveToTop(list, li) {
        if (!list || !li) return;
        if (list.firstElementChild === li) return;      // already there
        list.insertBefore(li, list.firstElementChild);
    }

    export function bumpPrivateToTop(userKey) {
        const list = document.getElementById("privateChats");
        if (!list || !userKey) return;

        // data-email keeps the server's casing, so match on a lowered copy
        // rather than with an attribute selector.
        const li = Array.from(list.children).find(
            row => (row.getAttribute("data-email") || "").trim().toLowerCase() === userKey);

        if (!li) {
            // First message to or from someone who is not in the list yet.
            // Their nickname and avatar are not in this payload, so refetch
            // rather than invent a row - it lands at the top either way,
            // because the server sorts by last activity.
            loadChatPartners();
            return;
        }

        moveToTop(list, li);
    }

    export function bumpGroupToTop(groupId) {
        const list = document.getElementById("groupChats");
        if (!list) return;

        const li = Array.from(list.children).find(
            row => String(row.getAttribute("data-group-id")) === String(groupId));

        moveToTop(list, li);
    }

