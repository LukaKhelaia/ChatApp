using System;
using Microsoft.AspNetCore.Identity;

namespace ChatApp.Models
{
    public class ApplicationUser : IdentityUser
    {
        public required string Nickname { get; set; }
        public string? AvatarUrl { get; set; }

        /// <summary>
        /// Whether an arriving message makes a sound. On the account rather
        /// than in the browser so it holds wherever they sign in.
        /// </summary>
        public bool SoundEnabled { get; set; } = true;

        /// <summary>
        /// When this account's last connection went away. Null while they are
        /// connected, and null for an account that has never been online.
        ///
        /// Presence itself is not stored - it is whether the hub is currently
        /// holding a connection, which cannot survive a restart and should not
        /// try to. This column exists only so "last seen 20 minutes ago" still
        /// means something after the process recycles.
        /// </summary>
        public DateTime? LastSeen { get; set; }
    }
}
