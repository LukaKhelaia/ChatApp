using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.Data;
using ChatApp.Hubs;
using ChatApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Controllers
{
    /// <summary>
    /// One emoji per person per message, in private chats and groups alike.
    /// Tapping the emoji you already picked takes it back; tapping a different
    /// one replaces it. That rule lives in the unique index on
    /// (Scope, MessageId, UserName) as well as in the code below.
    /// </summary>
    [Route("api/reactions")]
    [ApiController]
    [Authorize]
    public class ReactionsController : ControllerBase
    {
        /// <summary>
        /// The palette the picker offers. Reactions are stored as text, so
        /// without a whitelist this endpoint would be a way to write arbitrary
        /// strings into a column that is rendered on everyone else's screen.
        /// </summary>
        public static readonly string[] Palette = { "👍", "❤️", "😂", "😮", "😢", "🙏" };

        private static readonly HashSet<string> Allowed = new(Palette, StringComparer.Ordinal);

        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hub;
        private readonly ILogger<ReactionsController> _logger;

        public ReactionsController(ApplicationDbContext context, IHubContext<ChatHub> hub,
                                   ILogger<ReactionsController> logger)
        {
            _context = context;
            _hub = hub;
            _logger = logger;
        }

        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        private static string GroupName(int groupId) => $"group-{groupId}";

        [HttpPost("toggle")]
        public async Task<IActionResult> Toggle([FromBody] ReactionRequest request)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();
            if (request == null || request.MessageId <= 0) return BadRequest("Invalid message.");

            var emoji = (request.Emoji ?? string.Empty).Trim();
            if (!Allowed.Contains(emoji)) return BadRequest("Unsupported reaction.");

            var scope = request.IsGroup ? ReactionScope.Group : ReactionScope.Private;

            // Who is allowed to see - and therefore react to - this message.
            string? otherUser = null;
            int groupId = 0;

            if (scope == ReactionScope.Private)
            {
                var target = await _context.Messages
                    .Where(m => m.Id == request.MessageId)
                    .Select(m => new { m.User, m.Receiver })
                    .FirstOrDefaultAsync();

                if (target == null) return NotFound("No such message.");

                var sender = target.User.ToLowerInvariant();
                var receiver = target.Receiver.ToLowerInvariant();
                if (me != sender && me != receiver) return Forbid();

                otherUser = me == sender ? receiver : sender;

                // A block in either direction freezes the conversation, so it
                // freezes reactions too.
                if (await BlocksController.IsBlockedEitherWayAsync(_context, me, otherUser))
                    return Forbid();
            }
            else
            {
                var target = await _context.GroupMessages
                    .Where(m => m.Id == request.MessageId)
                    .Select(m => new { m.GroupId })
                    .FirstOrDefaultAsync();

                if (target == null) return NotFound("No such message.");
                groupId = target.GroupId;

                var isMember = await _context.UserGroups
                    .AnyAsync(ug => ug.GroupId == groupId && ug.UserName.ToLower() == me);
                if (!isMember) return Forbid();
            }

            var existing = await _context.Reactions
                .FirstOrDefaultAsync(r => r.Scope == scope
                                       && r.MessageId == request.MessageId
                                       && r.UserName == me);

            string? mine;

            if (existing == null)
            {
                _context.Reactions.Add(new Reaction
                {
                    Scope = scope,
                    MessageId = request.MessageId,
                    UserName = me,
                    Emoji = emoji,
                    CreatedAt = DateTime.UtcNow
                });
                mine = emoji;
            }
            else if (string.Equals(existing.Emoji, emoji, StringComparison.Ordinal))
            {
                _context.Reactions.Remove(existing);     // same emoji again = take it back
                mine = null;
            }
            else
            {
                existing.Emoji = emoji;                  // swap, keeping one per person
                existing.CreatedAt = DateTime.UtcNow;
                mine = emoji;
            }

            await _context.SaveChangesAsync();

            // Broadcast the whole list rather than the delta: two people
            // reacting at the same moment then converge on the same state
            // instead of each applying half of the other's change.
            var all = await ReactionsForAsync(scope, request.MessageId);

            if (scope == ReactionScope.Private)
            {
                await _hub.Clients.User(me).SendAsync("ReactionsUpdated", false, request.MessageId, all);
                if (!string.IsNullOrEmpty(otherUser))
                    await _hub.Clients.User(otherUser).SendAsync("ReactionsUpdated", false, request.MessageId, all);
            }
            else
            {
                await _hub.Clients.Group(GroupName(groupId))
                    .SendAsync("ReactionsUpdated", true, request.MessageId, all);
            }

            return Ok(new { mine, reactions = all });
        }

        private async Task<List<object>> ReactionsForAsync(ReactionScope scope, int messageId)
        {
            var map = await MessageExtras.ReactionsAsync(_context, scope, new[] { messageId });
            return map.TryGetValue(messageId, out var list) ? list : new List<object>();
        }

        public class ReactionRequest
        {
            public int MessageId { get; set; }
            public bool IsGroup { get; set; }
            public string? Emoji { get; set; }
        }
    }
}
