// ---------------------------------------------------------------------------
//  ChatApp - chat-conversations.js
//
//  Opening a conversation - private or group - and loading the sidebar.
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
import { chatBox, pinWhileMediaLoads, scrollChatToBottom, unreadGroups, unreadPrivate } from './chat-core.js';
import { handleSignalRMessage } from './chat-messages.js';
import { loadBlockState, resetBlockState } from './chat-blocking.js';
import { loadGroupReads, markConversationRead, ownBubbles, placeReaderAvatar } from './chat-receipts.js';
import { paneDays, resetDayTracking } from './chat-days.js';
import { clearReply, closeMessagePopup } from './chat-actions.js';
import { clearPendingAttachment, hideChatLoading, showChatLoading } from './chat-composer.js';
import { paintHeaderPresence, presence, rememberPresence, withPresenceDot } from './chat-presence.js';
import { clearTyping } from './chat-typing.js';
import { history, pageMessages, rememberCursor, renderGroupPage, renderPrivatePage, resetHistoryCursor } from './chat-history.js';
import { updateComposerState, updateDeleteButtonVisibility } from './chat-ui.js';


    //  On a narrow screen the chat list is a slide-over panel. Picking a
    //  conversation is the whole reason it was opened, so it gets itself out
    //  of the way - from the Chats list, the Groups list, the Friends list or
    //  a search result alike, since every one of them comes through here. On a
    //  wide screen the panel is always on show and never carries .active, so
    //  this does nothing.
    export function closeSidebar() {
        const sidebar = document.querySelector(".chat-list");
        const overlay = document.getElementById("sidebarOverlay");
        if (sidebar) sidebar.classList.remove("active");
        if (overlay) overlay.classList.remove("active");
    }

    export function openPrivateChat(email, nickname = null, avatarUrl = null) {
        closeSidebar();
        state.selectedUser = email.toLowerCase();
        clearPendingAttachment();      // a file picked for one chat shouldn't follow you into another
        clearReply();                  // and neither should a half-written reply
        closeMessagePopup();
        state.selectedGroupId = null;
        state.latestGroupMessageId = 0;
        // kept for the "seen by" avatar, which needs their face outside the header
        state.selectedUserAvatar = avatarUrl || "/images/default-avatar.png";
        state.selectedUserNickname = nickname || email;
        resetBlockState();
        loadBlockState(state.selectedUser);

        // Update the chat header with the avatar and name of the user
        const chatWithEl = document.getElementById("chatWith");
        chatWithEl.innerHTML = ""; // clear any previous header content

        const avatarImg = document.createElement("img");
        avatarImg.src = avatarUrl || "/images/default-avatar.png";
        avatarImg.alt = nickname || email;
        // No me-2: the gap to the name now lives on .avatar-wrap. A margin on
        // the image itself sits INSIDE the wrapper, which widens the box the
        // presence dot is anchored to - and the dot drifts off into the gap.
        avatarImg.className = "chat-avatar-small";

        // Name over status, stacked, so the header stays one line tall.
        const nameWrap = document.createElement("span");
        nameWrap.className = "chat-head-name";

        const nameSpan = document.createElement("span");
        nameSpan.textContent = nickname || email;

        const statusSpan = document.createElement("span");
        statusSpan.className = "chat-head-status";
        statusSpan.id = "chatHeadStatus";

        nameWrap.appendChild(nameSpan);
        nameWrap.appendChild(statusSpan);

        chatWithEl.appendChild(withPresenceDot(avatarImg, email));
        chatWithEl.appendChild(nameWrap);
        paintHeaderPresence();

        // Clear unread state for this user
        unreadPrivate.delete(state.selectedUser);
        const dotEl = document.getElementById(`unread-private-${state.selectedUser}`);
        if (dotEl) dotEl.classList.remove("active");

        // Reset chat box and state variables
        updateDeleteButtonVisibility();
        updateComposerState();
        chatBox.innerHTML = "";
        resetDayTracking();
        state.isHistoryLoaded = false;
        state.messageQueue = [];
        resetHistoryCursor();
        clearTyping();
        showChatLoading();

        // Fetch the newest page of history with this user
        fetch(`/api/messages/history?withUser=${encodeURIComponent(email)}`)
            .then(res => res.json())
            .then(page => {
                const messages = pageMessages(page);
                rememberCursor(page);
                renderPrivatePage(messages, chatBox, paneDays);

                // Scroll to bottom after loading history
                scrollChatToBottom(true);
            pinWhileMediaLoads();
                state.isHistoryLoaded = true;

                // Process any messages received while loading history
                for (const queued of state.messageQueue) {
                    handleSignalRMessage(queued);
                }
                state.messageQueue = [];

                // If they had already read my last message before I opened
                // this chat, their avatar belongs under it straight away.
                const mine = ownBubbles();
                const lastMine = mine[mine.length - 1];
                if (lastMine && lastMine.dataset.status === "seen") {
                    placeReaderAvatar(state.selectedUser, state.selectedUserNickname, state.selectedUserAvatar,
                                      lastMine.parentElement);
                }

                markConversationRead();
            })
            .catch(error => {
                console.error('Error loading private chat messages:', error);
            })
            .finally(hideChatLoading);
    }


    // Open a group chat and load group messages
    export async function openGroupChat(groupId, groupName, avatarUrl = null) {
        closeSidebar();
        state.selectedGroupId = groupId;
        state.selectedUser = null;
        unreadGroups.delete(groupId);   // reading it is the point of opening it
        const groupDot = document.getElementById(`unread-group-${groupId}`);
        if (groupDot) groupDot.classList.remove("active");
        state.groupIAmAdmin = false;          // until the group's info says otherwise
        clearPendingAttachment();
        clearReply();
        closeMessagePopup();
        state.latestGroupMessageId = 0;
        resetBlockState();

        // Update the chat header with the group avatar and name
        const chatWithEl = document.getElementById("chatWith");
        chatWithEl.innerHTML = "";

        const avatarImg = document.createElement("img");
        // A group picture is a database row now, and a row can go missing
        // (an old on-disk URL, a failed request). Fall back once - clearing
        // the handler first, or a missing default would loop.
        avatarImg.src = avatarUrl && avatarUrl.trim() !== "" ? avatarUrl : "/images/default-group.png";
        avatarImg.onerror = () => { avatarImg.onerror = null; avatarImg.src = "/images/default-group.png"; };
        avatarImg.alt = groupName;
        avatarImg.className = "chat-avatar-small";

        const nameSpan = document.createElement("span");
        nameSpan.textContent = groupName;

        chatWithEl.appendChild(avatarImg);
        chatWithEl.appendChild(nameSpan);

        // Store the current group ID in a hidden input as a backup
        const currentGroupInput = document.getElementById("currentGroupId");
        if (currentGroupInput) currentGroupInput.value = groupId;

        updateDeleteButtonVisibility();
        chatBox.innerHTML = "";
        resetDayTracking();
        resetHistoryCursor();
        clearTyping();
        showChatLoading();

        // Step 1: Load group info to know who the creator is
        try {
            const infoRes = await fetch(`/api/group/info?groupId=${encodeURIComponent(groupId)}`);
            if (infoRes.ok) {
                const info = await infoRes.json();
                state.groupCreatorUserName = (info.creatorUserName || info.CreatorUserName || "").toLowerCase();
                state.groupIAmAdmin = !!(info.amAdmin ?? info.AmAdmin);
                console.log("groupCreatorUserName:", state.groupCreatorUserName, "admin:", state.groupIAmAdmin);
            } else {
                console.warn("Failed to fetch group info:", infoRes.status);
                state.groupCreatorUserName = null;
                state.groupIAmAdmin = false;
            }
        } catch (err) {
            console.error("Error loading group info:", err);
            state.groupCreatorUserName = null;
            state.groupIAmAdmin = false;
        }

        // the menu is drawn before this point, so redraw it now that the
        // answer to "can I add people here" is known
        updateDeleteButtonVisibility();

        // Step 2: Load group messages and render them
        try {
            const res = await fetch(`/api/group/messages?groupId=${encodeURIComponent(groupId)}`);
            if (!res.ok) throw new Error('Failed to fetch group messages');
            const page = await res.json();
            const messages = pageMessages(page);
            rememberCursor(page);
            renderGroupPage(messages, chatBox, paneDays);

            // Scroll to bottom after loading messages
            scrollChatToBottom(true);
            pinWhileMediaLoads();

            await loadGroupReads(groupId);
            markConversationRead();
        } catch (error) {
            console.error('Error loading group messages:', error);
        } finally {
            hideChatLoading();
        }
    }

    // Load recent chats (both private and group chats)
    export async function loadChatPartners() {
        try {
            // Fetch chat partners (users and groups) from the server
            const res = await fetch('/Chat/GetChatPartners');
            if (!res.ok) throw new Error("Failed to load chat partners");
            const partners = await res.json();

            console.log('Chat partners:', partners);

            // Get containers for private and group chat lists
            const privateList = document.getElementById('privateChats');
            const groupList = document.getElementById('groupChats');
            privateList.innerHTML = "";
            groupList.innerHTML = "";

            // Build private chat list
            (partners.privateChats || []).forEach(user => {
                rememberPresence(user.email, user.online, user.lastSeen);

                const li = document.createElement('li');
                li.className = "list-group-item list-group-item-action d-flex align-items-center gap-2";
                li.style.cursor = "pointer";
                li.setAttribute("data-email", user.email);

                // Open private chat when user is clicked
                li.onclick = () => {
                    openPrivateChat(user.email, user.nickname, user.avatarUrl);

                    // Clear unread state for this user
                    const emailKey = (user.email || "").toLowerCase();
                    unreadPrivate.delete(emailKey);
                    const dot = li.querySelector(".unread-dot");
                    if (dot) dot.classList.remove("active");
                };

                // User avatar
                const avatar = document.createElement('img');
                avatar.src = user.avatarUrl || "/images/default-avatar.png";
                avatar.alt = "avatar";
                avatar.className = "chat-avatar";

                // Display nickname or email
                const span = document.createElement('span');
                span.textContent = user.nickname || user.email;

                // Create unread indicator (dot)
                const emailKey = (user.email || "").trim().toLowerCase();
                const dot = document.createElement('span');
                dot.className = "unread-dot";
                dot.id = `unread-private-${emailKey}`;
                console.log("Dot created:", dot.id);

                // The server counts what I have never read, which is what
                // puts the dot back after a reload. Only ever ADDED from the
                // server: a chat cleared locally a moment ago may not have
                // reached the database yet, and the dot should not flicker
                // back on because of the round trip.
                if (user.unread > 0) unreadPrivate.add(emailKey);

                // ...except for the conversation that is open on screen. The
                // server counts a message as unread until MarkPrivateRead has
                // been round-tripped, so without this the chat you are
                // literally reading lights its own dot for a moment - and
                // nothing cleared it again until you clicked the chat.
                if (state.selectedUser && state.selectedUser.toLowerCase() === emailKey) {
                    unreadPrivate.delete(emailKey);
                }

                if (unreadPrivate.has(emailKey)) {
                    dot.classList.add("active");
                }

                // Build the list item
                li.appendChild(withPresenceDot(avatar, user.email));
                li.appendChild(span);
                li.appendChild(dot);
                privateList.appendChild(li);
            });

            // Build group chat list
            (partners.groups || []).forEach(group => {
                const li = document.createElement('li');
                li.className = "list-group-item list-group-item-action d-flex align-items-center gap-2";
                li.style.cursor = "pointer";
                li.setAttribute("data-group-id", group.groupId || group.id);

                // Open group chat when group is clicked
                li.onclick = () => {
                    openGroupChat(group.groupId || group.id, group.name, group.imageUrl);

                    // Clear unread dot for this group
                    unreadGroups.delete(group.groupId || group.id);
                    const dot = li.querySelector(".unread-dot");
                    if (dot) dot.classList.remove("active");
                };

                // Group avatar
                const avatar = document.createElement('img');
                avatar.src = group.imageUrl && group.imageUrl.trim() !== ""
                    ? group.imageUrl
                    : "/images/default-group.png";
                avatar.onerror = () => { avatar.onerror = null; avatar.src = "/images/default-group.png"; };
                avatar.alt = "avatar";
                avatar.className = "chat-image";

                // Group name
                const span = document.createElement('span');
                span.textContent = group.name;

                // Create unread indicator (dot)
                const groupKey = group.groupId || group.id;
                const dot = document.createElement('span');
                dot.className = "unread-dot";
                dot.id = `unread-group-${groupKey}`;

                if (group.unread > 0) unreadGroups.add(groupKey);
                if (state.selectedGroupId === groupKey) unreadGroups.delete(groupKey);
                if (unreadGroups.has(groupKey)) dot.classList.add("active");

                // Build the list item
                li.appendChild(avatar);
                li.appendChild(span);
                li.appendChild(dot);

                // Appended, not prepended: the server already sorts these
                // newest-first, and inserting each one at the top turned that
                // order upside down.
                groupList.appendChild(li);
            });

        } catch (error) {
            console.error("Error loading chat partners:", error);
        }
    }

    // Hide group creation modal and reset its fields
    export function hideModal() {
        document.getElementById("createGroupModal").style.display = "none";
        clearGroupModal();
    }

    // Hide add members modal
    export function hideAddMembersModal() {
        document.getElementById("addMembersModal").style.display = "none";
    }

    // Reset group creation form fields
    export function clearGroupModal() {
        document.getElementById("groupNameInput").value = "";
        document.getElementById("groupUserSearch").value = "";
        document.getElementById("groupUserSuggestions").innerHTML = "";
        selectedUsers.clear();
        updateSelectedUsersUI();
    }

    // Show group creation modal when button is clicked
    document.getElementById("createGroupBtn").onclick = () => {
        document.getElementById("createGroupModal").style.display = "block";
    };

    // Group membership is friends-only, so both pickers search this list.
    export async function searchFriends(term) {
        try {
            const res = await fetch("/api/friends/list");
            if (!res.ok) return [];
            const friends = await res.json();
            const q = term.toLowerCase();
            return friends.filter(f =>
                (f.nickname || "").toLowerCase().includes(q) ||
                (f.email || "").toLowerCase().includes(q));
        } catch (err) {
            console.error("Friend search failed:", err);
            return [];
        }
    }

    // Track selected users for new group
    export const selectedUsers = new Set();

    // DOM references
    export const groupUserSearch = document.getElementById("groupUserSearch");
    export const groupUserSuggestions = document.getElementById("groupUserSuggestions");
    export const selectedUsersDiv = document.getElementById("selectedUsers");

    // Handle user search while typing
    groupUserSearch.addEventListener("input", async () => {
        const term = groupUserSearch.value.trim();

        // Require at least 2 characters
        if (term.length < 2) {
            groupUserSuggestions.innerHTML = "";
            return;
        }

        // Fetch matching users
        // Groups may only contain friends, so pick from the friends list
        // rather than every user on the server.
        const users = await searchFriends(term);

        // Exclude already selected users
        const filtered = users.filter(u => !selectedUsers.has(u.email));

        // Build suggestion list
        groupUserSuggestions.innerHTML = "";
        filtered.forEach(user => {
            const li = document.createElement("li");
            li.className = "list-group-item list-group-item-action";
            li.textContent = user.nickname || user.email;
            li.style.cursor = "pointer";

            // Add user to selected list on click
            li.onclick = () => {
                selectedUsers.add(user.email);
                updateSelectedUsersUI();
                groupUserSearch.value = "";
                groupUserSuggestions.innerHTML = "";
            };

            groupUserSuggestions.appendChild(li);
        });
    });

    // Update UI with selected users
    export function updateSelectedUsersUI() {
        selectedUsersDiv.innerHTML = "";

        selectedUsers.forEach(email => {
            const span = document.createElement("span");
            span.className = "create-selected-user";
            span.textContent = email;

            // Add remove button for each user
            const closeBtn = document.createElement("button");
            closeBtn.className = "btn-close btn-close-white btn-sm ms-1";
            closeBtn.onclick = () => {
                selectedUsers.delete(email);
                updateSelectedUsersUI();
            };

            span.appendChild(closeBtn);
            selectedUsersDiv.appendChild(span);
        });
    }

    // Confirm group creation and send data to server
    document.getElementById("confirmCreateGroup").addEventListener("click", async function () {
        const groupName = document.getElementById("groupNameInput").value.trim();
        const imageInput = document.getElementById("groupImageUpload");
        const imageFile = imageInput.files[0];
        const usernames = Array.from(selectedUsers);

        if (!groupName) {
            alert("Group name is required");
            return;
        }

        // Build request payload
        const formData = new FormData();
        formData.append("Name", groupName);
        usernames.forEach(u => formData.append("Usernames", u));
        if (imageFile) {
            formData.append("Image", imageFile);
        }

        try {
            // Send request to backend
            const response = await fetch("/api/group/create", {
                method: "POST",
                body: formData
            });

            if (response.ok) {
                console.log("Group created");
                hideModal();
            } else {
                const errorText = await response.text();
                alert("Error: " + errorText);
            }
        } catch (err) {
            console.error("Group creation failed:", err);
            alert("Something went wrong");
        }
    });

    export const selectedMembersToAdd = new Set();

    export const memberUserSearch = document.getElementById("memberUserSearch");
    export const memberUserSuggestions = document.getElementById("memberUserSuggestions");
    export const memberSelectedUsersDiv = document.getElementById("memberSelectedUsers");

