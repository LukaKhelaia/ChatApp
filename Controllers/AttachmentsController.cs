using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.Data;
using ChatApp.Services;
using ChatApp.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Controllers
{
    /// <summary>
    /// Upload and download for chat attachments. Bytes live in Postgres so they
    /// survive a redeploy - Render wipes the container disk on every deploy,
    /// which is why group images vanish.
    /// </summary>
    [Route("api/attachments")]
    [ApiController]
    [Authorize]
    public class AttachmentsController : ControllerBase
    {
        public const int MaxBytes = 10 * 1024 * 1024;   // 10 MB

        // Extension -> the content type WE will serve it back as. The browser's
        // own Content-Type header is ignored: it is attacker-controlled, and
        // echoing it back would let someone upload HTML as "photo.png" and then
        // have it rendered as a page on our own origin.
        private static readonly Dictionary<string, string> AllowedTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // photos (no .svg - SVG can carry script)
                [".jpg"] = "image/jpeg",
                [".jpeg"] = "image/jpeg",
                [".png"] = "image/png",
                [".gif"] = "image/gif",
                [".webp"] = "image/webp",

                // documents
                [".pdf"] = "application/pdf",
                [".doc"] = "application/msword",
                [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                [".xls"] = "application/vnd.ms-excel",
                [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                [".ppt"] = "application/vnd.ms-powerpoint",
                [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                [".txt"] = "text/plain",
                [".csv"] = "text/csv",
                [".zip"] = "application/zip"
            };

        private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/png", "image/gif", "image/webp"
        };

        private readonly ApplicationDbContext _context;
        private readonly ILogger<AttachmentsController> _logger;

        public AttachmentsController(ApplicationDbContext context, ILogger<AttachmentsController> logger)
        {
            _context = context;
            _logger = logger;
        }

        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        public static bool IsImage(string contentType) => ImageTypes.Contains(contentType);

        /// <summary>
        /// Metadata for a set of attachment ids, keyed by id, in the shape the
        /// client renders from. Used by the two history endpoints. Note the
        /// projection: Data is never selected, so listing a conversation costs
        /// the same as it did before attachments existed.
        /// </summary>
        public static async Task<Dictionary<int, object>> LookupAsync(
            ApplicationDbContext db, IEnumerable<int> ids)
        {
            var wanted = ids.Distinct().ToList();
            if (wanted.Count == 0) return new Dictionary<int, object>();

            var rows = await db.Attachments
                .Where(a => wanted.Contains(a.Id))
                .Select(a => new { a.Id, a.FileName, a.ContentType, a.Size })
                .ToListAsync();

            return rows.ToDictionary(a => a.Id, a => (object)new
            {
                id = a.Id,
                fileName = a.FileName,
                contentType = a.ContentType,
                size = a.Size,
                isImage = IsImage(a.ContentType)
            });
        }

        [HttpPost("upload")]
        [RequestSizeLimit(MaxBytes + (512 * 1024))]   // a little headroom for the multipart envelope
        public async Task<IActionResult> Upload(IFormFile? file)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            // Attachments live in the database, so an upload is much more
            // expensive than a message and gets a much tighter allowance: five
            // in hand, then one every five seconds. Refused before the body is
            // read, so a flood does not cost 10 MB of buffering each time.
            if (!RateLimiter.Allow("upload:" + currentUser, 0.2, 5))
                return StatusCode(429, new { error = "Too many uploads just now. Give it a moment." });

            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file was uploaded." });

            if (file.Length > MaxBytes)
                return BadRequest(new { error = "That file is larger than 10 MB." });

            // Path.GetFileName drops any directory part a crafted upload might
            // carry ("../../x"), and the name is only ever used as a label and
            // as a download name, never to open anything on disk.
            var originalName = Path.GetFileName(file.FileName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(originalName))
                originalName = "attachment";
            if (originalName.Length > 200)
                originalName = originalName[^200..];

            var extension = Path.GetExtension(originalName);
            if (string.IsNullOrEmpty(extension) || !AllowedTypes.TryGetValue(extension, out var contentType))
                return BadRequest(new { error = "That file type isn't allowed. Photos, PDFs, Office documents, text files and zips only." });

            byte[] data;
            using (var stream = new MemoryStream())
            {
                await file.CopyToAsync(stream);
                data = stream.ToArray();
            }

            // Re-check after reading: Length is only what the client claimed.
            if (data.Length == 0 || data.Length > MaxBytes)
                return BadRequest(new { error = "That file is larger than 10 MB." });

            var attachment = new Attachment
            {
                FileName = originalName,
                ContentType = contentType,
                Size = data.Length,
                Data = data,
                UploadedBy = currentUser,
                CreatedAt = DateTime.UtcNow
            };

            _context.Attachments.Add(attachment);
            await _context.SaveChangesAsync();

            _logger.LogInformation("User {User} uploaded attachment {Id} ({Size} bytes)",
                currentUser, attachment.Id, attachment.Size);

            return Ok(new
            {
                id = attachment.Id,
                fileName = attachment.FileName,
                contentType = attachment.ContentType,
                size = attachment.Size,
                isImage = IsImage(attachment.ContentType)
            });
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Download(int id)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            var attachment = await _context.Attachments.FirstOrDefaultAsync(a => a.Id == id);
            if (attachment == null) return NotFound();

            if (!await CanSeeAsync(id, attachment.UploadedBy, currentUser))
            {
                _logger.LogWarning("User {User} was refused attachment {Id}", currentUser, id);
                return Forbid();
            }

            // Stops a browser from second-guessing the type we declare.
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            Response.Headers["Cache-Control"] = "private, max-age=3600";

            // Images render inline so <img src> works; everything else is served
            // as a download, so nothing can execute in the page's origin.
            if (IsImage(attachment.ContentType))
                return File(attachment.Data, attachment.ContentType);

            return File(attachment.Data, "application/octet-stream", attachment.FileName);
        }

        /// <summary>
        /// The caller may see an attachment if they uploaded it (the window
        /// between upload and send), if it is on a private message they are a
        /// party to and have not deleted, or if it is on a message in a group
        /// they belong to.
        /// </summary>
        private async Task<bool> CanSeeAsync(int attachmentId, string uploadedBy, string currentUser)
        {
            if (string.Equals(uploadedBy, currentUser, StringComparison.OrdinalIgnoreCase))
                return true;

            var onMyPrivateMessage = await _context.Messages.AnyAsync(m =>
                m.AttachmentId == attachmentId &&
                ((m.User.ToLower() == currentUser && !m.DeletedBySender) ||
                 (m.Receiver.ToLower() == currentUser && !m.DeletedByReceiver)));

            if (onMyPrivateMessage) return true;

            var myGroupIds = _context.UserGroups
                .Where(ug => ug.UserName.ToLower() == currentUser)
                .Select(ug => ug.GroupId);

            return await _context.GroupMessages.AnyAsync(m =>
                m.AttachmentId == attachmentId && myGroupIds.Contains(m.GroupId));
        }
    }
}
