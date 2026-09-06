using System;

namespace ChatApp.Models
{
    /// <summary>
    /// How far one member has read in one group: a single pointer per member,
    /// not a row per (message, member).
    ///
    /// A group of 20 with 1000 messages would be 20,000 rows the other way; it
    /// is 20 here. It is also exactly what the UI needs - Messenger shows each
    /// reader's avatar against the last message they have read, which is this
    /// pointer and nothing more.
    /// </summary>
    public class GroupRead
    {
        public int Id { get; set; }

        public int GroupId { get; set; }

        /// <summary>Lower-cased user name, same normalisation as everywhere else.</summary>
        public string UserName { get; set; } = string.Empty;

        /// <summary>Id of the newest GroupMessage this member has read.</summary>
        public int LastReadMessageId { get; set; }

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
