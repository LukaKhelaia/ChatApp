using System;
using ChatApp.Data;
using Microsoft.EntityFrameworkCore;

namespace ChatApp.Tests
{
    /// <summary>
    /// A throwaway in-memory database per test.
    ///
    /// The provider is deliberately NOT Postgres. These tests are about the
    /// rules - who counts as a friend, whose block stops a message, which reply
    /// ids a conversation will accept - and those are decisions in C#, not in
    /// SQL. Running them against a real database would mean every developer
    /// needs one before the suite goes green, which is how a test suite stops
    /// being run.
    ///
    /// The trade is worth naming: the in-memory provider does not enforce
    /// relational constraints and does not translate queries the way Npgsql
    /// does, so it cannot catch "this LINQ will not translate". Only a real
    /// database can. That is a gap, not an oversight.
    /// </summary>
    internal static class TestDb
    {
        public static ApplicationDbContext Fresh()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase("test-" + Guid.NewGuid())
                .Options;

            return new ApplicationDbContext(options);
        }
    }
}
