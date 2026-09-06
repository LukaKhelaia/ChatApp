using System;

namespace ChatApp.Models
{
    /// <summary>
    /// A pending email change: someone signed in has asked to move their
    /// account to a new address, and a code has been sent THERE to prove they
    /// can read it. Same rules as the password-reset code - hashed, expiring,
    /// single-use, with a cap on wrong guesses.
    /// </summary>
    public class EmailChangeRequest
    {
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
        public const int MaxAttempts = 5;
        public static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds(60);

        public int Id { get; set; }

        /// <summary>Who asked, as their user name stands right now.</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>The address they want to move to, lower-cased.</summary>
        public string NewEmail { get; set; } = string.Empty;

        public string CodeHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.Add(Lifetime);

        public DateTime? UsedAt { get; set; }

        public int Attempts { get; set; }

        public bool IsUsable(DateTime now) =>
            UsedAt == null && Attempts < MaxAttempts && now < ExpiresAt;
    }
}
