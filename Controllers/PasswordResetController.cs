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
    /// Forgotten-password flow: ask for a code, prove you got it, set a new
    /// password. Anonymous by necessity - the whole point is that the person
    /// cannot sign in.
    ///
    /// The replies never say whether an address has an account. Someone who
    /// can tell the difference can use this form as a membership test, and
    /// "we sent a code if that address is registered" costs a real user
    /// nothing.
    /// </summary>
    [Route("api/password-reset")]
    [ApiController]
    [AllowAnonymous]
    public class PasswordResetController : ControllerBase
    {
        private const string NeutralReply =
            "If that address has an account, a code is on its way to it.";

        private readonly ApplicationDbContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAppEmailSender _email;
        private readonly ILogger<PasswordResetController> _logger;

        public PasswordResetController(ApplicationDbContext context,
                                       UserManager<ApplicationUser> userManager,
                                       IAppEmailSender email,
                                       ILogger<PasswordResetController> logger)
        {
            _context = context;
            _userManager = userManager;
            _email = email;
            _logger = logger;
        }

        private static string Hash(string code)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(code));
            return Convert.ToHexString(bytes);
        }

        /// <summary>Six digits from a cryptographic source, not Random.</summary>
        private static string NewCode() =>
            RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        private Task<PasswordResetCode?> NewestCodeAsync(string userName) =>
            _context.PasswordResetCodes
                .Where(c => c.UserName == userName)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync();

        [HttpPost("request")]
        public async Task<IActionResult> RequestCode([FromBody] EmailRequest request)
        {
            var email = (request?.Email ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest(new { message = "Enter your email address." });

            var user = await _userManager.FindByEmailAsync(email)
                       ?? await _userManager.FindByNameAsync(email);

            // No account: reply exactly as if there were one, and do nothing.
            if (user?.UserName == null)
            {
                _logger.LogInformation("Reset requested for unknown address {Email}", email);
                return Ok(new { message = NeutralReply });
            }

            var userName = user.UserName.ToLowerInvariant();
            var now = DateTime.UtcNow;

            // One code a minute. Without this the form is a way to have
            // somebody's inbox filled on demand.
            var previous = await NewestCodeAsync(userName);
            if (previous != null && now - previous.CreatedAt < PasswordResetCode.RequestCooldown)
            {
                var wait = (int)Math.Ceiling(
                    (PasswordResetCode.RequestCooldown - (now - previous.CreatedAt)).TotalSeconds);
                return Ok(new { message = $"A code was just sent. You can ask for another in {wait}s.", cooldown = wait });
            }

            // Any earlier code stops working the moment a new one is issued.
            var outstanding = await _context.PasswordResetCodes
                .Where(c => c.UserName == userName && c.UsedAt == null)
                .ToListAsync();
            foreach (var old in outstanding) old.UsedAt = now;

            var code = NewCode();
            _context.PasswordResetCodes.Add(new PasswordResetCode
            {
                UserName = userName,
                CodeHash = Hash(code),
                CreatedAt = now,
                ExpiresAt = now.Add(PasswordResetCode.Lifetime)
            });
            await _context.SaveChangesAsync();

            var minutes = (int)PasswordResetCode.Lifetime.TotalMinutes;
            var sent = await _email.SendAsync(
                user.Email ?? email,
                "Your ChatApp password reset code",
                $@"<div style=""font-family:system-ui,Segoe UI,Arial,sans-serif;font-size:15px;color:#111"">
                     <p>Someone asked to reset the password for this ChatApp account.</p>
                     <p style=""font-size:30px;font-weight:700;letter-spacing:6px;margin:22px 0"">{code}</p>
                     <p>The code is good for {minutes} minutes. If this wasn't you, ignore this message - nothing has changed.</p>
                   </div>",
                $"Your ChatApp password reset code is {code}. It expires in {minutes} minutes. " +
                "If you didn't ask for it, ignore this message.");

            if (!sent)
                _logger.LogWarning("Reset code for {User} could not be emailed - see the provider error above.", userName);

            return Ok(new { message = NeutralReply });
        }

        /// <summary>
        /// Checks a code without spending it, so the person can be told they
        /// mistyped before being asked to think up a new password.
        /// </summary>
        [HttpPost("verify")]
        public async Task<IActionResult> VerifyCode([FromBody] CodeRequest request)
        {
            var (row, error) = await CheckAsync(request?.Email, request?.Code);
            if (error != null) return BadRequest(new { message = error });

            return Ok(new { message = "Code accepted." });
        }

        [HttpPost("reset")]
        public async Task<IActionResult> Reset([FromBody] ResetRequest request)
        {
            if (string.IsNullOrEmpty(request?.NewPassword))
                return BadRequest(new { message = "Choose a new password." });

            var (row, error) = await CheckAsync(request.Email, request.Code);
            if (error != null || row == null) return BadRequest(new { message = error ?? "Something went wrong." });

            var user = await _userManager.FindByNameAsync(row.UserName);
            if (user == null) return BadRequest(new { message = "Something went wrong." });

            // Identity's own token does the actual work; the six digits are
            // only how we decided to trust this request.
            var token = await _userManager.GeneratePasswordResetTokenAsync(user);
            var result = await _userManager.ResetPasswordAsync(user, token, request.NewPassword);

            if (!result.Succeeded)
            {
                // Password-policy complaints belong to the person typing, so
                // these are passed through as they are.
                var message = string.Join(" ", result.Errors.Select(e => e.Description));
                return BadRequest(new { message });
            }

            row.UsedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Password reset completed for {User}", row.UserName);
            return Ok(new { message = "Your password has been changed. You can sign in with it now." });
        }

        /// <summary>
        /// Shared by verify and reset. A wrong guess is counted; the row is
        /// only spent by an actual password change.
        /// </summary>
        private async Task<(PasswordResetCode? row, string? error)> CheckAsync(string? email, string? code)
        {
            email = (email ?? string.Empty).Trim().ToLowerInvariant();
            code = (code ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(email) || code.Length != 6 || !code.All(char.IsDigit))
                return (null, "Enter the six-digit code from the email.");

            var user = await _userManager.FindByEmailAsync(email)
                       ?? await _userManager.FindByNameAsync(email);
            if (user?.UserName == null)
                return (null, "That code is not valid.");

            var row = await NewestCodeAsync(user.UserName.ToLowerInvariant());
            var now = DateTime.UtcNow;

            if (row == null || !row.IsUsable(now))
                return (null, "That code has expired. Ask for a new one.");

            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(row.CodeHash), Encoding.UTF8.GetBytes(Hash(code))))
            {
                row.Attempts += 1;
                await _context.SaveChangesAsync();

                var left = PasswordResetCode.MaxAttempts - row.Attempts;
                return (null, left > 0
                    ? $"That code is not right. {left} attempt{(left == 1 ? "" : "s")} left."
                    : "Too many wrong attempts. Ask for a new code.");
            }

            return (row, null);
        }

        public class EmailRequest
        {
            public string? Email { get; set; }
        }

        public class CodeRequest
        {
            public string? Email { get; set; }
            public string? Code { get; set; }
        }

        public class ResetRequest
        {
            public string? Email { get; set; }
            public string? Code { get; set; }
            public string? NewPassword { get; set; }
        }
    }
}
