using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using ChatApp.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Identity;
using ChatApp.Models;

namespace ChatApp.Controllers
{
    [Authorize] // Only authenticated users can access this controller
    public class ChatController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<ChatController> _logger;

        public ChatController(ApplicationDbContext context, UserManager<ApplicationUser> userManager, ILogger<ChatController> logger)
        {
            _context = context;
            _userManager = userManager;
            _logger = logger;
        }

        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        // Returns the logged-in user's nickname and avatar
        [HttpGet]
        public async Task<IActionResult> GetNickname()
        {
            var user = await _userManager.GetUserAsync(User);

            return Json(new
            {
                nickname = user?.Nickname ?? "Anonymous",
                avatarUrl = string.IsNullOrEmpty(user?.AvatarUrl) ? "/images/default-avatar.png" : user.AvatarUrl
            });
        }

        // Returns the user's private chat partners and groups
        [HttpGet]
        public async Task<IActionResult> GetChatPartners()
        {
            var user = CurrentUser;
            if (string.IsNullOrEmpty(user))
                return Unauthorized();

            try
            {
                // Distinct counterparties from private chats that the user
                // hasn't deleted on their side.
                var partnerNames = await _context.Messages
                    .Where(m =>
                        (!m.DeletedBySender && m.User.ToLower() == user) ||
                        (!m.DeletedByReceiver && m.Receiver.ToLower() == user))
                    .Select(m => m.User.ToLower() == user ? m.Receiver.ToLower() : m.User.ToLower())
                    .Distinct()
                    .ToListAsync();

                // How many messages from each of them I have never read. This
                // is what puts the dot back after a reload - the browser's own
                // idea of "unread" dies with the page.
                var privateUnread = (await _context.Messages
                    .Where(m => m.Receiver.ToLower() == user
                             && m.ReadAt == null
                             && !m.DeletedByReceiver)
                    .GroupBy(m => m.User.ToLower())
                    .Select(g => new { sender = g.Key, count = g.Count() })
                    .ToListAsync())
                    .ToDictionary(x => x.sender, x => x.count);

                var partners = await _context.Users
                    .Where(u => u.UserName != null && partnerNames.Contains(u.UserName.ToLower()))
                    .Select(u => new
                    {
                        email = u.UserName ?? string.Empty,
                        nickname = u.Nickname ?? "Anonymous",
                        avatarUrl = string.IsNullOrEmpty(u.AvatarUrl) ? "/images/default-avatar.png" : u.AvatarUrl,
                        u.LastSeen
                    })
                    .ToListAsync();

                // When each conversation last had a message in it, so the list
                // can be ordered the way every chat app orders it: whoever
                // spoke most recently is at the top.
                var lastSpoke = (await _context.Messages
                    .Where(m =>
                        (!m.DeletedBySender && m.User.ToLower() == user) ||
                        (!m.DeletedByReceiver && m.Receiver.ToLower() == user))
                    .Select(m => new
                    {
                        partner = m.User.ToLower() == user ? m.Receiver.ToLower() : m.User.ToLower(),
                        m.SentAt
                    })
                    .GroupBy(x => x.partner)
                    .Select(g => new { partner = g.Key, at = g.Max(x => x.SentAt) })
                    .ToListAsync())
                    .ToDictionary(x => x.partner, x => x.at);

                // Presence comes from the hub's connection map rather than the
                // database, so a page loaded now agrees with the PresenceChanged
                // events that arrive a second later. LastSeen is only ever the
                // fallback for somebody who is not here.
                var privateChats = partners.Select(p => new
                {
                    p.email,
                    p.nickname,
                    p.avatarUrl,
                    online = ChatApp.Hubs.ChatHub.IsOnline(p.email),
                    lastSeen = p.LastSeen,
                    unread = privateUnread.TryGetValue(p.email.ToLowerInvariant(), out var n) ? n : 0,
                    lastActivity = lastSpoke.TryGetValue(p.email.ToLowerInvariant(), out var t)
                        ? t
                        : DateTime.MinValue
                })
                .OrderByDescending(p => p.lastActivity)
                .ToList();

                // Groups the user has hidden by deleting the chat.
                var hiddenGroupIds = await _context.GroupChatDeletions
                    .Where(d => d.UserName.ToLower() == user && d.IsHidden)
                    .Select(d => d.GroupId)
                    .ToListAsync();

                var myGroups = await _context.UserGroups
                    .Where(ug => ug.UserName.ToLower() == user && !hiddenGroupIds.Contains(ug.GroupId))
                    .Include(ug => ug.Group)
                    .OrderByDescending(ug => ug.Group.CreatedAt)
                    .Select(ug => new
                    {
                        GroupId = ug.GroupId,
                        Name = ug.Group.Name ?? "Unnamed Group",
                        ImageUrl = string.IsNullOrEmpty(ug.Group.ImageUrl) ? "/images/default-group.png" : ug.Group.ImageUrl
                    })
                    .ToListAsync();

                var groupIds = myGroups.Select(g => g.GroupId).ToList();
                var groupUnread = await GroupUnreadAsync(user, groupIds);

                // Same for groups. A group nobody has posted in yet falls back
                // to when it was created, so a brand new group still appears
                // near the top rather than at the bottom.
                var groupActivity = (await _context.GroupMessages
                    .Where(m => groupIds.Contains(m.GroupId))
                    .GroupBy(m => m.GroupId)
                    .Select(g => new { groupId = g.Key, at = g.Max(x => x.SentAt) })
                    .ToListAsync())
                    .ToDictionary(x => x.groupId, x => x.at);

                var createdAt = (await _context.Groups
                    .Where(g => groupIds.Contains(g.Id))
                    .Select(g => new { g.Id, g.CreatedAt })
                    .ToListAsync())
                    .ToDictionary(x => x.Id, x => x.CreatedAt);

                var groups = myGroups.Select(g => new
                {
                    g.GroupId,
                    g.Name,
                    g.ImageUrl,
                    unread = groupUnread.TryGetValue(g.GroupId, out var n) ? n : 0,
                    lastActivity = groupActivity.TryGetValue(g.GroupId, out var t)
                        ? t
                        : (createdAt.TryGetValue(g.GroupId, out var c) ? c : DateTime.MinValue)
                })
                .OrderByDescending(g => g.lastActivity)
                .ToList();

                return Json(new { privateChats, groups });
            }
            catch (Exception ex)
            {
                // Log the detail, return a generic message - the old version
                // echoed the exception (and therefore schema details) to the client.
                _logger.LogError(ex, "Failed to load chat partners for {User}", user);
                return StatusCode(500, "Could not load chats.");
            }
        }

