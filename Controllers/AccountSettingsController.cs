using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ChatApp.Data;
using ChatApp.Models;
using ChatApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Controllers
{
    /// <summary>
    /// The account settings page: what the address is, and the two ways to
    /// change what gets you in - the email itself, and the password.
    /// </summary>
    [Route("api/account")]
    [ApiController]
    [Authorize]
    public class AccountSettingsController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IAppEmailSender _email;
        private readonly ILogger<AccountSettingsController> _logger;

        public AccountSettingsController(ApplicationDbContext context,
                                         UserManager<ApplicationUser> userManager,
                                         SignInManager<ApplicationUser> signInManager,
                                         IAppEmailSender email,
                                         ILogger<AccountSettingsController> logger)
        {
            _context = context;
            _userManager = userManager;
            _signInManager = signInManager;
            _email = email;
            _logger = logger;
        }

        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        private static string Hash(string code) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

        private static string NewCode() =>
            RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        [HttpGet("settings")]
        public async Task<IActionResult> Settings()
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            return Ok(new
            {
                email = user.Email ?? user.UserName ?? string.Empty,
                nickname = user.Nickname ?? string.Empty,
                soundEnabled = user.SoundEnabled
            });
        }

        /// <summary>On the account, so it holds on every device they use.</summary>
        [HttpPost("sound")]
        public async Task<IActionResult> SetSound([FromBody] SoundRequest request)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            user.SoundEnabled = request?.Enabled ?? true;
            await _userManager.UpdateAsync(user);

            return Ok(new { soundEnabled = user.SoundEnabled });
        }

        /// <summary>
        /// Everyone this account has blocked. Blocking is done from a chat,
        /// but the chat can be deleted afterwards - without this list there
        /// would be no way back to someone you had blocked and then lost.
        /// </summary>
        [HttpGet("blocked")]
        public async Task<IActionResult> Blocked()
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();

            var names = await _context.Blocks
                .Where(b => b.BlockerUserName == me)
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => b.BlockedUserName)
                .ToListAsync();

            if (names.Count == 0) return Ok(Array.Empty<object>());

            var people = await _context.Users
                .Where(u => u.UserName != null && names.Contains(u.UserName.ToLower()))
                .Select(u => new
                {
                    userName = u.UserName!,
                    nickname = u.Nickname ?? string.Empty,
                    avatarUrl = string.IsNullOrEmpty(u.AvatarUrl) ? "/images/default-avatar.png" : u.AvatarUrl
                })
                .ToListAsync();

            // Keep the order the blocks were made in, newest first.
            var byName = people.ToDictionary(p => p.userName.ToLowerInvariant(), p => p);
            var ordered = names
                .Where(n => byName.ContainsKey(n))
                .Select(n => byName[n]);

            return Ok(ordered);
        }

        /// <summary>
        /// Closing the account for good. The password is asked for because
        /// this is the one action here that cannot be undone.
        /// </summary>
        [HttpPost("delete")]
        public async Task<IActionResult> DeleteAccount([FromBody] PasswordConfirmRequest request)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user?.UserName == null) return Unauthorized();

            if (string.IsNullOrEmpty(request?.CurrentPassword))
                return BadRequest(new { message = "Enter your password to confirm." });

            if (!await _userManager.CheckPasswordAsync(user, request.CurrentPassword))
                return BadRequest(new { message = "That password is not right." });

            var me = user.UserName.ToLowerInvariant();

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                await EraseEverywhereAsync(me);

                var result = await _userManager.DeleteAsync(user);
                if (!result.Succeeded)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { message = string.Join(" ", result.Errors.Select(e => e.Description)) });
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Deleting {User} failed and was rolled back", me);
                return StatusCode(500, new { message = "The account could not be deleted. Nothing was removed." });
            }

            await _signInManager.SignOutAsync();
            _logger.LogInformation("Account {User} deleted", me);

            return Ok(new { message = "Your account has been deleted." });
        }

        /// <summary>
        /// Removes everything this user leaves behind. Their own messages go;
        /// so does their side of every conversation, their group membership,
        /// their friendships, blocks, reactions and uploads.
        ///
        /// Groups they created are handed to the longest-standing remaining
        /// member rather than deleted - other people's conversations should
        /// not disappear because the person who started the group left. A
        /// group with nobody left in it is removed.
        /// </summary>
        private async Task EraseEverywhereAsync(string me)
        {
            var myMessages = await _context.Messages
                .Where(m => m.User.ToLower() == me || m.Receiver.ToLower() == me)
                .ToListAsync();
            _context.Messages.RemoveRange(myMessages);

            var myGroupMessages = await _context.GroupMessages
                .Where(m => m.Sender.ToLower() == me)
                .ToListAsync();
            _context.GroupMessages.RemoveRange(myGroupMessages);

            var myGroupIds = await _context.UserGroups
                .Where(ug => ug.UserName.ToLower() == me)
                .Select(ug => ug.GroupId)
                .ToListAsync();

            _context.UserGroups.RemoveRange(
                await _context.UserGroups.Where(ug => ug.UserName.ToLower() == me).ToListAsync());

            _context.GroupChatDeletions.RemoveRange(
                await _context.GroupChatDeletions.Where(d => d.UserName.ToLower() == me).ToListAsync());

            _context.GroupReads.RemoveRange(
                await _context.GroupReads.Where(r => r.UserName == me).ToListAsync());

            _context.GroupMessageDeletions.RemoveRange(
                await _context.GroupMessageDeletions.Where(d => d.UserName == me).ToListAsync());

            _context.Friendships.RemoveRange(
                await _context.Friendships
                    .Where(f => f.RequesterUserName.ToLower() == me || f.AddresseeUserName.ToLower() == me)
                    .ToListAsync());

            _context.Blocks.RemoveRange(
                await _context.Blocks
                    .Where(b => b.BlockerUserName == me || b.BlockedUserName == me)
                    .ToListAsync());

            _context.Reactions.RemoveRange(
                await _context.Reactions.Where(r => r.UserName == me).ToListAsync());

            _context.Attachments.RemoveRange(
                await _context.Attachments.Where(a => a.UploadedBy.ToLower() == me).ToListAsync());

            _context.PasswordResetCodes.RemoveRange(
                await _context.PasswordResetCodes.Where(c => c.UserName == me).ToListAsync());

            _context.EmailChangeRequests.RemoveRange(
                await _context.EmailChangeRequests.Where(r => r.UserName == me).ToListAsync());

            await _context.SaveChangesAsync();

            // Now that the membership rows are gone, deal with the groups this
            // person owned.
            var owned = await _context.Groups
                .Where(g => g.CreatorUserName.ToLower() == me)
                .ToListAsync();

            foreach (var group in owned)
            {
                var successor = await _context.UserGroups
                    .Where(ug => ug.GroupId == group.Id)
                    .OrderBy(ug => ug.Id)
                    .Select(ug => ug.UserName)
                    .FirstOrDefaultAsync();

                if (successor == null)
                {
                    _context.GroupMessages.RemoveRange(
                        await _context.GroupMessages.Where(m => m.GroupId == group.Id).ToListAsync());
                    _context.Groups.Remove(group);
                }
                else
                {
                    group.CreatorUserName = successor;

                    // The new owner is an admin by definition.
                    var row = await _context.UserGroups
                        .FirstOrDefaultAsync(ug => ug.GroupId == group.Id && ug.UserName == successor);
                    if (row != null) row.IsAdmin = true;
                }
            }

            // And groups that are now empty for any other reason.
            var emptyGroups = await _context.Groups
                .Where(g => !_context.UserGroups.Any(ug => ug.GroupId == g.Id))
                .ToListAsync();

            foreach (var group in emptyGroups)
            {
                _context.GroupMessages.RemoveRange(
                    await _context.GroupMessages.Where(m => m.GroupId == group.Id).ToListAsync());
                _context.Groups.Remove(group);
            }

            await _context.SaveChangesAsync();
        }

        // ------------------------------------------------------------------
        //  Changing the email address
        // ------------------------------------------------------------------

        /// <summary>
        /// Sends a code to the address currently ON THE ACCOUNT, not to the
        /// new one. Proving you can still read the address on file is what
        /// authorises the move.
        ///
        /// The trade-off is that nothing checks the new address, so a typo
        /// there leaves an account nobody can sign in to. The new address is
        /// therefore validated for shape, checked against other accounts, and
        /// shown back on the confirmation step before anything happens.
        /// </summary>
        [HttpPost("email/request-code")]
        public async Task<IActionResult> RequestEmailCode([FromBody] EmailRequest request)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();

            var newEmail = (request?.NewEmail ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(newEmail) || !newEmail.Contains('@') || newEmail.Length < 5)
                return BadRequest(new { message = "That does not look like an email address." });

            var user = await _userManager.GetUserAsync(User);
            if (user?.UserName == null) return Unauthorized();

            if (string.Equals(newEmail, user.Email, StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { message = "That is already your address." });

            var taken = await _context.Users.AnyAsync(u =>
                (u.Email != null && u.Email.ToLower() == newEmail) ||
                (u.UserName != null && u.UserName.ToLower() == newEmail));
            if (taken)
                return BadRequest(new { message = "Another account already uses that address." });

            var now = DateTime.UtcNow;
            var previous = await _context.EmailChangeRequests
                .Where(r => r.UserName == me)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            if (previous != null && now - previous.CreatedAt < EmailChangeRequest.RequestCooldown)
            {
                var wait = (int)Math.Ceiling(
                    (EmailChangeRequest.RequestCooldown - (now - previous.CreatedAt)).TotalSeconds);
                return BadRequest(new { message = $"A code was just sent. Try again in {wait}s." });
            }

            // Only the newest request can be used.
            var outstanding = await _context.EmailChangeRequests
                .Where(r => r.UserName == me && r.UsedAt == null)
                .ToListAsync();
            foreach (var old in outstanding) old.UsedAt = now;

            var code = NewCode();
            _context.EmailChangeRequests.Add(new EmailChangeRequest
            {
                UserName = me,
                NewEmail = newEmail,
                CodeHash = Hash(code),
                CreatedAt = now,
                ExpiresAt = now.Add(EmailChangeRequest.Lifetime)
            });
            await _context.SaveChangesAsync();

            var minutes = (int)EmailChangeRequest.Lifetime.TotalMinutes;
            var currentAddress = user.Email ?? user.UserName;

            await _email.SendAsync(currentAddress,
                "Confirm your ChatApp email change",
                $@"<div style=""font-family:system-ui,Segoe UI,Arial,sans-serif;font-size:15px;color:#111"">
                     <p>Someone asked to move this ChatApp account to <strong>{newEmail}</strong>.</p>
                     <p style=""font-size:30px;font-weight:700;letter-spacing:6px;margin:22px 0"">{code}</p>
                     <p>The code is good for {minutes} minutes. If this wasn't you, ignore this message and
                        change your password - nothing has moved yet.</p>
                   </div>",
                $"Your ChatApp confirmation code is {code}. It moves this account to {newEmail} and " +
                $"expires in {minutes} minutes. If this wasn't you, ignore it and change your password.");

            return Ok(new
            {
                message = $"A code is on its way to {currentAddress}, the address on your account.",
                newEmail
            });
        }

        [HttpPost("email/confirm")]
        public async Task<IActionResult> ConfirmEmail([FromBody] CodeRequest request)
        {
            var me = CurrentUser;
            if (string.IsNullOrEmpty(me)) return Unauthorized();

            var code = (request?.Code ?? string.Empty).Trim();
            if (code.Length != 6 || !code.All(char.IsDigit))
                return BadRequest(new { message = "Enter the six-digit code." });

            var row = await _context.EmailChangeRequests
                .Where(r => r.UserName == me)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            var now = DateTime.UtcNow;
            if (row == null || !row.IsUsable(now))
                return BadRequest(new { message = "That code has expired. Ask for a new one." });

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(row.CodeHash), Encoding.UTF8.GetBytes(Hash(code))))
            {
                row.Attempts += 1;
                await _context.SaveChangesAsync();

                var left = EmailChangeRequest.MaxAttempts - row.Attempts;
                return BadRequest(new { message = left > 0
                    ? $"That code is not right. {left} attempt{(left == 1 ? "" : "s")} left."
                    : "Too many wrong attempts. Ask for a new code." });
            }

            var user = await _userManager.GetUserAsync(User);
            if (user?.UserName == null) return Unauthorized();

            var oldEmail = user.Email;
            var oldUserName = user.UserName.ToLowerInvariant();
            var newEmail = row.NewEmail;

            // Last check before committing: somebody else may have taken the
            // address during the fifteen minutes this code was valid.
            var taken = await _context.Users.AnyAsync(u => u.Id != user.Id &&
                ((u.Email != null && u.Email.ToLower() == newEmail) ||
                 (u.UserName != null && u.UserName.ToLower() == newEmail)));
            if (taken)
                return BadRequest(new { message = "Another account has taken that address in the meantime." });

            // This app keys EVERYTHING on the user name - messages, group
            // membership, friendships, blocks, read pointers, reactions,
            // uploads. Since the user name IS the email address, changing one
            // means rewriting all of it, and it has to be all or nothing.
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                user.Email = newEmail;
                user.NormalizedEmail = newEmail.ToUpperInvariant();
                user.UserName = newEmail;
                user.NormalizedUserName = newEmail.ToUpperInvariant();

                var update = await _userManager.UpdateAsync(user);
                if (!update.Succeeded)
                {
                    await transaction.RollbackAsync();
                    return BadRequest(new { message = string.Join(" ", update.Errors.Select(e => e.Description)) });
                }

                await RenameEverywhereAsync(oldUserName, newEmail);

                row.UsedAt = now;
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Email change for {User} failed and was rolled back", oldUserName);
                return StatusCode(500, new { message = "The change could not be completed. Nothing was altered." });
            }

            // The sign-in cookie still names the old user; refresh it or the
            // next request looks like a stranger.
            await _signInManager.RefreshSignInAsync(user);

            _logger.LogInformation("Account {Old} is now {New}", oldUserName, newEmail);

            // Courtesy note to the address they just left, so a change nobody
            // meant to make does not go unnoticed.
            if (!string.IsNullOrWhiteSpace(oldEmail))
            {
                await _email.SendAsync(oldEmail,
                    "Your ChatApp address was changed",
                    $@"<div style=""font-family:system-ui,Segoe UI,Arial,sans-serif;font-size:15px;color:#111"">
                         <p>Your ChatApp account now signs in as <strong>{newEmail}</strong>.</p>
                         <p>If this wasn't you, reset the password from the sign-in page straight away.</p>
                       </div>",
                    $"Your ChatApp account now signs in as {newEmail}. If this wasn't you, reset your password.");
            }

            return Ok(new { message = "Your email address has been changed.", email = newEmail });
        }

        /// <summary>
        /// Rewrites the old user name to the new one everywhere it is stored.
        /// Every table here holds a user name as plain text - none of them are
        /// foreign keys - so this is the whole cost of the account being
        /// identified by its address.
        /// </summary>
        private async Task RenameEverywhereAsync(string oldName, string newName)
        {
            foreach (var m in await _context.Messages.Where(x => x.User.ToLower() == oldName).ToListAsync())
                m.User = newName;

            foreach (var m in await _context.Messages.Where(x => x.Receiver.ToLower() == oldName).ToListAsync())
                m.Receiver = newName;

            foreach (var m in await _context.GroupMessages.Where(x => x.Sender.ToLower() == oldName).ToListAsync())
                m.Sender = newName;

            foreach (var ug in await _context.UserGroups.Where(x => x.UserName.ToLower() == oldName).ToListAsync())
                ug.UserName = newName;

            foreach (var d in await _context.GroupChatDeletions.Where(x => x.UserName.ToLower() == oldName).ToListAsync())
                d.UserName = newName;

            foreach (var r in await _context.GroupReads.Where(x => x.UserName.ToLower() == oldName).ToListAsync())
                r.UserName = newName;

            foreach (var d in await _context.GroupMessageDeletions.Where(x => x.UserName.ToLower() == oldName).ToListAsync())
                d.UserName = newName;

            foreach (var f in await _context.Friendships.Where(x => x.RequesterUserName.ToLower() == oldName).ToListAsync())
                f.RequesterUserName = newName;

            foreach (var f in await _context.Friendships.Where(x => x.AddresseeUserName.ToLower() == oldName).ToListAsync())
                f.AddresseeUserName = newName;

            foreach (var b in await _context.Blocks.Where(x => x.BlockerUserName.ToLower() == oldName).ToListAsync())
                b.BlockerUserName = newName;

            foreach (var b in await _context.Blocks.Where(x => x.BlockedUserName.ToLower() == oldName).ToListAsync())
                b.BlockedUserName = newName;

            foreach (var r in await _context.Reactions.Where(x => x.UserName.ToLower() == oldName).ToListAsync())
                r.UserName = newName;

            foreach (var a in await _context.Attachments.Where(x => x.UploadedBy.ToLower() == oldName).ToListAsync())
                a.UploadedBy = newName;

            foreach (var c in await _context.PasswordResetCodes.Where(x => x.UserName == oldName).ToListAsync())
                c.UserName = newName;

            foreach (var r in await _context.EmailChangeRequests.Where(x => x.UserName == oldName).ToListAsync())
                r.UserName = newName;
        }

        // ------------------------------------------------------------------
        //  Changing the password
        // ------------------------------------------------------------------

        /// <summary>
        /// No code here: knowing the current password is the proof. Identity
        /// checks it, applies its own policy to the new one, and rotates the
        /// security stamp - which is why the sign-in has to be refreshed
        /// afterwards or this very session is logged out.
        /// </summary>
        [HttpPost("password")]
        public async Task<IActionResult> ChangePassword([FromBody] PasswordRequest request)
        {
            var user = await _userManager.GetUserAsync(User);
            if (user == null) return Unauthorized();

            if (string.IsNullOrEmpty(request?.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
                return BadRequest(new { message = "Fill in both passwords." });

            var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
            if (!result.Succeeded)
            {
                var wrongCurrent = result.Errors.Any(e => e.Code == "PasswordMismatch");
                var message = wrongCurrent
                    ? "Your current password is not right."
                    : string.Join(" ", result.Errors.Select(e => e.Description));
                return BadRequest(new { message });
            }

            await _signInManager.RefreshSignInAsync(user);
            _logger.LogInformation("Password changed for {User}", user.UserName);

            return Ok(new { message = "Your password has been changed." });
        }

        public class EmailRequest
        {
            public string? NewEmail { get; set; }
        }

        public class CodeRequest
        {
            public string? Code { get; set; }
        }

        public class SoundRequest
        {
            public bool Enabled { get; set; }
        }

        public class PasswordConfirmRequest
        {
            public string? CurrentPassword { get; set; }
        }

        public class PasswordRequest
        {
            public string? CurrentPassword { get; set; }
            public string? NewPassword { get; set; }
        }
    }
}
