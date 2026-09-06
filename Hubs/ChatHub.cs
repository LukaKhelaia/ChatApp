using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using System.Collections.Concurrent;
using ChatApp.Models;
using ChatApp.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using ChatApp.Controllers;
using ChatApp.Services;

namespace ChatApp.Hubs
{
    [Authorize]
    public class ChatHub : Hub
    {
        // Maximum accepted message length. Without a cap a client can post an
        // unbounded string straight into the database.
        private const int MaxMessageLength = 2000;

        // Two messages a second sustained, ten in hand. Nobody types faster
        // than that for long, and a burst of ten covers the normal case of
        // firing off a few short lines in a row. Typing pings get their own,
        // smaller allowance so a held-down key cannot spend the message budget.
        private const double SendPerSecond = 2;
        private const double SendBurst = 10;
        private const double TypingPerSecond = 1;
        private const double TypingBurst = 4;

        // connection ids per user - a user may have several tabs open.
        private static readonly ConcurrentDictionary<string, HashSet<string>> _userConnections = new();

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<ChatHub> _logger;

        public ChatHub(ApplicationDbContext context, UserManager<ApplicationUser> userManager, ILogger<ChatHub> logger)
        {
            _context = context;
            _userManager = userManager;
            _logger = logger;
        }

        // The SignalR user id (see NameUserIdProvider) is always the lower-cased
        // user name, so use the same normalisation everywhere.
        private string? CurrentUser => Context.User?.Identity?.Name?.Trim().ToLowerInvariant();

        private static string GroupName(int groupId) => $"group-{groupId}";

        // When a user connects, register them and join their SignalR groups
        public override async Task OnConnectedAsync()
        {
            var username = CurrentUser;

            if (!string.IsNullOrEmpty(username))
            {
                // A second tab is not a second arrival. Only the transition
                // from no connections to one is "came online", and only the
                // transition back is "went offline" - otherwise closing one of
                // three tabs would tell everyone you had left.
                var firstConnection = false;

                _userConnections.AddOrUpdate(
                    username,
                    _ => { firstConnection = true; return new HashSet<string> { Context.ConnectionId }; },
                    (_, set) => { lock (set) { set.Add(Context.ConnectionId); } return set; });

                var groupIds = await _context.UserGroups
                    .Where(ug => ug.UserName.ToLower() == username)
                    .Select(ug => ug.GroupId)
                    .ToListAsync();

                foreach (var groupId in groupIds)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(groupId));
                }

                if (firstConnection)
                {
                    // LastSeen is cleared while they are here, so a stale value
                    // can never be shown next to a green dot.
                    var me = await _userManager.FindByNameAsync(username);
                    if (me != null && me.LastSeen != null)
                    {
                        me.LastSeen = null;
                        await _userManager.UpdateAsync(me);
                    }

                    await AnnouncePresenceAsync(username, true, null);
                }

                _logger.LogDebug("User {User} connected and joined {Count} group(s).", username, groupIds.Count);
            }

