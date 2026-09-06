using System;

namespace ChatApp.Models
{
    public enum ReactionScope
    {
        Private = 0,
        Group = 1
    }

    /// <summary>
    /// One emoji reaction from one person on one message.
    ///
    /// A single table covers both message kinds, discriminated by Scope, since
    /// the shape and every query are identical - two near-identical tables
    /// would mean two of every controller method for no benefit.
    ///
    /// The unique index on (Scope, MessageId, UserName) is the rule "one
    /// reaction per person per message": reacting again replaces it, reacting
    /// with the same emoji removes it.
    /// </summary>
    public class Reaction
    {
        public int Id { get; set; }

        public ReactionScope Scope { get; set; }

        /// <summary>Message.Id or GroupMessage.Id, depending on Scope.</summary>
        public int MessageId { get; set; }

        /// <summary>Lower-cased user name, same normalisation as everywhere else.</summary>
        public string UserName { get; set; } = string.Empty;

        public string Emoji { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
