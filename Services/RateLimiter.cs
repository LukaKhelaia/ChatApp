using System;
using System.Collections.Concurrent;

namespace ChatApp.Services
{
    /// <summary>
    /// A per-caller token bucket, held in memory.
    ///
    /// Every account gets a bucket that refills at a steady rate up to a
    /// ceiling. Spending a token is allowed while there is one; a burst is
    /// fine, a sustained flood is not. That shape matters here - a real person
    /// firing off five short messages in three seconds is normal, and a fixed
    /// "one per second" would punish them for it.
    ///
    /// This is deliberately not the framework's rate limiter. That one is
    /// middleware, and middleware sees the single long-lived POST that opens a
    /// SignalR connection, not the hundreds of hub calls that then travel
    /// inside it - which is exactly where the flooding risk lives. So the same
    /// mechanism is used for both the hub and the couple of HTTP endpoints
    /// worth protecting, rather than two half-solutions.
    ///
    /// In memory, therefore per process: two instances behind a load balancer
    /// would each allow the full rate. For one server that is the right
    /// trade - a shared counter would mean a Redis round trip on every
    /// message, which is a real cost to solve a problem this app does not
    /// have yet.
    /// </summary>
    public static class RateLimiter
    {
        private sealed class Bucket
        {
            public double Tokens;
            public DateTime UpdatedUtc;
        }

        private static readonly ConcurrentDictionary<string, Bucket> Buckets = new();

        // Housekeeping. Without it the dictionary keeps a row per account that
        // has ever sent anything, for the life of the process.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
        private static DateTime _lastSweepUtc = DateTime.UtcNow;

        /// <summary>
        /// Takes one token from <paramref name="key"/>'s bucket. Returns false
        /// when the bucket is empty, which the caller should treat as "not
        /// now" rather than as an error.
        /// </summary>
        /// <param name="perSecond">How fast the bucket refills.</param>
        /// <param name="burst">How many can be spent at once from full.</param>
        public static bool Allow(string key, double perSecond, double burst)
        {
            if (string.IsNullOrEmpty(key)) return true;

            var now = DateTime.UtcNow;
            Sweep(now);

            var bucket = Buckets.GetOrAdd(key, _ => new Bucket { Tokens = burst, UpdatedUtc = now });

            // One bucket can be touched by several tabs of the same account at
            // once, and the read-modify-write below is not atomic.
            lock (bucket)
            {
                var elapsed = (now - bucket.UpdatedUtc).TotalSeconds;
                if (elapsed > 0)
                {
                    bucket.Tokens = Math.Min(burst, bucket.Tokens + elapsed * perSecond);
                    bucket.UpdatedUtc = now;
                }

                if (bucket.Tokens < 1) return false;

                bucket.Tokens -= 1;
                return true;
            }
        }

        /// <summary>
        /// Drops buckets nobody has touched recently. A bucket that has been
        /// idle that long has refilled to the brim anyway, so forgetting it
        /// gives the account nothing it did not already have.
        /// </summary>
        private static void Sweep(DateTime now)
        {
            if (now - _lastSweepUtc < StaleAfter) return;
            _lastSweepUtc = now;

            foreach (var pair in Buckets)
            {
                if (now - pair.Value.UpdatedUtc > StaleAfter)
                    Buckets.TryRemove(pair.Key, out _);
            }
        }
    }
}
