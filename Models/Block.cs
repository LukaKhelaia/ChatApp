using System;

namespace ChatApp.Models
{
    /// <summary>
    /// One user blocking another. Directional: "A blocked B" is a different row
    /// from "B blocked A", and either row is enough to stop messages passing
    /// between them.
    ///
    /// Blocking deliberately does NOT touch the friendship. Unblocking is then
    /// a single click that restores everything, instead of leaving one of them
    /// to send a fresh friend request.
    /// </summary>
    public class Block
    {
        public int Id { get; set; }

        /// <summary>Lower-cased user name of the person who did the blocking.</summary>
        public string BlockerUserName { get; set; } = string.Empty;

        /// <summary>Lower-cased user name of the person who was blocked.</summary>
        public string BlockedUserName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
