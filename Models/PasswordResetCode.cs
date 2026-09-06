using System;

namespace ChatApp.Models
{
    /// <summary>
    /// A six-digit code emailed to someone who has forgotten their password.
    ///
    /// The code itself is never stored - only a SHA-256 hash of it, the same
    /// way a password would be. It is short-lived and single-use, and it stops
    /// accepting guesses after a handful of wrong ones, because six digits is
    /// a million possibilities and an unlimited form would chew through that.
    /// </summary>
    public class PasswordResetCode
    {
        /// <summary>How long a code is good for.</summary>
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

        /// <summary>Wrong guesses allowed before the code is burned.</summary>
        public const int MaxAttempts = 5;

        /// <summary>How long before another code can be requested.</summary>
        public static readonly TimeSpan RequestCooldown = TimeSpan.FromSeconds(60);

        public int Id { get; set; }

        /// <summary>Lower-cased user name, the same key everything else uses.</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>Hex SHA-256 of the six digits.</summary>
        public string CodeHash { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.Add(Lifetime);

        /// <summary>Set once the password has actually been changed.</summary>
        public DateTime? UsedAt { get; set; }

        public int Attempts { get; set; }

        public bool IsUsable(DateTime now) =>
            UsedAt == null && Attempts < MaxAttempts && now < ExpiresAt;
    }
}
