using System;

namespace ChatApp.Models
{
    public class GroupMessage
    {
        public int Id { get; set; }
        public int GroupId { get; set; }
        public required string Sender { get; set; }
        public required string Text { get; set; }

        /// <summary>See Message.AttachmentId - same idea for group messages.</summary>
        public int? AttachmentId { get; set; }

        /// <summary>See Message.ReplyToMessageId.</summary>
        public int? ReplyToMessageId { get; set; }

        public DateTime SentAt { get; set; } = DateTime.UtcNow;
    }
}
