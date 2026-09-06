using System.Threading.Tasks;
using ChatApp.Controllers;
using ChatApp.Models;
using Xunit;

namespace ChatApp.Tests
{
    /// <summary>
    /// Replying quotes the message you answered, and the client sends the id of
    /// that message. An id is trivial to change by hand, so the server has to
    /// check the quoted message really belongs to THIS conversation - otherwise
    /// replying with someone else's id is a way to read a stranger's messages,
    /// one quote at a time.
    ///
    /// The guard returns null rather than throwing, which quietly turns a
    /// forged id into an ordinary message with no quote.
    /// </summary>
    public class ReplyScopeTests
    {
        private static Message Between(string from, string to, string text) =>
            new Message { User = from, Receiver = to, Text = text };

        [Fact]
        public async Task A_message_from_this_conversation_can_be_quoted()
        {
            using var db = TestDb.Fresh();
            var mine = Between("gela@x.com", "luka@x.com", "the original");
            db.Messages.Add(mine);
            await db.SaveChangesAsync();

            var quote = await MessageExtras.PrivateReplyAsync(db, mine.Id, "gela@x.com", "luka@x.com");
            Assert.NotNull(quote);
        }

        [Fact]
        public async Task The_other_participant_can_quote_it_too()
        {
            using var db = TestDb.Fresh();
            var mine = Between("gela@x.com", "luka@x.com", "the original");
            db.Messages.Add(mine);
            await db.SaveChangesAsync();

            // same conversation, asked from the other side
            var quote = await MessageExtras.PrivateReplyAsync(db, mine.Id, "luka@x.com", "gela@x.com");
            Assert.NotNull(quote);
        }

        [Fact]
        public async Task A_message_from_someone_elses_conversation_cannot()
        {
            using var db = TestDb.Fresh();
            var theirs = Between("ana@x.com", "beka@x.com", "a private thing");
            db.Messages.Add(theirs);
            await db.SaveChangesAsync();

            // gela guesses the id and tries to quote it into her own chat
            var quote = await MessageExtras.PrivateReplyAsync(db, theirs.Id, "gela@x.com", "luka@x.com");
            Assert.Null(quote);
        }

        [Fact]
        public async Task Half_a_pair_is_not_enough()
        {
            using var db = TestDb.Fresh();
            // luka really did send this - but to someone else
            var elsewhere = Between("luka@x.com", "ana@x.com", "not for gela");
            db.Messages.Add(elsewhere);
            await db.SaveChangesAsync();

            var quote = await MessageExtras.PrivateReplyAsync(db, elsewhere.Id, "gela@x.com", "luka@x.com");
            Assert.Null(quote);
        }

        [Fact]
        public async Task An_id_that_does_not_exist_is_simply_no_quote()
        {
            using var db = TestDb.Fresh();
            Assert.Null(await MessageExtras.PrivateReplyAsync(db, 999999, "gela@x.com", "luka@x.com"));
        }

        [Fact]
        public async Task No_reply_id_means_no_quote()
        {
            using var db = TestDb.Fresh();
            Assert.Null(await MessageExtras.PrivateReplyAsync(db, null, "gela@x.com", "luka@x.com"));
        }
    }
}
