using System;
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
    /// Blocking is directional and independent of friendship: blocking someone
    /// does not unfriend them, and unblocking restores everything immediately.
    /// A block in EITHER direction stops messages passing between the two.
    /// </summary>
    [Route("api/blocks")]
    [ApiController]
    [Authorize]
    public class BlocksController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hub;
        private readonly ILogger<BlocksController> _logger;

        public BlocksController(ApplicationDbContext context, IHubContext<ChatHub> hub,
                                ILogger<BlocksController> logger)
        {
            _context = context;
            _hub = hub;
            _logger = logger;
        }

        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        /// <summary>True if either of the two has blocked the other.</summary>
        public static Task<bool> IsBlockedEitherWayAsync(ApplicationDbContext db, string a, string b)
        {
            a = a.Trim().ToLowerInvariant();
            b = b.Trim().ToLowerInvariant();
            return db.Blocks.AnyAsync(x =>
                (x.BlockerUserName == a && x.BlockedUserName == b) ||
                (x.BlockerUserName == b && x.BlockedUserName == a));
        }

        public static Task<bool> HasBlockedAsync(ApplicationDbContext db, string blocker, string blocked)
        {
            blocker = blocker.Trim().ToLowerInvariant();
            blocked = blocked.Trim().ToLowerInvariant();
            return db.Blocks.AnyAsync(x =>
                x.BlockerUserName == blocker && x.BlockedUserName == blocked);
        }

        /// <summary>Where the caller stands with one other user.</summary>
        [HttpGet("status")]
        public async Task<IActionResult> Status([FromQuery] string withUser)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me) || string.IsNullOrWhiteSpace(withUser))
                return BadRequest("Invalid user.");

            withUser = withUser.Trim().ToLowerInvariant();

            var rows = await _context.Blocks
                .Where(x => (x.BlockerUserName == me && x.BlockedUserName == withUser) ||
                            (x.BlockerUserName == withUser && x.BlockedUserName == me))
                .ToListAsync();

            return Ok(new
            {
                blockedByMe = rows.Any(r => r.BlockerUserName == me),
                blockedMe = rows.Any(r => r.BlockerUserName == withUser)
            });
        }

        [HttpPost("block")]
        public async Task<IActionResult> BlockUser([FromBody] BlockRequest request)
        {
            var me = CurrentUser;
            var target = request?.UserName?.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(me) || string.IsNullOrWhiteSpace(target))
                return BadRequest("Invalid user.");
            if (target == me)
                return BadRequest("You can't block yourself.");

            var exists = await _context.Users
                .AnyAsync(u => u.UserName != null && u.UserName.ToLower() == target);
            if (!exists) return NotFound("No such user.");

            if (!await HasBlockedAsync(_context, me, target))
            {
                _context.Blocks.Add(new Block
                {
                    BlockerUserName = me,
                    BlockedUserName = target,
                    CreatedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                _logger.LogInformation("{User} blocked {Target}", me, target);
            }

            // Both sides re-check: the blocked user's composer locks straight
            // away rather than only after their next reload.
            await _hub.Clients.User(target).SendAsync("BlockStateChanged", me);
            await _hub.Clients.User(me).SendAsync("BlockStateChanged", target);

            return Ok(new { blocked = true });
        }

        [HttpPost("unblock")]
        public async Task<IActionResult> UnblockUser([FromBody] BlockRequest request)
        {
            var me = CurrentUser;
            var target = request?.UserName?.Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(me) || string.IsNullOrWhiteSpace(target))
                return BadRequest("Invalid user.");

            var row = await _context.Blocks
                .FirstOrDefaultAsync(x => x.BlockerUserName == me && x.BlockedUserName == target);

            if (row != null)
            {
                _context.Blocks.Remove(row);
                await _context.SaveChangesAsync();
                _logger.LogInformation("{User} unblocked {Target}", me, target);
            }

            await _hub.Clients.User(target).SendAsync("BlockStateChanged", me);
            await _hub.Clients.User(me).SendAsync("BlockStateChanged", target);

            return Ok(new { blocked = false });
        }

        public class BlockRequest
        {
            public string? UserName { get; set; }
        }
    }
}
