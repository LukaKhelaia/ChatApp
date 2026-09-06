using System;

namespace ChatApp.Models
{
    /// <summary>
    /// A group's picture, kept in the database rather than on disk.
    ///
    /// The obvious place for an upload is wwwroot, and that is where this used
    /// to go. It works exactly until the first redeploy: the container is
    /// rebuilt from the image and every file written at runtime goes with it,
    /// so groups silently lost their pictures on each push. Two megabytes is
    /// the hard cap on an upload, the rows are read once and then cached by the
    /// browser for a year, so the size this adds to the database is not a
    /// concern at this scale.
    /// </summary>
    public class GroupImage
    {
        public int Id { get; set; }

        /// <summary>Taken from the file extension, never from the client's header.</summary>
        public string ContentType { get; set; } = "application/octet-stream";

        public byte[] Data { get; set; } = Array.Empty<byte>();

        public string UploadedBy { get; set; } = string.Empty;

        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    }
}
