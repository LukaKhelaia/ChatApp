using Microsoft.AspNetCore.Mvc;
using ChatApp.Data;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using ChatApp.Controllers;
using ChatApp.Hubs;
using ChatApp.Models;

namespace ChatApp.Controllers.Api
{
    [Route("api/messages")]
    [ApiController]
    [Authorize]
    public class MessagesController : ControllerBase
    {
        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hub;

        public MessagesController(ApplicationDbContext context, IHubContext<ChatHub> hub)
        {
            _context = context;
            _hub = hub;
        }

        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        private Task<Dictionary<int, object>> AttachmentLookupAsync(IEnumerable<int> ids)
            => AttachmentsController.LookupAsync(_context, ids);

        /// <summary>Newest page when the client asks for no cursor.</summary>
        public const int DefaultPageSize = 40;
        private const int MaxPageSize = 100;

        /// <summary>
        /// One page of the conversation, newest last.
        ///
        /// <paramref name="before"/> is a message id, not an offset: the client
        /// asks for "the page before message 812" and gets a stable answer even
        /// though messages keep arriving at the other end. An OFFSET would slide
        /// under it and duplicate or skip a row every time someone sent
        /// something mid-scroll.
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetChatHistory(string withUser, int? before = null, int? take = null)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser) || string.IsNullOrWhiteSpace(withUser))
                return BadRequest("Invalid users.");

            withUser = withUser.Trim().ToLowerInvariant();

            var pageSize = Math.Clamp(take ?? DefaultPageSize, 1, MaxPageSize);

            // Deliberately NOT friends-gated. Unfriending stops you SENDING
            // (see ChatHub.SendPrivateMessage) but the conversation you already
            // had stays readable. The query below only ever returns rows where
            // the caller is one of the two participants, so this exposes
            // nothing that isn't already theirs.

            // Loading the conversation IS delivery: their browser now has the
            // messages. Reading is a separate, stricter step handled by
            // ChatHub.MarkPrivateRead once the window is actually focused.
            //
            // This runs before the page is read, and covers the whole
            // conversation rather than the page - opening a chat with three
            // hundred unread still tells the sender all three hundred landed,
            // which is what it did before paging. It is bounded by what is
            // actually undelivered, not by how long the history is, and once
            // stamped a message never comes back here.
            if (!before.HasValue)
            {
                var now = DateTime.UtcNow;
                var undelivered = await _context.Messages
                    .Where(m => m.User.ToLower() == withUser
                             && m.Receiver.ToLower() == currentUser
                             && !m.DeletedByReceiver
                             && m.DeliveredAt == null)
                    .ToListAsync();

                if (undelivered.Count > 0)
                {
                    foreach (var m in undelivered) m.DeliveredAt = now;
                    await _context.SaveChangesAsync();
                }
            }

            // Messages between the two users, minus the ones this user deleted.
            var conversation = _context.Messages
                .Where(m =>
                    (m.User.ToLower() == currentUser && m.Receiver.ToLower() == withUser && !m.DeletedBySender) ||
                    (m.User.ToLower() == withUser && m.Receiver.ToLower() == currentUser && !m.DeletedByReceiver));

            if (before.HasValue)
                conversation = conversation.Where(m => m.Id < before.Value);

            // Ordered by id, not SentAt: ids are the cursor, and two messages
            // in the same millisecond would otherwise page unpredictably. One
            // row past the page size answers "is there more" without a second
            // COUNT query.
            var messages = await conversation
                .OrderByDescending(m => m.Id)
                .Take(pageSize + 1)
                .ToListAsync();

            var hasMore = messages.Count > pageSize;
            if (hasMore) messages.RemoveAt(messages.Count - 1);

            messages.Reverse();   // oldest first, the order the pane renders in

            var senderNames = messages
                .Select(m => m.User.ToLower())
                .Distinct()
                .ToList();

            var senders = await _context.Users
                .Where(u => u.UserName != null && senderNames.Contains(u.UserName.ToLower()))
                .ToDictionaryAsync(
                    u => u.UserName!.ToLowerInvariant(),
                    u => new
                    {
                        AvatarUrl = u.AvatarUrl ?? string.Empty,
                        Nickname = u.Nickname ?? string.Empty
                    });

            // Attachment metadata for whichever of these messages carry one.
            // Data (the bytes) is deliberately NOT selected here - only the
            // download endpoint ever reads that column, so loading a chat stays
            // as cheap as it was before attachments existed.
            var attachments = await AttachmentLookupAsync(
                messages.Where(m => m.AttachmentId.HasValue).Select(m => m.AttachmentId!.Value));

            // Reactions on these messages, and quotes for the ones that are
            // replies. Two queries for the whole conversation, not two per
            // message.
            var reactions = await MessageExtras.ReactionsAsync(
                _context, ReactionScope.Private, messages.Select(m => m.Id));

            var replies = await MessageExtras.PrivateRepliesAsync(
                _context, messages.Where(m => m.ReplyToMessageId.HasValue)
                                  .Select(m => m.ReplyToMessageId!.Value));

            var result = messages.Select(m =>
            {
                senders.TryGetValue(m.User.ToLowerInvariant(), out var info);

                object? attachment = null;
                if (m.AttachmentId.HasValue)
                    attachments.TryGetValue(m.AttachmentId.Value, out attachment);

                object? replyTo = null;
                if (m.ReplyToMessageId.HasValue)
                    replies.TryGetValue(m.ReplyToMessageId.Value, out replyTo);

                reactions.TryGetValue(m.Id, out var messageReactions);

                return new
                {
                    m.Id,
                    m.User,
                    m.Receiver,
                    m.Text,
                    m.SentAt,
                    m.DeliveredAt,
                    m.ReadAt,
                    Attachment = attachment,
                    ReplyTo = replyTo,
                    Reactions = messageReactions ?? new List<object>(),
                    AvatarUrl = string.IsNullOrEmpty(info?.AvatarUrl) ? "/images/default-avatar.png" : info!.AvatarUrl,
                    Nickname = string.IsNullOrEmpty(info?.Nickname) ? m.User : info!.Nickname
                };
            });

            // An object rather than a bare array now: the client needs to know
            // whether scrolling further back is worth a request.
            return Ok(new
            {
                messages = result,
                hasMore,
                oldestId = messages.Count > 0 ? messages[0].Id : (int?)null
            });
        }

        // Marks messages in a private chat as deleted for the logged-in user
        [HttpPost("delete")]
        public async Task<IActionResult> DeleteChat([FromBody] string withUser)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser) || string.IsNullOrWhiteSpace(withUser))
                return BadRequest("Invalid users.");

            withUser = withUser.Trim().ToLowerInvariant();

            var messages = await _context.Messages
                .Where(m =>
                    (m.User.ToLower() == currentUser && m.Receiver.ToLower() == withUser) ||
                    (m.User.ToLower() == withUser && m.Receiver.ToLower() == currentUser))
                .ToListAsync();

            // Only hide them for the caller - the other side keeps their copy.
            foreach (var msg in messages)
            {
                if (string.Equals(msg.User, currentUser, System.StringComparison.OrdinalIgnoreCase))
                    msg.DeletedBySender = true;
                if (string.Equals(msg.Receiver, currentUser, System.StringComparison.OrdinalIgnoreCase))
                    msg.DeletedByReceiver = true;
            }

            await _context.SaveChangesAsync();

            return Ok();
        }

        /// <summary>
        /// Hide one message for the caller only. The other side keeps their
        /// copy - this is "remove for me", not "unsend", which is why nothing
        /// is broadcast to them.
        /// </summary>
        [HttpPost("delete-message")]
        public async Task<IActionResult> DeleteMessage([FromBody] DeleteMessageRequest request)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();
            if (request == null || request.MessageId <= 0) return BadRequest("Invalid message.");

            var message = await _context.Messages.FirstOrDefaultAsync(m => m.Id == request.MessageId);
            if (message == null) return NotFound();

            var isSender = string.Equals(message.User, currentUser, StringComparison.OrdinalIgnoreCase);
            var isReceiver = string.Equals(message.Receiver, currentUser, StringComparison.OrdinalIgnoreCase);
            if (!isSender && !isReceiver) return Forbid();

            if (isSender) message.DeletedBySender = true;
            if (isReceiver) message.DeletedByReceiver = true;

            await _context.SaveChangesAsync();

            // Their own other tabs, so the message disappears everywhere they
            // are signed in rather than only where they clicked.
            await _hub.Clients.User(currentUser).SendAsync("MessageDeleted", false, message.Id);

            return Ok();
        }

        public class DeleteMessageRequest
        {
            public int MessageId { get; set; }
        }
    }
}
