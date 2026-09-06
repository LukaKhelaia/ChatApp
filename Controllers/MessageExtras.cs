using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ChatApp.Data;
using ChatApp.Models;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Controllers
{
    /// <summary>
    /// The two things every message payload now carries besides its own text:
    /// the reactions on it, and - if it is a reply - a small quote of the
    /// message it answers.
    ///
    /// Both are needed by the private history, the group history and both hub
    /// broadcasts. Building them in one place is what stops the four payloads
    /// drifting into four slightly different shapes, which is exactly what
    /// happened with attachments before AttachmentPayload existed.
    /// </summary>
    public static class MessageExtras
    {
        /// <summary>A quote is a glance, not a copy. Longer text is cut here.</summary>
        public const int SnippetLength = 140;

        private static string Snip(string? text)
        {
            text = (text ?? string.Empty).Trim();
            return text.Length <= SnippetLength ? text : text[..SnippetLength] + "…";
        }

        /// <summary>
        /// Reactions on the given messages, keyed by message id. Users who have
        /// since been deleted simply fall back to their user name.
        /// </summary>
        public static async Task<Dictionary<int, List<object>>> ReactionsAsync(
            ApplicationDbContext ctx, ReactionScope scope, IEnumerable<int> messageIds)
        {
            var ids = messageIds.Distinct().ToList();
            var empty = new Dictionary<int, List<object>>();
            if (ids.Count == 0) return empty;

            var rows = await ctx.Reactions
                .Where(r => r.Scope == scope && ids.Contains(r.MessageId))
                .OrderBy(r => r.CreatedAt)
                .ToListAsync();

            if (rows.Count == 0) return empty;

            var names = rows.Select(r => r.UserName).Distinct().ToList();
            var nicknames = await ctx.Users
                .Where(u => u.UserName != null && names.Contains(u.UserName.ToLower()))
                .ToDictionaryAsync(
                    u => u.UserName!.ToLowerInvariant(),
                    u => u.Nickname ?? string.Empty);

            var byMessage = new Dictionary<int, List<object>>();
            foreach (var r in rows)
            {
                if (!byMessage.TryGetValue(r.MessageId, out var list))
                {
                    list = new List<object>();
                    byMessage[r.MessageId] = list;
                }

                nicknames.TryGetValue(r.UserName, out var nickname);
                list.Add(ReactionPayload(r.UserName, string.IsNullOrWhiteSpace(nickname) ? r.UserName : nickname, r.Emoji));
            }

            return byMessage;
        }

        /// <summary>The shape the client renders a single reaction from.</summary>
        public static object ReactionPayload(string userName, string nickname, string emoji) => new
        {
            userName,
            nickname,
            emoji
        };

        /// <summary>
        /// Quotes for the given private messages, keyed by the id of the
        /// message doing the replying.
        ///
        /// Note this deliberately ignores DeletedBySender / DeletedByReceiver:
        /// once someone has quoted a message, the quote is part of the reply's
        /// meaning. Hiding your own copy of the original should not blank out a
        /// sentence in somebody else's conversation.
        /// </summary>
        public static async Task<Dictionary<int, object>> PrivateRepliesAsync(
            ApplicationDbContext ctx, IEnumerable<int> targetIds)
        {
            var ids = targetIds.Distinct().ToList();
            var result = new Dictionary<int, object>();
            if (ids.Count == 0) return result;

            var targets = await ctx.Messages
                .Where(m => ids.Contains(m.Id))
                .Select(m => new { m.Id, m.User, m.Text, m.AttachmentId })
                .ToListAsync();

            if (targets.Count == 0) return result;

            var extras = await DescribeAsync(ctx,
                targets.Select(t => t.User),
                targets.Where(t => t.AttachmentId.HasValue).Select(t => t.AttachmentId!.Value));

            foreach (var t in targets)
                result[t.Id] = Quote(t.Id, t.User, t.Text, t.AttachmentId, extras);

            return result;
        }

        /// <summary>The group-message twin of <see cref="PrivateRepliesAsync"/>.</summary>
        public static async Task<Dictionary<int, object>> GroupRepliesAsync(
            ApplicationDbContext ctx, IEnumerable<int> targetIds)
        {
            var ids = targetIds.Distinct().ToList();
            var result = new Dictionary<int, object>();
            if (ids.Count == 0) return result;

            var targets = await ctx.GroupMessages
                .Where(m => ids.Contains(m.Id))
                .Select(m => new { m.Id, User = m.Sender, m.Text, m.AttachmentId })
                .ToListAsync();

            if (targets.Count == 0) return result;

            var extras = await DescribeAsync(ctx,
                targets.Select(t => t.User),
                targets.Where(t => t.AttachmentId.HasValue).Select(t => t.AttachmentId!.Value));

            foreach (var t in targets)
                result[t.Id] = Quote(t.Id, t.User, t.Text, t.AttachmentId, extras);

            return result;
        }

        /// <summary>
        /// One private quote, for the hub's broadcast. Returns null when the id
        /// is null or points at a message that is not part of this
        /// conversation - a reply may only quote something both people can see.
        /// </summary>
        public static async Task<object?> PrivateReplyAsync(
            ApplicationDbContext ctx, int? targetId, string userA, string userB)
        {
            if (targetId is not int id) return null;

            var belongs = await ctx.Messages.AnyAsync(m => m.Id == id &&
                ((m.User.ToLower() == userA && m.Receiver.ToLower() == userB) ||
                 (m.User.ToLower() == userB && m.Receiver.ToLower() == userA)));
            if (!belongs) return null;

            var quotes = await PrivateRepliesAsync(ctx, new[] { id });
            return quotes.TryGetValue(id, out var quote) ? quote : null;
        }

        /// <summary>One group quote, scoped to the group it was sent in.</summary>
        public static async Task<object?> GroupReplyAsync(
            ApplicationDbContext ctx, int? targetId, int groupId)
        {
            if (targetId is not int id) return null;

            var belongs = await ctx.GroupMessages.AnyAsync(m => m.Id == id && m.GroupId == groupId);
            if (!belongs) return null;

            var quotes = await GroupRepliesAsync(ctx, new[] { id });
            return quotes.TryGetValue(id, out var quote) ? quote : null;
        }

        private sealed class Describe
        {
            public Dictionary<string, string> Nicknames { get; init; } = new();
            public Dictionary<int, (string FileName, bool IsImage)> Attachments { get; init; } = new();
        }

        private static async Task<Describe> DescribeAsync(
            ApplicationDbContext ctx, IEnumerable<string> userNames, IEnumerable<int> attachmentIds)
        {
            var names = userNames.Select(n => n.ToLowerInvariant()).Distinct().ToList();
            var nicknames = await ctx.Users
                .Where(u => u.UserName != null && names.Contains(u.UserName.ToLower()))
                .ToDictionaryAsync(
                    u => u.UserName!.ToLowerInvariant(),
                    u => u.Nickname ?? string.Empty);

            // Built by hand rather than with a conditional expression: the two
            // branches would be tuple types differing only in element names,
            // which is more argument than it is worth having with the compiler.
            var attachments = new Dictionary<int, (string FileName, bool IsImage)>();
            var ids = attachmentIds.Distinct().ToList();

            if (ids.Count > 0)
            {
                var rows = await ctx.Attachments
                    .Where(a => ids.Contains(a.Id))
                    .Select(a => new { a.Id, a.FileName, a.ContentType })
                    .ToListAsync();

                foreach (var a in rows)
                    attachments[a.Id] = (a.FileName, AttachmentsController.IsImage(a.ContentType));
            }

            return new Describe { Nicknames = nicknames, Attachments = attachments };
        }

        private static object Quote(int id, string user, string text, int? attachmentId, Describe extras)
        {
            extras.Nicknames.TryGetValue(user.ToLowerInvariant(), out var nickname);

            var kind = "text";
            var snippet = Snip(text);

            if (attachmentId is int aid && extras.Attachments.TryGetValue(aid, out var file))
            {
                kind = file.IsImage ? "image" : "file";
                // An attachment sent with a caption quotes the caption; without
                // one, the file name is the only thing worth showing.
                if (snippet.Length == 0) snippet = file.IsImage ? "Photo" : file.FileName;
            }

            return new
            {
                id,
                userName = user,
                nickname = string.IsNullOrWhiteSpace(nickname) ? user : nickname,
                text = snippet,
                kind
            };
        }
    }
}