        /// <summary>
        /// Unread count per group: messages newer than this member's read
        /// pointer, not sent by them, not hidden by them, and not older than
        /// the point where they last cleared the chat.
        ///
        /// Every condition is a correlated subquery so the whole thing is one
        /// GROUP BY on the server and what comes back is a handful of numbers.
        /// The earlier version pulled every group message the member had not
        /// sent - across all their groups, for all time - into memory and
        /// counted them in C#. That is fine at forty messages and ruinous at
        /// forty thousand, and it is the sidebar, so it runs on every load.
        /// </summary>
        private async Task<Dictionary<int, int>> GroupUnreadAsync(string user, List<int> groupIds)
        {
            if (groupIds.Count == 0) return new Dictionary<int, int>();

            var counts = await _context.GroupMessages
                .Where(m => groupIds.Contains(m.GroupId) && m.Sender.ToLower() != user)

                // newer than my pointer; no row yet means I have read nothing
                .Where(m => m.Id > (_context.GroupReads
                    .Where(r => r.UserName == user && r.GroupId == m.GroupId)
                    .Select(r => (int?)r.LastReadMessageId)
                    .FirstOrDefault() ?? 0))

                // not one I deleted for myself
                .Where(m => !_context.GroupMessageDeletions
                    .Any(x => x.UserName == user && x.GroupMessageId == m.Id))

                // If I have cleared this chat, only what arrived afterwards
                // counts. Spelled as two EXISTS rather than one negated
                // comparison because DeletedAt is nullable, and "no timestamp"
                // has to mean "nothing survives the clear" - which is what the
                // in-memory version did, and what a NULL comparison in SQL
                // would quietly get wrong.
                .Where(m => !_context.GroupChatDeletions
                                .Any(d => d.UserName.ToLower() == user && d.GroupId == m.GroupId)
                         || _context.GroupChatDeletions
                                .Any(d => d.UserName.ToLower() == user
                                       && d.GroupId == m.GroupId
                                       && m.SentAt > d.DeletedAt))

                .GroupBy(m => m.GroupId)
                .Select(g => new { GroupId = g.Key, Count = g.Count() })
                .ToListAsync();

            return counts.ToDictionary(x => x.GroupId, x => x.Count);
        }
    }
}
