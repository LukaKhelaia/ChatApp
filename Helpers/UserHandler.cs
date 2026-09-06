using System.Collections.Concurrent;

namespace ChatApp.Helpers
{
    public static class UserHandler
    {
        // Use thread-safe dictionary
        public static ConcurrentDictionary<string, string> ConnectedUsers { get; } = new ConcurrentDictionary<string, string>();
    }
}
