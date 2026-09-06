using System;

namespace ChatApp.Models
{
    public class Message
    {
        public int Id { get; set; }
        public required string User { get; set; }
        public required string Receiver { get; set; }
        public required string Text { get; set; }

        /// <summary>
        /// Set when this message carries a photo or file. A message may have an
        /// attachment, text, or both - Text is an empty string for an
        /// attachment-only message, never null.
        /// </summary>
        public int? AttachmentId { get; set; }

        /// <summary>
        /// The message this one is a reply to, if any. A plain nullable id
        /// rather than a relationship: the target may since have been
        /// hidden by one side, and the quote should survive that.
        /// </summary>
        public int? ReplyToMessageId { get; set; }

        public DateTime SentAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Set the moment the message reaches the recipient's browser - either
        /// because they had a live connection when it was sent, or because they
        /// loaded the conversation later. Null means it is still only "sent".
        /// </summary>
        public DateTime? DeliveredAt { get; set; }

        /// <summary>
        /// Set when the recipient actually had this conversation open AND the
        /// window focused. Deliberately stricter than DeliveredAt - claiming
        /// "seen" for a message sitting in a background tab would be a lie.
        /// </summary>
        public DateTime? ReadAt { get; set; }

        public bool DeletedBySender { get; set; }
        public bool DeletedByReceiver { get; set; }

    }
}
