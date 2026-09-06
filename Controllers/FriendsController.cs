using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using ChatApp.Data;
using ChatApp.Services;
using ChatApp.Models;
using ChatApp.Hubs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ChatApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class FriendsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly ILogger<FriendsController> _logger;

        public FriendsController(
            ApplicationDbContext context,
            IHubContext<ChatHub> hubContext,
            ILogger<FriendsController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _logger = logger;
        }

        // Everything is keyed on the lower-cased user name, same as the rest of
        // the app - see NameUserIdProvider.
        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        // ------------------------------------------------------------------
        // Shared helper. Also used by ChatHub / GroupController / Messages, so
        // the friends-only rule is defined in exactly one place.
        // ------------------------------------------------------------------
        public static Task<bool> AreFriendsAsync(ApplicationDbContext db, string a, string b)
        {
            a = a.Trim().ToLowerInvariant();
            b = b.Trim().ToLowerInvariant();

            return db.Friendships.AnyAsync(f =>
                f.Status == FriendshipStatus.Accepted &&
                ((f.RequesterUserName == a && f.AddresseeUserName == b) ||
                 (f.RequesterUserName == b && f.AddresseeUserName == a)));
        }

        private Task<Friendship?> FindPairAsync(string a, string b)
        {
            return _context.Friendships.FirstOrDefaultAsync(f =>
                (f.RequesterUserName == a && f.AddresseeUserName == b) ||
                (f.RequesterUserName == b && f.AddresseeUserName == a));
        }

        /// <summary>Accepted friends, shaped for the sidebar list.</summary>
        [HttpGet("list")]
        public async Task<IActionResult> GetFriends()
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();

            var names = await _context.Friendships
                .Where(f => f.Status == FriendshipStatus.Accepted &&
                            (f.RequesterUserName == me || f.AddresseeUserName == me))
                .Select(f => f.RequesterUserName == me ? f.AddresseeUserName : f.RequesterUserName)
                .ToListAsync();

            var rows = await _context.Users
                .Where(u => u.UserName != null && names.Contains(u.UserName.ToLower()))
                .OrderBy(u => u.Nickname)
                .Select(u => new
                {
                    email = u.UserName ?? string.Empty,
                    nickname = u.Nickname ?? "Anonymous",
                    avatarUrl = string.IsNullOrEmpty(u.AvatarUrl) ? "/images/default-avatar.png" : u.AvatarUrl,
                    u.LastSeen
                })
                .ToListAsync();

            // Presence is a live fact about the hub, not a column, so it is
            // read here rather than queried.
            var friends = rows.Select(r => new
            {
                r.email,
                r.nickname,
                r.avatarUrl,
                online = ChatApp.Hubs.ChatHub.IsOnline(r.email),
                lastSeen = r.LastSeen
            });

            return Ok(friends);
        }

        /// <summary>Incoming requests waiting on me, for the notification bell.</summary>
        [HttpGet("requests")]
        public async Task<IActionResult> GetPendingRequests()
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();

            var pending = await _context.Friendships
                .Where(f => f.AddresseeUserName == me && f.Status == FriendshipStatus.Pending)
                .OrderByDescending(f => f.CreatedAt)
                .Select(f => new { f.Id, f.RequesterUserName, f.CreatedAt })
                .ToListAsync();

            var names = pending.Select(p => p.RequesterUserName).ToList();

            var users = await _context.Users
                .Where(u => u.UserName != null && names.Contains(u.UserName.ToLower()))
                .ToDictionaryAsync(
                    u => u.UserName!.ToLowerInvariant(),
                    u => new
                    {
                        Nickname = u.Nickname ?? "Anonymous",
                        AvatarUrl = string.IsNullOrEmpty(u.AvatarUrl) ? "/images/default-avatar.png" : u.AvatarUrl
                    });

            var result = pending.Select(p =>
            {
                users.TryGetValue(p.RequesterUserName, out var u);
                return new
                {
                    id = p.Id,
                    email = p.RequesterUserName,
                    nickname = u?.Nickname ?? p.RequesterUserName,
                    avatarUrl = u?.AvatarUrl ?? "/images/default-avatar.png",
                    createdAt = p.CreatedAt
                };
            });

            return Ok(result);
        }

        /// <summary>
        /// Relationship between me and a list of users, so search results can
        /// render the right button without one request per row.
        /// </summary>
        [HttpPost("statuses")]
        public async Task<IActionResult> GetStatuses([FromBody] List<string> userNames)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();
            if (userNames == null || userNames.Count == 0) return Ok(new Dictionary<string, string>());

            var wanted = userNames
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();

            var rows = await _context.Friendships
                .Where(f => (f.RequesterUserName == me && wanted.Contains(f.AddresseeUserName)) ||
                            (f.AddresseeUserName == me && wanted.Contains(f.RequesterUserName)))
                .ToListAsync();

            var map = new Dictionary<string, string>();
            foreach (var name in wanted)
            {
                var row = rows.FirstOrDefault(f =>
                    (f.RequesterUserName == me && f.AddresseeUserName == name) ||
                    (f.AddresseeUserName == me && f.RequesterUserName == name));

                if (row == null) { map[name] = "none"; continue; }

                map[name] = row.Status switch
                {
                    FriendshipStatus.Accepted => "friends",
                    // distinguish the two pending directions - one shows
                    // "requested", the other shows accept/decline
                    FriendshipStatus.Pending when row.RequesterUserName == me => "outgoing",
                    FriendshipStatus.Pending => "incoming",
                    _ => "none"
                };
            }

            return Ok(map);
        }

        [HttpPost("request")]
        public async Task<IActionResult> SendRequest([FromBody] FriendRequestDto dto)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();

            // Ten in hand, then one every twenty seconds. Adding a handful of
            // people you already know in one sitting is normal; papering the
            // whole user table with requests is not.
            if (!RateLimiter.Allow("friendreq:" + me, 0.05, 10))
                return StatusCode(429, "You've sent a lot of requests just now. Try again shortly.");

            if (dto == null || string.IsNullOrWhiteSpace(dto.UserName))
                return BadRequest("User name is required.");

            var target = dto.UserName.Trim().ToLowerInvariant();

            if (target == me)
                return BadRequest("You cannot add yourself.");

            var targetUser = await _context.Users
                .FirstOrDefaultAsync(u => u.UserName != null && u.UserName.ToLower() == target);
            if (targetUser == null)
                return NotFound("User not found.");

            var existing = await FindPairAsync(me, target);
            if (existing != null)
            {
                if (existing.Status == FriendshipStatus.Accepted)
                    return BadRequest("You are already friends.");

                if (existing.Status == FriendshipStatus.Pending)
                {
                    // They already asked me - treat my "add" as an accept
                    // rather than creating a second, mirrored row.
                    if (existing.AddresseeUserName == me)
                        return await AcceptInternalAsync(existing, me);

                    return BadRequest("Request already sent.");
                }

                // A previously declined pair can be asked again.
                existing.Status = FriendshipStatus.Pending;
                existing.RequesterUserName = me;
                existing.AddresseeUserName = target;
                existing.CreatedAt = DateTime.UtcNow;
                existing.RespondedAt = null;
                await _context.SaveChangesAsync();
                await NotifyRequestAsync(target);
                return Ok(new { status = "outgoing" });
            }

            _context.Friendships.Add(new Friendship
            {
                RequesterUserName = me,
                AddresseeUserName = target,
                Status = FriendshipStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });

            await _context.SaveChangesAsync();
            await NotifyRequestAsync(target);

            _logger.LogInformation("Friend request {From} -> {To}", me, target);
            return Ok(new { status = "outgoing" });
        }

        [HttpPost("accept")]
        public async Task<IActionResult> Accept([FromBody] FriendRequestDto dto)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();
            if (dto == null || string.IsNullOrWhiteSpace(dto.UserName))
                return BadRequest("User name is required.");

            var other = dto.UserName.Trim().ToLowerInvariant();

            var row = await _context.Friendships.FirstOrDefaultAsync(f =>
                f.RequesterUserName == other &&
                f.AddresseeUserName == me &&
                f.Status == FriendshipStatus.Pending);

            if (row == null) return NotFound("No pending request from that user.");

            return await AcceptInternalAsync(row, me);
        }

        private async Task<IActionResult> AcceptInternalAsync(Friendship row, string me)
        {
            row.Status = FriendshipStatus.Accepted;
            row.RespondedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            var other = row.RequesterUserName == me ? row.AddresseeUserName : row.RequesterUserName;

            // Both sides refresh: the new friend appears in each sidebar and
            // both badges recount.
            await _hubContext.Clients.User(other).SendAsync("FriendsUpdated");
            await _hubContext.Clients.User(other).SendAsync("FriendRequestsUpdated");
            await _hubContext.Clients.User(other).SendAsync("FriendRequestAccepted", me);
            await _hubContext.Clients.User(me).SendAsync("FriendsUpdated");
            await _hubContext.Clients.User(me).SendAsync("FriendRequestsUpdated");

            _logger.LogInformation("Friendship accepted between {A} and {B}", me, other);
            return Ok(new { status = "friends" });
        }

        [HttpPost("decline")]
        public async Task<IActionResult> Decline([FromBody] FriendRequestDto dto)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();
            if (dto == null || string.IsNullOrWhiteSpace(dto.UserName))
                return BadRequest("User name is required.");

            var other = dto.UserName.Trim().ToLowerInvariant();

            var row = await _context.Friendships.FirstOrDefaultAsync(f =>
                f.RequesterUserName == other &&
                f.AddresseeUserName == me &&
                f.Status == FriendshipStatus.Pending);

            if (row == null) return NotFound("No pending request from that user.");

            row.Status = FriendshipStatus.Declined;
            row.RespondedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            await _hubContext.Clients.User(me).SendAsync("FriendRequestsUpdated");

            // The requester also needs to know - without this their search row
            // stayed on "Requested" until they reloaded the page.
            await _hubContext.Clients.User(other).SendAsync("FriendRequestDeclined", me);
            await _hubContext.Clients.User(other).SendAsync("FriendsUpdated");

            return Ok(new { status = "none" });
        }

        [HttpPost("remove")]
        public async Task<IActionResult> Remove([FromBody] FriendRequestDto dto)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();
            if (dto == null || string.IsNullOrWhiteSpace(dto.UserName))
                return BadRequest("User name is required.");

            var other = dto.UserName.Trim().ToLowerInvariant();

            var row = await FindPairAsync(me, other);
            if (row == null) return NotFound("Not friends.");

            _context.Friendships.Remove(row);
            await _context.SaveChangesAsync();

            await _hubContext.Clients.User(other).SendAsync("FriendsUpdated");
            await _hubContext.Clients.User(me).SendAsync("FriendsUpdated");
            await _hubContext.Clients.User(other).SendAsync("FriendRequestsUpdated");

            return Ok(new { status = "none" });
        }

        private async Task NotifyRequestAsync(string target)
        {
            await _hubContext.Clients.User(target).SendAsync("FriendRequestReceived");
            await _hubContext.Clients.User(target).SendAsync("FriendRequestsUpdated");

            var me = CurrentUser;
            if (!string.IsNullOrEmpty(me))
                await _hubContext.Clients.User(me).SendAsync("FriendsUpdated");
        }

        public class FriendRequestDto
        {
            public string UserName { get; set; } = string.Empty;
        }
    }
}
