using System.Threading.Tasks;
using ChatApp.Controllers;
using ChatApp.Models;
using Xunit;

namespace ChatApp.Tests
{
    /// <summary>
    /// A pair gets ONE friendship row, and whoever sent the request is the
    /// Requester. So "are these two friends" has to be asked in both
    /// directions - the bug this pins is the one where a friendship only works
    /// for the person who happened to send the request.
    /// </summary>
    public class FriendshipRulesTests
    {
        [Fact]
        public async Task Accepted_counts_from_the_requester_side()
        {
            using var db = TestDb.Fresh();
            db.Friendships.Add(new Friendship
            {
                RequesterUserName = "gela@x.com",
                AddresseeUserName = "luka@x.com",
                Status = FriendshipStatus.Accepted
            });
            await db.SaveChangesAsync();

            Assert.True(await FriendsController.AreFriendsAsync(db, "gela@x.com", "luka@x.com"));
        }

        [Fact]
        public async Task Accepted_counts_from_the_addressee_side_too()
        {
            using var db = TestDb.Fresh();
            db.Friendships.Add(new Friendship
            {
                RequesterUserName = "gela@x.com",
                AddresseeUserName = "luka@x.com",
                Status = FriendshipStatus.Accepted
            });
            await db.SaveChangesAsync();

            // same row, asked the other way round
            Assert.True(await FriendsController.AreFriendsAsync(db, "luka@x.com", "gela@x.com"));
        }

        [Fact]
        public async Task A_request_nobody_accepted_is_not_a_friendship()
        {
            using var db = TestDb.Fresh();
            db.Friendships.Add(new Friendship
            {
                RequesterUserName = "gela@x.com",
                AddresseeUserName = "luka@x.com",
                Status = FriendshipStatus.Pending
            });
            await db.SaveChangesAsync();

            Assert.False(await FriendsController.AreFriendsAsync(db, "gela@x.com", "luka@x.com"));
        }

        [Fact]
        public async Task A_declined_request_is_not_a_friendship()
        {
            using var db = TestDb.Fresh();
            db.Friendships.Add(new Friendship
            {
                RequesterUserName = "gela@x.com",
                AddresseeUserName = "luka@x.com",
                Status = FriendshipStatus.Declined
            });
            await db.SaveChangesAsync();

            Assert.False(await FriendsController.AreFriendsAsync(db, "gela@x.com", "luka@x.com"));
        }

        [Fact]
        public async Task Strangers_are_not_friends()
        {
            using var db = TestDb.Fresh();
            Assert.False(await FriendsController.AreFriendsAsync(db, "gela@x.com", "nobody@x.com"));
        }
    }
}
