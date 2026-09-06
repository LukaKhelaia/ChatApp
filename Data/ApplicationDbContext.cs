using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ChatApp.Models;

namespace ChatApp.Data
{
    // IDataProtectionKeyContext puts the key ring in Postgres. Without it the
    // keys live in the container's filesystem, which is new on every start, so
    // every deploy or wake-from-sleep would invalidate every auth cookie and
    // signed out is what the whole world would look like.
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<Message> Messages { get; set; }
        public DbSet<Group> Groups { get; set; }
        public DbSet<UserGroup> UserGroups { get; set; }
        public DbSet<GroupMessage> GroupMessages { get; set; }
        public DbSet<GroupChatDeletion> GroupChatDeletions { get; set; }
        public DbSet<Friendship> Friendships { get; set; }
        public DbSet<Attachment> Attachments { get; set; }
        public DbSet<GroupRead> GroupReads { get; set; }
        public DbSet<Block> Blocks { get; set; }
        public DbSet<Reaction> Reactions { get; set; }
        public DbSet<GroupMessageDeletion> GroupMessageDeletions { get; set; }
        public DbSet<PasswordResetCode> PasswordResetCodes { get; set; }
        public DbSet<EmailChangeRequest> EmailChangeRequests { get; set; }
        public DbSet<GroupImage> GroupImages { get; set; }

        /// <summary>The Data Protection key ring. Framework-owned; nothing here reads it.</summary>
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Deliberately no foreign key from Message/GroupMessage to
            // Attachment: the column is a plain nullable int. A real FK would
            // need cascade rules on a table whose rows are only ever created
            // moments before the message that points at them, and the download
            // endpoint already validates the link both ways.
            builder.Entity<Attachment>(entity =>
            {
                entity.HasIndex(a => a.UploadedBy);
            });

            builder.Entity<Message>(entity =>
            {
                entity.HasIndex(m => m.AttachmentId);

                // "everything unread in this conversation" is the hot query
                // behind read receipts.
                entity.HasIndex(m => new { m.Receiver, m.User, m.ReadAt });
            });

            builder.Entity<GroupMessage>(entity =>
            {
                entity.HasIndex(m => m.AttachmentId);
            });

            builder.Entity<GroupRead>(entity =>
            {
                // One pointer per member per group. The unique index is what
                // makes the upsert in ChatHub.MarkGroupRead safe against two
                // tabs reporting at the same moment.
                entity.HasIndex(r => new { r.GroupId, r.UserName }).IsUnique();
            });

            builder.Entity<Reaction>(entity =>
            {
                // One reaction per person per message - the upsert in
                // ReactionsController relies on this.
                entity.HasIndex(r => new { r.Scope, r.MessageId, r.UserName }).IsUnique();

                // and "give me the reactions on these messages"
                entity.HasIndex(r => new { r.Scope, r.MessageId });
            });

            builder.Entity<GroupMessageDeletion>(entity =>
            {
                entity.HasIndex(d => new { d.GroupMessageId, d.UserName }).IsUnique();
                entity.HasIndex(d => d.UserName);
            });

            builder.Entity<PasswordResetCode>(entity =>
            {
                // "the newest code for this person" is the only lookup there
                // is, and it runs on every attempt.
                entity.HasIndex(c => new { c.UserName, c.CreatedAt });
            });

            builder.Entity<EmailChangeRequest>(entity =>
            {
                entity.HasIndex(c => new { c.UserName, c.CreatedAt });
            });

            builder.Entity<Block>(entity =>
            {
                // One row per direction, and never two of the same.
                entity.HasIndex(b => new { b.BlockerUserName, b.BlockedUserName }).IsUnique();

                // "who has blocked me" is the filter user search runs on every
                // keystroke, so index the blocked side on its own too.
                entity.HasIndex(b => b.BlockedUserName);
            });

            builder.Entity<Friendship>(entity =>
            {
                // One row per ordered pair. The controller checks both
                // directions before inserting, so this index is the safety net
                // against a double insert from two concurrent requests.
                entity.HasIndex(f => new { f.RequesterUserName, f.AddresseeUserName })
                      .IsUnique();

                // "my friends" and "my pending requests" each filter on one
                // side at a time, so index both sides separately as well.
                entity.HasIndex(f => f.AddresseeUserName);
                entity.HasIndex(f => f.RequesterUserName);
            });
        }
    }
}
