using System.Threading.Tasks;
using ChatApp.Controllers;
using ChatApp.Models;
using Xunit;

namespace ChatApp.Tests
{
    /// <summary>
    /// A block row is directional, but the RULE is not: either side having
    /// blocked the other stops messages both ways. Get that wrong and the
    /// person who did the blocking still receives everything.
    /// </summary>
    public class BlockRulesTests
    {
        [Fact]
        public async Task My_block_stops_the_pair()
        {
            using var db = TestDb.Fresh();
            db.Blocks.Add(new Block { BlockerUserName = "gela@x.com", BlockedUserName = "luka@x.com" });
            await db.SaveChangesAsync();

            Assert.True(await BlocksController.IsBlockedEitherWayAsync(db, "gela@x.com", "luka@x.com"));
        }

        [Fact]
        public async Task Their_block_stops_the_pair_as_well()
        {
            using var db = TestDb.Fresh();
            db.Blocks.Add(new Block { BlockerUserName = "luka@x.com", BlockedUserName = "gela@x.com" });
            await db.SaveChangesAsync();

            // the row runs the other way; the answer must not
            Assert.True(await BlocksController.IsBlockedEitherWayAsync(db, "gela@x.com", "luka@x.com"));
        }

        [Fact]
        public async Task An_unrelated_block_leaves_us_alone()
        {
            using var db = TestDb.Fresh();
            db.Blocks.Add(new Block { BlockerUserName = "someone@x.com", BlockedUserName = "else@x.com" });
            await db.SaveChangesAsync();

            Assert.False(await BlocksController.IsBlockedEitherWayAsync(db, "gela@x.com", "luka@x.com"));
        }

        [Fact]
        public async Task Nobody_blocked_anybody()
        {
            using var db = TestDb.Fresh();
            Assert.False(await BlocksController.IsBlockedEitherWayAsync(db, "gela@x.com", "luka@x.com"));
        }
    }
}
