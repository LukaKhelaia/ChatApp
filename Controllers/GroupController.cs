using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using ChatApp.Models;
using ChatApp.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using System;
using ChatApp.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Identity;

namespace ChatApp.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize] // every endpoint here reads or mutates group membership
    public class GroupController : ControllerBase
    {
        private const long MaxImageBytes = 2 * 1024 * 1024; // 2 MB

        private static readonly HashSet<string> AllowedImageExtensions =
            new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp" };

        private readonly ApplicationDbContext _context;
        private readonly IHubContext<ChatHub> _hubContext;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<GroupController> _logger;

        public GroupController(
            ApplicationDbContext context,
            IHubContext<ChatHub> hubContext,
            UserManager<ApplicationUser> userManager,
            ILogger<GroupController> logger)
        {
            _context = context;
            _hubContext = hubContext;
            _userManager = userManager;
            _logger = logger;
        }

        // The SignalR user id is the lower-cased user name (see NameUserIdProvider),
        // so normalise the same way on every comparison and notification.
        private string? CurrentUser => User.Identity?.Name?.Trim().ToLowerInvariant();

        private static string GroupName(int groupId) => $"group-{groupId}";

        private Task<bool> IsMemberAsync(int groupId, string userName) =>
            _context.UserGroups.AnyAsync(ug => ug.GroupId == groupId && ug.UserName.ToLower() == userName);

        /// <summary>
        /// The one who made the group. Their power does not live in a column
        /// that another admin could flip, which is what makes "admins cannot
        /// touch each other, but the creator can" enforceable.
        /// </summary>
        private async Task<bool> IsCreatorAsync(int groupId, string userName)
        {
            var creator = await _context.Groups
                .Where(g => g.Id == groupId)
                .Select(g => g.CreatorUserName)
                .FirstOrDefaultAsync();

            return creator != null && string.Equals(creator, userName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Creator, or a member the creator has appointed.</summary>
        private async Task<bool> IsAdminAsync(int groupId, string userName)
        {
            if (await IsCreatorAsync(groupId, userName)) return true;

            return await _context.UserGroups
                .AnyAsync(ug => ug.GroupId == groupId && ug.UserName.ToLower() == userName && ug.IsAdmin);
        }

        [HttpPost("create")]
        public async Task<IActionResult> CreateGroup([FromForm] CreateGroupRequest request)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Name))
                return BadRequest("Group name cannot be empty.");

            if (request.Name.Trim().Length > 100)
                return BadRequest("Group name is too long.");

            // Validate the membership list BEFORE anything is written. The
            // image is a couple of megabytes in a table nothing ever sweeps, so
            // a request that is going to be rejected must not get as far as
            // storing one - the old code saved the picture first and left it
            // behind on every "not your friend".
            var requestedNames = new HashSet<string>(
                (request.Usernames ?? new List<string>())
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Select(u => u.Trim().ToLowerInvariant()));

            requestedNames.Add(currentUser);

            // Resolve to real accounts (and pick up the exact casing stored in the DB).
            var dbUsers = await _context.Users
                .Where(u => u.UserName != null && requestedNames.Contains(u.UserName.ToLower()))
                .Select(u => u.UserName!)
                .ToListAsync();

            if (dbUsers.Count != requestedNames.Count)
            {
                var found = dbUsers.Select(u => u.ToLowerInvariant()).ToHashSet();
                var missing = requestedNames.Where(u => !found.Contains(u));
                return BadRequest($"Users not found: {string.Join(", ", missing)}");
            }

            // You can only put your friends in a group.
            foreach (var invitee in requestedNames.Where(n => n != currentUser))
            {
                if (!await FriendsController.AreFriendsAsync(_context, currentUser, invitee))
                    return BadRequest($"You can only add friends to a group: {invitee} is not your friend.");
            }

            var group = new Group
            {
                Name = request.Name.Trim(),
                CreatedAt = DateTime.UtcNow,
                CreatorUserName = currentUser
            };

            // Save the group image, if one was uploaded.
            if (request.Image != null && request.Image.Length > 0)
            {
                var imageUrl = await SaveGroupImageAsync(request.Image);
                if (imageUrl == null)
                    return BadRequest("Invalid image. Use a png, jpg, gif or webp file under 2 MB.");

                group.ImageUrl = imageUrl;
            }

            _context.Groups.Add(group);
            await _context.SaveChangesAsync(); // generates group.Id

            foreach (var username in dbUsers)
            {
                _context.UserGroups.Add(new UserGroup
                {
                    UserName = username,
                    GroupId = group.Id
                });
            }

            await _context.SaveChangesAsync();

            var notifyTargets = dbUsers.Select(u => u.ToLowerInvariant()).ToList();

            await _hubContext.Clients.Users(notifyTargets).SendAsync("ChatListUpdated");
            await _hubContext.Clients.Users(notifyTargets).SendAsync("JoinGroupSignalR", group.Id);

            return Ok(new { group.Id, group.Name, group.ImageUrl, group.CreatorUserName });
        }

        /// <summary>Newest page when the client asks for no cursor.</summary>
        public const int DefaultPageSize = 40;
        private const int MaxPageSize = 100;

        /// <summary>
        /// One page of a group's messages, newest last. Same cursor scheme as
        /// the private history: <paramref name="before"/> is a message id, so a
        /// message arriving mid-scroll cannot shift the page under the reader.
        /// </summary>
        [HttpGet("messages")]
        public async Task<IActionResult> GetGroupMessages([FromQuery] int groupId, [FromQuery] int? before = null, [FromQuery] int? take = null)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            if (!await IsMemberAsync(groupId, currentUser))
                return Forbid();

            var pageSize = Math.Clamp(take ?? DefaultPageSize, 1, MaxPageSize);

            var deleted = await _context.GroupChatDeletions
                .FirstOrDefaultAsync(d => d.GroupId == groupId && d.UserName.ToLower() == currentUser);
            var deletedAt = deleted?.DeletedAt;

            // Messages this member has removed for themselves. A group has more
            // than two parties, so this is a row per (message, member) rather
            // than the pair of flags a private message carries. It is an EXISTS
            // against the message rather than a list fetched up front, because
            // the list grows with the group and only one page of it is ever
            // relevant.
            var visible = _context.GroupMessages
                .Where(m => m.GroupId == groupId
                         && (deletedAt == null || m.SentAt > deletedAt)
                         && !_context.GroupMessageDeletions
                                .Any(d => d.UserName == currentUser && d.GroupMessageId == m.Id));

            if (before.HasValue)
                visible = visible.Where(m => m.Id < before.Value);

            // One row past the page size answers "is there more" for free.
            var messages = await visible
                .OrderByDescending(m => m.Id)
                .Take(pageSize + 1)
                .ToListAsync();

            var hasMore = messages.Count > pageSize;
            if (hasMore) messages.RemoveAt(messages.Count - 1);

            messages.Reverse();   // oldest first, the order the pane renders in

            var senderUsernames = messages
                .Select(m => m.Sender.ToLower())
                .Distinct()
                .ToList();

            var userDetails = await _context.Users
                .Where(u => u.UserName != null && senderUsernames.Contains(u.UserName.ToLower()))
                .ToDictionaryAsync(
                    u => u.UserName!.ToLowerInvariant(),
                    u => new
                    {
                        AvatarUrl = u.AvatarUrl ?? string.Empty,
                        Nickname = u.Nickname ?? string.Empty
                    });

            // Metadata only - the bytes stay in the database until someone
            // actually opens the attachment.
            var attachments = await AttachmentsController.LookupAsync(
                _context, messages.Where(m => m.AttachmentId.HasValue).Select(m => m.AttachmentId!.Value));

            var reactions = await MessageExtras.ReactionsAsync(
                _context, ReactionScope.Group, messages.Select(m => m.Id));

            var replies = await MessageExtras.GroupRepliesAsync(
                _context, messages.Where(m => m.ReplyToMessageId.HasValue)
                                  .Select(m => m.ReplyToMessageId!.Value));

            var result = messages.Select(m =>
            {
                userDetails.TryGetValue(m.Sender.ToLowerInvariant(), out var details);

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
                    m.Sender,
                    Text = m.Text,
                    Timestamp = m.SentAt,
                    m.GroupId,
                    Attachment = attachment,
                    ReplyTo = replyTo,
                    Reactions = messageReactions ?? new List<object>(),
                    AvatarUrl = string.IsNullOrEmpty(details?.AvatarUrl) ? "/images/default-avatar.png" : details!.AvatarUrl,
                    Nickname = string.IsNullOrEmpty(details?.Nickname) ? m.Sender : details!.Nickname
                };
            });

            return Ok(new
            {
                messages = result,
                hasMore,
                oldestId = messages.Count > 0 ? messages[0].Id : (int?)null
            });
        }

        /// <summary>
        /// Hide one group message for the caller only. Everyone else's copy is
        /// untouched, so nothing is broadcast to the group.
        /// </summary>
        [HttpPost("delete-message")]
        public async Task<IActionResult> DeleteGroupMessage([FromBody] DeleteGroupMessageRequest request)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();
            if (request == null || request.MessageId <= 0) return BadRequest("Invalid message.");

            var message = await _context.GroupMessages
                .Where(m => m.Id == request.MessageId)
                .Select(m => new { m.Id, m.GroupId })
                .FirstOrDefaultAsync();

            if (message == null) return NotFound();

            if (!await IsMemberAsync(message.GroupId, currentUser))
                return Forbid();

            var already = await _context.GroupMessageDeletions
                .AnyAsync(d => d.GroupMessageId == message.Id && d.UserName == currentUser);

            if (!already)
            {
                _context.GroupMessageDeletions.Add(new GroupMessageDeletion
                {
                    GroupMessageId = message.Id,
                    UserName = currentUser,
                    DeletedAt = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
            }

            await _hubContext.Clients.User(currentUser).SendAsync("MessageDeleted", true, message.Id);

            return Ok();
        }

        public class DeleteGroupMessageRequest
        {
            public int MessageId { get; set; }
        }

        /// <summary>
        /// How far every other member has read in this group, so the client can
        /// place their avatar against the right message. Own row is excluded -
        /// nobody needs their own face on their own messages.
        /// </summary>
        [HttpGet("reads")]
        public async Task<IActionResult> GetGroupReads([FromQuery] int groupId)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            if (!await IsMemberAsync(groupId, currentUser))
                return Forbid();

            var reads = await _context.GroupReads
                .Where(r => r.GroupId == groupId && r.UserName != currentUser)
                .ToListAsync();

            if (reads.Count == 0) return Ok(Array.Empty<object>());

            var names = reads.Select(r => r.UserName).ToList();
            var users = await _context.Users
                .Where(u => u.UserName != null && names.Contains(u.UserName.ToLower()))
                .ToDictionaryAsync(
                    u => u.UserName!.ToLowerInvariant(),
                    u => new
                    {
                        AvatarUrl = u.AvatarUrl ?? string.Empty,
                        Nickname = u.Nickname ?? string.Empty
                    });

            var result = reads.Select(r =>
            {
                users.TryGetValue(r.UserName, out var info);
                return new
                {
                    userName = r.UserName,
                    lastReadMessageId = r.LastReadMessageId,
                    nickname = string.IsNullOrEmpty(info?.Nickname) ? r.UserName : info!.Nickname,
                    avatarUrl = string.IsNullOrEmpty(info?.AvatarUrl) ? "/images/default-avatar.png" : info!.AvatarUrl
                };
            });

            return Ok(result);
        }

        /// <summary>
        /// Groups the caller is in whose name matches. Deliberately limited to
        /// their own groups: a group you are not a member of is not something
        /// you could open from here, so listing it would only leak its name.
        /// </summary>
        [HttpGet("search")]
        public async Task<IActionResult> SearchGroups([FromQuery] string term)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            if (string.IsNullOrWhiteSpace(term) || term.Trim().Length < 2)
                return Ok(Array.Empty<object>());

            var needle = term.Trim().ToLowerInvariant();

            var hidden = await _context.GroupChatDeletions
                .Where(d => d.UserName.ToLower() == currentUser && d.IsHidden)
                .Select(d => d.GroupId)
                .ToListAsync();

            var groups = await _context.UserGroups
                .Where(ug => ug.UserName.ToLower() == currentUser)
                .Include(ug => ug.Group)
                .Where(ug => ug.Group.Name.ToLower().Contains(needle))
                .OrderBy(ug => ug.Group.Name)
                .Take(20)
                .Select(ug => new
                {
                    groupId = ug.GroupId,
                    name = ug.Group.Name ?? "Unnamed Group",
                    imageUrl = string.IsNullOrEmpty(ug.Group.ImageUrl) ? "/images/default-group.png" : ug.Group.ImageUrl,
                    hidden = hidden.Contains(ug.GroupId)
                })
                .ToListAsync();

            return Ok(groups);
        }

        [HttpGet("group-members")]
        public async Task<IActionResult> GetGroupMembers([FromQuery] int groupId)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            if (!await IsMemberAsync(groupId, currentUser))
                return Forbid();

            var creator = (await _context.Groups
                .Where(g => g.Id == groupId)
                .Select(g => g.CreatorUserName)
                .FirstOrDefaultAsync() ?? string.Empty).ToLowerInvariant();

            var rows = await (from ug in _context.UserGroups
                              join u in _context.Users on ug.UserName equals u.UserName
                              where ug.GroupId == groupId
                              select new
                              {
                                  userName = u.UserName ?? "Unknown",
                                  email = u.Email ?? string.Empty,
                                  nickname = u.Nickname ?? string.Empty,
                                  avatarUrl = string.IsNullOrEmpty(u.AvatarUrl) ? "/images/default-avatar.png" : u.AvatarUrl,
                                  appointed = ug.IsAdmin
                              }).ToListAsync();

            var members = rows.Select(m =>
            {
                var isCreator = string.Equals(m.userName, creator, StringComparison.OrdinalIgnoreCase);
                return new
                {
                    m.userName,
                    m.email,
                    m.nickname,
                    m.avatarUrl,
                    isCreator,
                    isAdmin = isCreator || m.appointed
                };
            });

            return Ok(members);
        }

        [HttpPost("addMember")]
        public async Task<IActionResult> AddMember([FromForm] int GroupId, [FromForm] string UserName)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(UserName)) return BadRequest("User name is required.");

            // Adding people is an admin's job now. The check has to be here and
            // not only on the button: this endpoint is reachable directly.
            if (!await IsAdminAsync(GroupId, currentUser))
                return Forbid();

            var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == GroupId);
            if (group == null)
                return NotFound("Group not found");

            var user = await _userManager.FindByNameAsync(UserName);
            if (user == null || user.UserName == null)
                return NotFound("User not found");

            var normalized = user.UserName.ToLowerInvariant();

            if (!await FriendsController.AreFriendsAsync(_context, currentUser, normalized))
                return BadRequest("You can only add your friends to a group.");

            if (await IsMemberAsync(GroupId, normalized))
                return BadRequest("User already in group");

            _context.UserGroups.Add(new UserGroup
            {
                GroupId = GroupId,
                UserName = user.UserName
            });

            await _context.SaveChangesAsync();

            var allUsernames = await _context.UserGroups
                .Where(ug => ug.GroupId == GroupId)
                .Select(ug => ug.UserName.ToLower())
                .Distinct()
                .ToListAsync();

            await _hubContext.Clients.Users(allUsernames).SendAsync("ChatListUpdated");
            await _hubContext.Clients.User(normalized).SendAsync("JoinGroupSignalR", GroupId);

            return Ok("User added");
        }

        [HttpGet("info")]
        public async Task<IActionResult> GetGroupInfo([FromQuery] int groupId)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            if (!await IsMemberAsync(groupId, currentUser))
                return Forbid();

            var group = await _context.Groups
                .Where(g => g.Id == groupId)
                .Select(g => new
                {
                    g.Id,
                    g.Name,
                    g.ImageUrl,
                    CreatorUserName = g.CreatorUserName
                })
                .FirstOrDefaultAsync();

            if (group == null) return NotFound("Group not found");

            // The client hides Add members and the kick buttons for ordinary
            // members, and it needs to know which one it is looking at before
            // the member list has been opened.
            var amCreator = string.Equals(group.CreatorUserName, currentUser, StringComparison.OrdinalIgnoreCase);

            return Ok(new
            {
                group.Id,
                group.Name,
                group.ImageUrl,
                group.CreatorUserName,
                amCreator,
                amAdmin = amCreator || await IsAdminAsync(groupId, currentUser)
            });
        }

        /// <summary>
        /// Appoint or remove an admin. The creator only: admins can remove
        /// ordinary members, but who holds that power stays the decision of
        /// the person whose group it is.
        /// </summary>
        [HttpPost("set-admin")]
        public async Task<IActionResult> SetAdmin([FromBody] SetAdminRequest request)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();
            if (request == null || string.IsNullOrWhiteSpace(request.UserName))
                return BadRequest("User name is required.");

            if (!await IsCreatorAsync(request.GroupId, currentUser))
                return Forbid();

            var target = request.UserName.Trim().ToLowerInvariant();
            if (string.Equals(target, currentUser, StringComparison.OrdinalIgnoreCase))
                return BadRequest("You are the group's creator already.");

            var row = await _context.UserGroups
                .FirstOrDefaultAsync(ug => ug.GroupId == request.GroupId && ug.UserName.ToLower() == target);

            if (row == null) return NotFound("User is not a member of this group.");

            if (row.IsAdmin != request.IsAdmin)
            {
                row.IsAdmin = request.IsAdmin;
                await _context.SaveChangesAsync();
                _logger.LogInformation("{Creator} set admin={Value} for {Target} in group {GroupId}",
                    currentUser, request.IsAdmin, target, request.GroupId);
            }

            // Everyone in the group: the member list may be open on any of
            // their screens, and the person themselves needs their buttons
            // to appear without a reload.
            await _hubContext.Clients.Group(GroupName(request.GroupId))
                .SendAsync("GroupAdminChanged", request.GroupId, target, request.IsAdmin);

            return Ok(new { userName = target, isAdmin = request.IsAdmin });
        }

        public class SetAdminRequest
        {
            public int GroupId { get; set; }
            public string? UserName { get; set; }
            public bool IsAdmin { get; set; }
        }

        [HttpPost("kick")]
        public async Task<IActionResult> KickUser([FromForm] int groupId, [FromForm] string userNameToKick)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();
            if (string.IsNullOrWhiteSpace(userNameToKick)) return BadRequest("User name is required.");

            var group = await _context.Groups.FirstOrDefaultAsync(g => g.Id == groupId);
            if (group == null)
                return NotFound("Group not found");

            var callerIsCreator = string.Equals(group.CreatorUserName, currentUser, StringComparison.OrdinalIgnoreCase);

            if (!callerIsCreator && !await IsAdminAsync(groupId, currentUser))
                return Forbid();

            var target = userNameToKick.Trim().ToLowerInvariant();

            if (string.Equals(currentUser, target, StringComparison.OrdinalIgnoreCase))
                return BadRequest("You cannot kick yourself - leave the group instead.");

            // Nobody removes the person whose group it is.
            if (string.Equals(group.CreatorUserName, target, StringComparison.OrdinalIgnoreCase))
                return Forbid();

            var userGroup = await _context.UserGroups
                .FirstOrDefaultAsync(ug => ug.GroupId == groupId && ug.UserName.ToLower() == target);

            if (userGroup == null)
                return NotFound("User is not a member of this group");

            // Admins are equals: one cannot remove another. Only the creator,
            // who appointed them in the first place, can.
            if (userGroup.IsAdmin && !callerIsCreator)
                return Forbid();

            _context.UserGroups.Remove(userGroup);
            await _context.SaveChangesAsync();

            await _hubContext.Clients.User(target).SendAsync("KickedFromGroup", groupId);

            // The SignalR group is named "group-{id}" - the previous version sent
            // to the bare id, so nobody ever received this.
            await _hubContext.Clients.Group(GroupName(groupId))
                .SendAsync("MemberKicked", target, groupId);

            return Ok("User kicked from group");
        }

        [HttpPost("leave")]
        public async Task<IActionResult> LeaveGroup([FromBody] LeaveGroupRequest request)
        {
            // The caller can only remove themselves. The old version took the
            // user name from the request body, which let any caller remove any
            // user from any group.
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();
            if (request == null || request.GroupId == 0) return BadRequest("Invalid request");

            var userGroup = await _context.UserGroups
                .FirstOrDefaultAsync(ug => ug.UserName.ToLower() == currentUser && ug.GroupId == request.GroupId);

            if (userGroup == null)
                return NotFound("User is not in the group");

            _context.UserGroups.Remove(userGroup);

            // Drop any "hidden chat" bookkeeping so re-joining starts clean.
            var deletions = await _context.GroupChatDeletions
                .Where(d => d.GroupId == request.GroupId && d.UserName.ToLower() == currentUser)
                .ToListAsync();
            _context.GroupChatDeletions.RemoveRange(deletions);

            await _context.SaveChangesAsync();

            return Ok("Left the group successfully");
        }

        public class LeaveGroupRequest
        {
            public string Email { get; set; } = string.Empty; // kept for client compatibility; ignored
            public int GroupId { get; set; }
        }

        [HttpDelete("messages/delete")]
        public async Task<IActionResult> DeleteGroupChat([FromQuery] int groupId)
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            if (!await IsMemberAsync(groupId, currentUser))
                return Forbid();

            var existing = await _context.GroupChatDeletions
                .FirstOrDefaultAsync(d => d.GroupId == groupId && d.UserName.ToLower() == currentUser);

            if (existing != null)
            {
                existing.DeletedAt = DateTime.UtcNow;
                existing.IsHidden = true;
            }
            else
            {
                _context.GroupChatDeletions.Add(new GroupChatDeletion
                {
                    GroupId = groupId,
                    UserName = currentUser,
                    DeletedAt = DateTime.UtcNow,
                    IsHidden = true
                });
            }

            await _context.SaveChangesAsync();

            return Ok();
        }

        [HttpGet("list")]
        public async Task<IActionResult> GetUserGroups()
        {
            var currentUser = CurrentUser;
            if (string.IsNullOrEmpty(currentUser)) return Unauthorized();

            var hiddenGroupIds = await _context.GroupChatDeletions
                .Where(d => d.UserName.ToLower() == currentUser && d.IsHidden)
                .Select(d => d.GroupId)
                .ToListAsync();

            var groups = await _context.UserGroups
                .Where(ug => ug.UserName.ToLower() == currentUser && !hiddenGroupIds.Contains(ug.GroupId))
                .Include(ug => ug.Group)
                .OrderByDescending(ug => ug.Group.CreatedAt)
                .Select(ug => new
                {
                    GroupId = ug.GroupId,
                    Name = ug.Group.Name,
                    ImageUrl = ug.Group.ImageUrl
                })
                .ToListAsync();

            return Ok(groups);
        }

        /// <summary>
        /// Serves a group picture out of the database.
        ///
        /// The route is absolute so the URL stays "/group-images/{id}" - the
        /// same shape the old on-disk files used, which is why rows written
        /// before the move still resolve: static files are matched first, and
        /// only a URL with no matching file on disk falls through to here.
        ///
        /// A row's bytes never change, so it can be cached hard. Private, not
        /// public: an intermediary has no business holding a copy.
        /// </summary>
        [AllowAnonymous]
        [HttpGet("/group-images/{id:int}")]
        public async Task<IActionResult> Image(int id)
        {
            var image = await _context.GroupImages
                .AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == id);

            if (image == null) return NotFound();

            Response.Headers.CacheControl = "private, max-age=31536000, immutable";
            return File(image.Data, image.ContentType);
        }

        /// <summary>
        /// Validates an uploaded group image and stores it in the database.
        /// Returns the public URL, or null if the file was rejected.
        /// </summary>
        private async Task<string?> SaveGroupImageAsync(IFormFile image)
        {
            if (image.Length <= 0 || image.Length > MaxImageBytes) return null;

            var extension = Path.GetExtension(image.FileName);
            if (string.IsNullOrEmpty(extension) || !AllowedImageExtensions.Contains(extension))
                return null;

            if (!(image.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ?? false))
                return null;

            // The content type is derived from the extension we just validated,
            // never echoed back from the request: a client-supplied header is
            // exactly how an "image" ends up being served as something the
            // browser will execute.
            var contentType = ContentTypeFor(extension);

            using var buffer = new MemoryStream();
            await image.CopyToAsync(buffer);

            var row = new GroupImage
            {
                ContentType = contentType,
                Data = buffer.ToArray(),
                UploadedBy = CurrentUser ?? string.Empty,
                UploadedAt = DateTime.UtcNow
            };

            _context.GroupImages.Add(row);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Stored group image {Id} ({Bytes} bytes)", row.Id, row.Data.Length);

            return $"/group-images/{row.Id}";
        }

        private static string ContentTypeFor(string extension) => extension.ToLowerInvariant() switch
        {
            ".png"  => "image/png",
            ".gif"  => "image/gif",
            ".webp" => "image/webp",
            _       => "image/jpeg"   // .jpg / .jpeg - the only ones left in AllowedImageExtensions
        };

        public class CreateGroupRequest
        {
            public string Name { get; set; } = "";
            public List<string> Usernames { get; set; } = new();
            public IFormFile? Image { get; set; }
        }
    }
}
