// ---------------------------------------------------------------------------
//  ChatApp - chat-groups.js
//
//  Group management: members, admins, adding and kicking.
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
import { chatBox, connection, currentUser, username } from './chat-core.js';
import { loadFriends, postFriend } from './chat-friends.js';
import { resetDayTracking } from './chat-days.js';
import { hideAddMembersModal, hideModal, loadChatPartners, memberSelectedUsersDiv, memberUserSearch, memberUserSuggestions, openGroupChat, searchFriends, selectedMembersToAdd } from './chat-conversations.js';
import { updateComposerState, updateDeleteButtonVisibility } from './chat-ui.js';

    //  Handle typing in search input to suggest users
    memberUserSearch.addEventListener("input", async () => {
        const term = memberUserSearch.value.trim();
        if (term.length < 2) {
            memberUserSuggestions.innerHTML = "";
            return;
        }

        const users = await searchFriends(term);

        const filtered = users.filter(u => !selectedMembersToAdd.has(u.email));

        memberUserSuggestions.innerHTML = "";
        filtered.forEach(user => {
            const li = document.createElement("li");
            li.className = "list-group-item list-group-item-action";
            li.textContent = user.nickname || user.email;
            li.style.cursor = "pointer";
            li.onclick = () => {
                selectedMembersToAdd.add(user.email);
                updateSelectedMembersUI();
                memberUserSearch.value = "";
                memberUserSuggestions.innerHTML = "";
            };
            memberUserSuggestions.appendChild(li);
        });
    });

    //  Update UI with selected members
    export function updateSelectedMembersUI() {
        memberSelectedUsersDiv.innerHTML = "";
        selectedMembersToAdd.forEach(email => {
            const span = document.createElement("span");
            span.className = "create-selected-user";
            span.textContent = email;
            const closeBtn = document.createElement("button");
            closeBtn.className = "btn-close btn-close-white btn-sm ms-1";
            closeBtn.onclick = () => {
                selectedMembersToAdd.delete(email);
                updateSelectedMembersUI();
            };
            span.appendChild(closeBtn);
            memberSelectedUsersDiv.appendChild(span);
        });
    }

    //  Load and render all group members in modal
    export async function loadMembersAndRender(groupId) {
        if (!groupId) return;

        if (!state.groupCreatorUserName) {
            try {
                const infoRes = await fetch(`/api/group/info?groupId=${encodeURIComponent(groupId)}`);
                if (infoRes.ok) {
                    const info = await infoRes.json();
                    state.groupCreatorUserName = (info.creatorUserName || info.CreatorUserName || "").toLowerCase();
                }
            } catch (e) {
                console.warn("Couldn't fetch group info for members:", e);
            }
        }

        try {
            const membersRes = await fetch(`/api/group/group-members?groupId=${encodeURIComponent(groupId)}`);
            if (!membersRes.ok) throw new Error(`Failed to load members (HTTP ${membersRes.status})`);
            const members = await membersRes.json();

            const container = document.getElementById("membersModalBody");
            container.innerHTML = "";

            members.forEach(m => {
                const memberUserName = (m.userName || "").toLowerCase();
                const displayName = m.nickname || m.email || m.userName || "Unknown";

                const row = document.createElement("div");
                row.className = "member d-flex align-items-center gap-2 mb-2";

                const img = document.createElement("img");
                img.src = m.avatarUrl || "/images/default-avatar.png";
                img.alt = displayName;
                img.className = "member-avatar";
                img.style.width = "36px";
                img.style.height = "36px";
                img.style.borderRadius = "50%";

                const nameSpan = document.createElement("span");
                nameSpan.className = "member-name";
                nameSpan.textContent = displayName;

                row.appendChild(img);
                row.appendChild(nameSpan);

                // Who is who. The creator's authority is the group record, not
                // a flag, so it can never be taken away by another admin.
                const targetIsCreator = !!m.isCreator;
                const targetIsAdmin = !!m.isAdmin;
                const iAmCreator = (currentUser === (state.groupCreatorUserName || "").toLowerCase());
                const isSelf = (memberUserName === currentUser);

                if (targetIsCreator || targetIsAdmin) {
                    const tag = document.createElement("span");
                    tag.className = "member-role" + (targetIsCreator ? " owner" : "");
                    tag.textContent = targetIsCreator ? "Owner" : "Admin";
                    row.appendChild(tag);
                }

                const actions = document.createElement("div");
                actions.className = "member-actions";

                // Only the creator hands out admin, and never to themselves.
                if (iAmCreator && !isSelf && !targetIsCreator) {
                    const roleBtn = document.createElement("button");
                    roleBtn.className = "member-btn";
                    roleBtn.textContent = targetIsAdmin ? "Remove admin" : "Make admin";
                    roleBtn.addEventListener("click", async () => {
                        roleBtn.disabled = true;
                        try {
                            const res = await fetch("/api/group/set-admin", {
                                method: "POST",
                                headers: { "Content-Type": "application/json" },
                                body: JSON.stringify({
                                    groupId: Number(groupId),
                                    userName: m.userName,
                                    isAdmin: !targetIsAdmin
                                })
                            });
                            if (!res.ok) {
                                alert("Could not change admin: " + (await res.text() || res.status));
                                roleBtn.disabled = false;
                                return;
                            }
                            await loadMembersAndRender(groupId);
                        } catch (error) {
                            console.error("set-admin failed:", error);
                            roleBtn.disabled = false;
                        }
                    });
                    actions.appendChild(roleBtn);
                }

                // An admin can remove ordinary members; admins are equals, so
                // only the creator can remove one of them. Nobody removes the
                // creator. The server enforces all three - this only decides
                // whether the button is worth showing.
                const mayKick = !isSelf && !targetIsCreator &&
                    (iAmCreator || (state.groupIAmAdmin && !targetIsAdmin));

                if (mayKick) {
                    const kickBtn = document.createElement("button");
                    kickBtn.className = "member-btn danger";
                    kickBtn.textContent = "Kick";
                    kickBtn.addEventListener("click", async () => {
                        if (!confirm(`Kick ${displayName} from this group?`)) return;
                        const body = new URLSearchParams();
                        body.append("groupId", String(groupId));
                        body.append("userNameToKick", m.userName);

                        const res = await fetch("/api/group/kick", {
                            method: "POST",
                            headers: { "Content-Type": "application/x-www-form-urlencoded" },
                            body: body
                        });

                        if (!res.ok) {
                            const txt = await res.text();
                            alert("Kick failed: " + (txt || res.status));
                            return;
                        }
                        await loadMembersAndRender(groupId);
                    });
                    actions.appendChild(kickBtn);
                }

                if (actions.childElementCount) row.appendChild(actions);

                container.appendChild(row);
            });

            document.getElementById("membersModal").style.display = "flex";
        } catch (e) {
            console.error("loadMembersAndRender error:", e);
            alert("Error loading members. Check console.");
        }
    }

    //  View members button
    document.getElementById("viewMembersBtn").addEventListener("click", async function () {
        const gid = (typeof state.selectedGroupId !== "undefined" && state.selectedGroupId)
            ? state.selectedGroupId
            : (document.getElementById("currentGroupId")?.value || null);

        if (!gid) {
            console.error("No group selected to view members.");
            return;
        }

        await loadMembersAndRender(gid);
    });

    //  Kick user helper function
    export async function kickUserFromGroup(groupId, usernameToKick) {
        const body = new URLSearchParams();
        body.append("groupId", String(groupId));
        body.append("userNameToKick", usernameToKick);

        const res = await fetch("/api/group/kick", {
            method: "POST",
            headers: { "Content-Type": "application/x-www-form-urlencoded" },
            body
        });

        if (!res.ok) {
            const txt = await res.text();
            throw new Error(txt || `HTTP ${res.status}`);
        }
        return await res.text();
    }

    //  Confirm adding members to group
    document.getElementById("confirmAddMembers").addEventListener("click", async function () {
        const groupId = document.getElementById("currentGroupId").value;
        const usernames = Array.from(selectedMembersToAdd);

        if (!groupId || usernames.length === 0) {
            alert("Please select users to add.");
            return;
        }

        for (const username of usernames) {
            const formData = new FormData();
            formData.append("GroupId", groupId);
            formData.append("UserName", username);

            const res = await fetch("/api/group/addMember", {
                method: "POST",
                body: formData
            });

            if (!res.ok) {
                const errorText = await res.text();
                alert("Error adding " + username + ": " + errorText);
            }
        }

        hideAddMembersModal();
        selectedMembersToAdd.clear();
        updateSelectedMembersUI();
    });

    //  Modal close buttons
    document.getElementById("closeCreateModal").addEventListener("click", hideModal);
    document.getElementById("closeAddMembersModal").addEventListener("click", hideAddMembersModal);
    document.getElementById("closeMembersModal").addEventListener("click", function () {
        document.getElementById("membersModal").style.display = "none";
    });

    //  Close add-members modal when clicking outside
    window.addEventListener("click", function (event) {
        const modal = document.getElementById("addMembersModal");
        if (event.target === modal) {
            modal.style.display = "none";
        }
    });

    //  Open add-members modal
    export function openAddMembersModal(groupId) {
        document.getElementById("currentGroupId").value = groupId;
        selectedMembersToAdd.clear();
        updateSelectedMembersUI();
        memberUserSearch.value = "";
        memberUserSuggestions.innerHTML = "";
        document.getElementById("addMembersModal").style.display = "block";
    }

    //  Add member button
    document.getElementById("addMemberBtn").addEventListener("click", () => {
        if (state.selectedGroupId) {
            openAddMembersModal(state.selectedGroupId);
        } else {
            alert("No group selected");
        }
    });

    // Leave group button
    document.getElementById("leaveGroupBtn").addEventListener("click", async () => {
        if (!state.selectedGroupId) return;

        const confirmed = confirm("Are you sure you want to leave this group?");
        if (!confirmed) return;

        try {
            const response = await fetch("/api/group/leave", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    email: username,
                    groupId: state.selectedGroupId
                })
            });

            if (response.ok) {
                alert("You have left the group.");

                // Also leave the SignalR group - the REST call only updates the
                // database, so without this the client keeps receiving live
                // messages from the group until the page is refreshed.
                try {
                    await connection.invoke("LeaveGroup", Number(state.selectedGroupId));
                } catch (err) {
                    console.error("Error leaving SignalR group:", err);
                }

                state.selectedGroupId = null;

                chatBox.innerHTML = "";
        resetDayTracking();

                const chatWith = document.getElementById("chatWith");
                if (chatWith) chatWith.textContent = "";

                updateDeleteButtonVisibility();
                loadChatPartners();
            } else {
                const errorText = await response.text();
                alert("Error: " + errorText);
            }
        } catch (err) {
            console.error("Leave group failed:", err);
            alert("Something went wrong");
        }
    });

    //  Unfriend button - only shown in a private chat
    document.getElementById("unfriendBtn").addEventListener("click", async () => {
        if (!state.selectedUser) return;

        const label = document.getElementById("chatWith").textContent.trim() || state.selectedUser;
        if (!confirm(`Unfriend ${label}? You will not be able to message each other.`)) return;

        if (await postFriend("/api/friends/remove", state.selectedUser)) {
            // Keep the conversation open and readable - just lock the composer.
            await loadFriends();
            loadChatPartners();
            updateDeleteButtonVisibility();
            updateComposerState();
        }
    });

    //  Delete chat button (private or group)
    document.getElementById("deleteChatBtn").addEventListener("click", async () => {
        if (state.selectedUser) {
            const confirmed = confirm(`Delete chat with ${state.selectedUser}?`);
            if (confirmed) {
                await fetch("/api/messages/delete", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(state.selectedUser)
                });
                chatBox.innerHTML = "";
        resetDayTracking();
                state.selectedUser = null;
                document.getElementById("chatWith").textContent = "";
                updateDeleteButtonVisibility();

                // Reload chat list so deleted chat disappears
                loadChatPartners();
            }

        } else if (state.selectedGroupId) {
            const groupIdToReload = state.selectedGroupId;
            const groupNameToReload = document.getElementById("chatWith").textContent;
            const confirmed = confirm(`Delete group chat?`);
            if (confirmed) {
                await fetch(`/api/group/messages/delete?groupId=${groupIdToReload}`, { method: "DELETE" });
                chatBox.innerHTML = "";
        resetDayTracking();
                state.selectedGroupId = null; // Clear selected group
                document.getElementById("chatWith").textContent = "";
                updateDeleteButtonVisibility();
                loadChatPartners();
                /* openGroupChat(groupIdToReload, groupNameToReload); */ // Optional: reload chat after deletion
            }
        }
    });

