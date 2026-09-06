using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using ChatApp.Data;
using ChatApp.Models;
using System.Linq;

namespace ChatApp.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // this endpoint lists every user's email - never expose it anonymously
    public class UsersController : ControllerBase
    {
        private const int MaxResults = 20;

        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApplicationDbContext _context;

        public UsersController(UserManager<ApplicationUser> userManager, ApplicationDbContext context)
        {
            _userManager = userManager;
            _context = context;
        }

        [HttpGet("search")]
        public IActionResult Search(string term)
        {
            // Require a real search term so the endpoint can't be used to dump
            // the whole user table one letter at a time.
            if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
                return Ok(Enumerable.Empty<object>());

            term = term.Trim().ToLowerInvariant();

            var currentUser = User.Identity?.Name?.ToLowerInvariant();

            // Anyone who has blocked the caller drops out of their results
            // entirely. The reverse is NOT filtered: you can still find someone
            // you blocked, which is how you'd go and unblock them.
            var blockedMe = _context.Blocks
                .Where(b => b.BlockedUserName == currentUser)
                .Select(b => b.BlockerUserName);

            var users = _userManager.Users
                .Where(u => (u.Email != null && u.Email.ToLower().Contains(term)) ||
                            (u.Nickname != null && u.Nickname.ToLower().Contains(term)))
                .Where(u => u.UserName == null || u.UserName.ToLower() != currentUser)
                .Where(u => u.UserName == null || !blockedMe.Contains(u.UserName.ToLower()))
                .OrderBy(u => u.Nickname)
                .Take(MaxResults)
                .Select(u => new
                {
                    u.Nickname,
                    u.Email,
                    // the chat header and search row both need this - without it
                    // opening a chat from search showed the default avatar
                    AvatarUrl = string.IsNullOrEmpty(u.AvatarUrl) ? "/images/default-avatar.png" : u.AvatarUrl
                })
                .ToList();

            return Ok(users);
        }
    }
}