            await base.OnConnectedAsync();
        }

        // When a user disconnects, drop that connection id
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var username = CurrentUser;
            var wasLastConnection = false;

            if (!string.IsNullOrEmpty(username) && _userConnections.TryGetValue(username, out var set))
            {
                lock (set)
                {
                    set.Remove(Context.ConnectionId);
                    if (set.Count == 0)
                    {
                        _userConnections.TryRemove(username, out _);
                        wasLastConnection = true;
                    }
                }
            }

            if (wasLastConnection && !string.IsNullOrEmpty(username))
            {
                var seenAt = DateTime.UtcNow;

                var me = await _userManager.FindByNameAsync(username);
                if (me != null)
                {
                    me.LastSeen = seenAt;
                    await _userManager.UpdateAsync(me);
                }

                await AnnouncePresenceAsync(username, false, seenAt);
            }

            await base.OnDisconnectedAsync(exception);
        }

        /// <summary>
        /// Tells this person's friends that they came or went.
        ///
        /// Friends only, deliberately. Presence is the most quietly revealing
        /// thing a chat app knows - when you sleep, when you are at work - and
        /// broadcasting it to everyone who has ever searched for you would be
        /// handing that out for nothing. The friend rule already gates
        /// messaging, so it gates this too.
        /// </summary>
        private async Task AnnouncePresenceAsync(string username, bool online, DateTime? lastSeen)
        {
            var friends = await _context.Friendships
                .Where(f => f.Status == FriendshipStatus.Accepted &&
                            (f.RequesterUserName == username || f.AddresseeUserName == username))
                .Select(f => f.RequesterUserName == username ? f.AddresseeUserName : f.RequesterUserName)
                .ToListAsync();

            if (friends.Count == 0) return;

            await Clients.Users(friends).SendAsync("PresenceChanged", username, online, lastSeen);
        }

        /// <summary>
        /// Whether the hub is currently holding a connection for this account.
        /// Static because presence lives in the connection map, not the
        /// database, and the controllers need to read it when they build a
        /// sidebar for someone who has just loaded the page.
        /// </summary>
        public static bool IsOnline(string? username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            return _userConnections.ContainsKey(username.Trim().ToLowerInvariant());
        }

        /// <summary>
        /// Resolves an attachment the caller is trying to send. Returns null if
        /// the id is unknown, belongs to someone else, or has already been sent
        /// on another message - otherwise anyone could attach a stranger's
        /// upload to their own message and then read it through the download
        /// endpoint's "it's on a message you can see" rule.
        /// </summary>
        private async Task<Attachment?> ResolveAttachmentAsync(int? attachmentId, string sender)
        {
            if (attachmentId is not int id) return null;

            var attachment = await _context.Attachments.FirstOrDefaultAsync(a => a.Id == id);
            if (attachment == null) return null;

            if (!string.Equals(attachment.UploadedBy, sender, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("User {User} tried to send attachment {Id} uploaded by {Owner}",
                    sender, id, attachment.UploadedBy);
                return null;
            }

            var alreadyUsed = await _context.Messages.AnyAsync(m => m.AttachmentId == id)
                           || await _context.GroupMessages.AnyAsync(m => m.AttachmentId == id);
            if (alreadyUsed)
            {
                _logger.LogWarning("Attachment {Id} has already been sent", id);
                return null;
            }

            return attachment;
        }

        // The shape the client renders attachments from. Defined once so the
        // private and group payloads can't drift apart.
        private static object? AttachmentPayload(Attachment? a) => a == null ? null : new
        {
            id = a.Id,
            fileName = a.FileName,
            contentType = a.ContentType,
            size = a.Size,
            isImage = AttachmentsController.IsImage(a.ContentType)
        };

        // Send a private message between two users
        public async Task SendPrivateMessage(string toUser, string message, int? attachmentId = null, int? replyToMessageId = null)
        {
            var fromUser = CurrentUser;

            if (string.IsNullOrEmpty(fromUser) || string.IsNullOrWhiteSpace(toUser))
                return;

            // Checked before anything is read or written. A flood should cost
            // the server one dictionary lookup, not a database round trip.
            if (!RateLimiter.Allow("send:" + fromUser, SendPerSecond, SendBurst))
            {
                await Clients.Caller.SendAsync("MessageRejected", "You're sending messages too quickly.");
                return;
            }

            message = (message ?? string.Empty).Trim();
            if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];

            var attachment = await ResolveAttachmentAsync(attachmentId, fromUser);

            // A message has to carry something: text, an attachment, or both.
            if (message.Length == 0 && attachment == null) return;

            toUser = toUser.Trim().ToLowerInvariant();

            // Only allow sending to a real account...
            var recipient = await _context.Users
                .FirstOrDefaultAsync(u => u.UserName != null && u.UserName.ToLower() == toUser);
            if (recipient == null) return;

            // ...and only to an accepted friend. The UI hides non-friends, but
            // the hub is reachable directly, so the rule is enforced here too.
            if (!await FriendsController.AreFriendsAsync(_context, fromUser, toUser))
            {
                _logger.LogWarning("Blocked message from {From} to non-friend {To}", fromUser, toUser);
                await Clients.Caller.SendAsync("MessageRejected", "You can only message friends.");
                return;
            }

            // A block in EITHER direction stops the message. Checked here and
            // not just in the UI, for the same reason as the friend rule.
            if (await BlocksController.IsBlockedEitherWayAsync(_context, fromUser, toUser))
            {
                _logger.LogWarning("Blocked message between {From} and {To}", fromUser, toUser);
                await Clients.Caller.SendAsync("MessageRejected", "You can't message this user.");
                return;
            }

            // "Delivered" means it reached their browser. If they have a live
            // connection right now, this SendAsync is that moment, so stamp it
            // here; otherwise MessagesController stamps it when they next load
            // the conversation.
            var recipientOnline = _userConnections.ContainsKey(toUser);

            // A reply may only quote a message from this same conversation.
            // MessageExtras returns null for anything else, which quietly turns
            // a forged id into an ordinary message rather than a way to read a
            // stranger's text.
            var replyTo = await MessageExtras.PrivateReplyAsync(_context, replyToMessageId, fromUser, toUser);

            var chatMessage = new Message
            {
                User = fromUser,
                Receiver = toUser,
                Text = message,
                AttachmentId = attachment?.Id,
                ReplyToMessageId = replyTo == null ? null : replyToMessageId,
                SentAt = DateTime.UtcNow,
                DeliveredAt = recipientOnline ? DateTime.UtcNow : null
            };

            _context.Messages.Add(chatMessage);
            await _context.SaveChangesAsync();

            var senderUser = await _userManager.FindByNameAsync(fromUser);
            var avatarUrl = string.IsNullOrWhiteSpace(senderUser?.AvatarUrl)
                ? "/images/default-avatar.png"
                : senderUser!.AvatarUrl!;

            var payload = AttachmentPayload(attachment);

            // Both sides. Only the recipient used to be told, so the first
            // message to somebody new never appeared in the SENDER's chat list
            // - they had to wait for a reply before the conversation showed up
            // at all.
            await Clients.User(toUser).SendAsync("ChatListUpdated");
            await Clients.User(fromUser).SendAsync("ChatListUpdated");
            await Clients.User(toUser).SendAsync(
                "ReceivePrivateMessage",
                fromUser,
                message,
                chatMessage.SentAt,
                avatarUrl,
                payload,
                chatMessage.Id,
                chatMessage.DeliveredAt,
                replyTo,
                toUser          // which conversation this belongs to, for the sender's copy
            );

            // Echo to every tab the sender has open, not just the calling one.
            await Clients.User(fromUser).SendAsync(
                "ReceivePrivateMessage",
                fromUser,
                message,
                chatMessage.SentAt,
                avatarUrl,
                payload,
                chatMessage.Id,
                chatMessage.DeliveredAt,
                replyTo,
                toUser          // which conversation this belongs to, for the sender's copy
            );
        }

        // Send a message to a group
        public async Task SendGroupMessage(int groupId, string message, int? attachmentId = null, int? replyToMessageId = null)
        {
            var senderUserName = CurrentUser;
            if (string.IsNullOrEmpty(senderUserName)) return;

            // Same allowance as a private message, and the same bucket - the
            // limit is on the person, not on the conversation, or you could
            // flood a group by rotating through your others.
            if (!RateLimiter.Allow("send:" + senderUserName, SendPerSecond, SendBurst))
            {
                await Clients.Caller.SendAsync("MessageRejected", "You're sending messages too quickly.");
                return;
            }

            message = (message ?? string.Empty).Trim();
            if (message.Length > MaxMessageLength) message = message[..MaxMessageLength];

            var attachment = await ResolveAttachmentAsync(attachmentId, senderUserName);
            if (message.Length == 0 && attachment == null) return;

            // Look the sender up by user name (not email - a user can change
            // their email address while the user name stays the key we use).
            var sender = await _context.Users
                .FirstOrDefaultAsync(u => u.UserName != null && u.UserName.ToLower() == senderUserName);
            if (sender == null) return;

            // Only members may post to a group.
            var isMember = await _context.UserGroups
                .AnyAsync(ug => ug.GroupId == groupId && ug.UserName.ToLower() == senderUserName);
            if (!isMember) return;

            var replyTo = await MessageExtras.GroupReplyAsync(_context, replyToMessageId, groupId);

            var groupMessage = new GroupMessage
            {
                GroupId = groupId,
                Sender = senderUserName,
                Text = message,
                AttachmentId = attachment?.Id,
                ReplyToMessageId = replyTo == null ? null : replyToMessageId,
                SentAt = DateTime.UtcNow
            };

            _context.GroupMessages.Add(groupMessage);

            // A new message un-hides the group for anyone who had deleted it.
            var deletions = await _context.GroupChatDeletions
                .Where(d => d.GroupId == groupId && d.IsHidden)
                .ToListAsync();

            foreach (var deletion in deletions)
            {
                deletion.IsHidden = false;
            }

            await _context.SaveChangesAsync();

            var affectedUsers = deletions
                .Select(d => d.UserName.ToLowerInvariant())
                .Distinct()
                .ToList();

            if (affectedUsers.Count > 0)
            {
                await Clients.Users(affectedUsers).SendAsync("ChatListUpdated");
            }

            var avatarUrl = string.IsNullOrWhiteSpace(sender.AvatarUrl)
                ? "/images/default-avatar.png"
                : sender.AvatarUrl;

            await Clients.Group(GroupName(groupId))
                .SendAsync("ReceiveGroupMessage", groupId, senderUserName, sender.Nickname, message,
                           groupMessage.SentAt, avatarUrl, AttachmentPayload(attachment), groupMessage.Id,
                           replyTo);
        }

        /// <summary>
        /// The caller has the private conversation with <paramref name="withUser"/>
        /// open and focused, so everything they have received from that user is
        /// now read. The sender's tabs are told once - not once per message.
        /// </summary>
        // ------------------------------------------------------------------
        //  Typing
        // ------------------------------------------------------------------
        //  Nothing here is persisted and nothing is acknowledged. A typing
        //  signal that goes missing is a line that briefly does not appear,
        //  which is the right way for this to fail - so it is a bare relay
        //  with no table behind it.
        //
        //  There is deliberately no "stopped typing" call either. The client
        //  pings every couple of seconds while a key is being pressed and the
        //  receiver forgets after a few, so a closed laptop, a dropped
        //  connection and a finished sentence all end the same way: the pings
        //  stop and the line times out. A stop message would have to arrive to
        //  work, and the one case you most need it - the tab disappearing - is
        //  exactly when it cannot be sent.
        //
        //  The friend and block rules are the same as for a real message.
        //  Telling somebody who has blocked you that you are typing to them is
        //  still contact, and the hub is reachable without going through the UI.

        public async Task TypingPrivate(string toUser)
        {
            var fromUser = CurrentUser;
            if (string.IsNullOrEmpty(fromUser) || string.IsNullOrWhiteSpace(toUser)) return;

            toUser = toUser.Trim().ToLowerInvariant();
            if (toUser == fromUser) return;

            // Dropped silently. A typing ping is already best-effort, so a
            // rejection has nothing useful to say and the client is not
            // listening for one.
            if (!RateLimiter.Allow("typing:" + fromUser, TypingPerSecond, TypingBurst)) return;

            if (!await FriendsController.AreFriendsAsync(_context, fromUser, toUser)) return;
            if (await BlocksController.IsBlockedEitherWayAsync(_context, fromUser, toUser)) return;

            // The receiver already knows this person's name - it is the chat
            // they have open - so nothing but the sender needs to travel.
            // Typed nulls: the client's handler takes (fromUser, groupId,
            // nickname) whichever kind of chat it is, so the shape stays the
            // same and the group id being absent is what says "private".
            await Clients.User(toUser).SendAsync("UserTyping", fromUser, (int?)null, (string?)null);
        }

        public async Task TypingGroup(int groupId)
        {
            var fromUser = CurrentUser;
            if (string.IsNullOrEmpty(fromUser)) return;

            if (!RateLimiter.Allow("typing:" + fromUser, TypingPerSecond, TypingBurst)) return;

            var isMember = await _context.UserGroups
                .AnyAsync(ug => ug.GroupId == groupId && ug.UserName.ToLower() == fromUser);
            if (!isMember) return;

            // A group has many people in it, so this one does carry a name -
            // read from the account rather than taken from the client, which
            // would let anyone type under somebody else's name.
            var me = await _userManager.FindByNameAsync(fromUser);
            var nickname = string.IsNullOrWhiteSpace(me?.Nickname) ? fromUser : me!.Nickname!;

            await Clients.OthersInGroup(GroupName(groupId))
                .SendAsync("UserTyping", fromUser, groupId, nickname);
        }

        public async Task MarkPrivateRead(string withUser)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me) || string.IsNullOrWhiteSpace(withUser)) return;

            withUser = withUser.Trim().ToLowerInvariant();
            var now = DateTime.UtcNow;

            var unread = await _context.Messages
                .Where(m => m.Receiver.ToLower() == me
                         && m.User.ToLower() == withUser
                         && m.ReadAt == null)
                .ToListAsync();

            if (unread.Count == 0) return;

            foreach (var m in unread)
            {
                m.DeliveredAt ??= now;      // you cannot read what was never delivered
                m.ReadAt = now;
            }

            await _context.SaveChangesAsync();

            // Tell the other side. One event covers the whole conversation
            // because reading marks every outstanding message at once.
            await Clients.User(withUser).SendAsync("PrivateMessagesRead", me, now);
        }

        /// <summary>
        /// Move this member's read pointer in a group. Upserted, and only ever
        /// moved forward - an older tab catching up must not drag it back.
        /// </summary>
        public async Task MarkGroupRead(int groupId, int lastMessageId)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me) || lastMessageId <= 0) return;

            var isMember = await _context.UserGroups
                .AnyAsync(ug => ug.GroupId == groupId && ug.UserName.ToLower() == me);
            if (!isMember) return;

            var row = await _context.GroupReads
                .FirstOrDefaultAsync(r => r.GroupId == groupId && r.UserName == me);

            if (row == null)
            {
                row = new GroupRead
                {
                    GroupId = groupId,
                    UserName = me,
                    LastReadMessageId = lastMessageId,
                    UpdatedAt = DateTime.UtcNow
                };
                _context.GroupReads.Add(row);
            }
            else if (lastMessageId > row.LastReadMessageId)
            {
                row.LastReadMessageId = lastMessageId;
                row.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                return;                     // nothing moved, nothing to announce
            }

            await _context.SaveChangesAsync();

            var reader = await _userManager.FindByNameAsync(me);
            var avatarUrl = string.IsNullOrWhiteSpace(reader?.AvatarUrl)
                ? "/images/default-avatar.png"
                : reader!.AvatarUrl!;

            await Clients.Group(GroupName(groupId))
                .SendAsync("GroupMessagesRead", groupId, me, lastMessageId,
                           reader?.Nickname ?? me, avatarUrl);
        }

        // Add the current user to a group's SignalR group after it's created
        public async Task AddToGroupAfterCreation(int groupId)
        {
            var username = CurrentUser;
            if (string.IsNullOrEmpty(username)) return;

            var isInGroup = await _context.UserGroups
                .AnyAsync(ug => ug.UserName.ToLower() == username && ug.GroupId == groupId);

            if (!isInGroup)
            {
                _logger.LogDebug("User {User} is not a member of group {GroupId}.", username, groupId);
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(groupId));
        }

        // Remove the current user from a group's SignalR group.
        // NOTE: the SignalR group name is "group-{id}", not the bare id - the
        // previous version passed the raw id and never actually removed anyone.
        public async Task LeaveGroup(int groupId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(groupId));
        }
    }
}
