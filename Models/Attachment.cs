using System;

namespace ChatApp.Models
{
    /// <summary>
    /// A file or photo sent in a chat, stored as bytes in Postgres.
    ///
    /// Kept in its own table rather than as columns on Message/GroupMessage for
    /// two reasons: one row serves both kinds of message, and the bytes stay
    /// out of the way of every ordinary message query (EF only loads them when
    /// the Attachments table is queried directly, which is just the download
    /// endpoint).
    ///
    /// ContentType is NOT whatever the browser claimed at upload time - it is
    /// re-derived from the extension against the allow-list in
    /// AttachmentsController, so a .html renamed to .png can't come back out
    /// as text/html and run in the origin.
    /// </summary>
    public class Attachment
    {
        public int Id { get; set; }

        /// <summary>Original file name, stripped of any path.</summary>
        public string FileName { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        /// <summary>Size in bytes. Denormalised so the list can show it without loading Data.</summary>
        public int Size { get; set; }

        public byte[] Data { get; set; } = Array.Empty<byte>();

        /// <summary>Lower-cased user name of the uploader, same normalisation as everywhere else.</summary>
        public string UploadedBy { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
