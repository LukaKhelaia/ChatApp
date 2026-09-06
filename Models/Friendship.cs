using System;

namespace ChatApp.Models
{
    public enum FriendshipStatus
    {
        Pending = 0,
        Accepted = 1,
        Declined = 2
    }

    /// <summary>
    /// One row per relationship between two users.
    ///
    /// Both user names are stored lower-cased, the same normalisation the rest
    /// of the app uses (see NameUserIdProvider) - Postgres string comparison is
    /// case sensitive, so mixing casing here would silently break lookups.
    ///
    /// A pair only ever gets ONE row. Whoever sends the request is the
    /// Requester; the other side is the Addressee and is the only one who may
    /// accept it. To test "are these two friends" you therefore have to check
    /// the pair in both directions - see FriendsController.AreFriendsAsync.
    /// </summary>
    public class Friendship
    {
        public int Id { get; set; }

        public string RequesterUserName { get; set; } = string.Empty;

        public string AddresseeUserName { get; set; } = string.Empty;

        public FriendshipStatus Status { get; set; } = FriendshipStatus.Pending;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? RespondedAt { get; set; }
    }
}
