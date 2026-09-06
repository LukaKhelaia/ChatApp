using System;

namespace ChatApp.Models
{
    /// <summary>
    /// One member hiding one group message for themselves.
    ///
    /// Private messages already carry DeletedBySender / DeletedByReceiver
    /// because there are only ever two parties. A group has any number, so the
    /// same idea needs a row per (message, member) instead of a column.
    /// </summary>
    public class GroupMessageDeletion
    {
        public int Id { get; set; }

        public int GroupMessageId { get; set; }

        public string UserName { get; set; } = string.Empty;

        public DateTime DeletedAt { get; set; } = DateTime.UtcNow;
    }
}
