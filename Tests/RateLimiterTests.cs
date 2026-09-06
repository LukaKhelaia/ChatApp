using System;
using System.Threading;
using ChatApp.Services;
using Xunit;

namespace ChatApp.Tests
{
    /// <summary>
    /// The token bucket behind message sends, uploads and friend requests.
    ///
    /// Buckets are static and keyed by string, so every test mints its own key.
    /// Sharing one would make these depend on the order they run in.
    /// </summary>
    public class RateLimiterTests
    {
        private static string FreshKey() => "test:" + Guid.NewGuid();

        [Fact]
        public void Allows_a_full_burst_then_refuses()
        {
            var key = FreshKey();

            // refill is effectively off, so only the burst allowance is in play
            for (var i = 0; i < 5; i++)
                Assert.True(RateLimiter.Allow(key, 0.0001, 5), $"call {i + 1} of the burst was refused");

            Assert.False(RateLimiter.Allow(key, 0.0001, 5));
        }

        [Fact]
        public void One_account_running_out_does_not_affect_another()
        {
            var mine = FreshKey();
            var theirs = FreshKey();

            Assert.True(RateLimiter.Allow(mine, 0.0001, 1));
            Assert.False(RateLimiter.Allow(mine, 0.0001, 1));

            Assert.True(RateLimiter.Allow(theirs, 0.0001, 1));
        }

        [Fact]
        public void Refills_as_time_passes()
        {
            var key = FreshKey();

            // Spend the bucket at a rate that refills far too slowly to matter,
            // so "it is empty" cannot be undone by the clock between two lines.
            // At 1000/second the bucket refills a whole token in a millisecond,
            // and a GC pause between the spend and the check would hand one back
            // and fail this for no reason.
            Assert.True(RateLimiter.Allow(key, 0.0001, 1));
            Assert.False(RateLimiter.Allow(key, 0.0001, 1));

            // Then ask the same bucket at a fast refill: 1000/second means 20ms
            // is worth twenty tokens, far more slack than a loaded machine needs
            // to produce the single one being asked for.
            Thread.Sleep(20);
            Assert.True(RateLimiter.Allow(key, 1000, 1));
        }

        [Fact]
        public void An_empty_key_is_never_limited()
        {
            // callers pass a user name; an anonymous one must not share a bucket
            // with every other anonymous caller
            for (var i = 0; i < 50; i++) Assert.True(RateLimiter.Allow("", 0.0001, 1));
        }
    }
}
