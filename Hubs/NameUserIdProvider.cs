using Microsoft.AspNetCore.SignalR;

namespace ChatApp.Hubs
{
    /// <summary>
    /// Makes SignalR address users by their user name (the email, in this app)
    /// instead of the Identity GUID, so Clients.User("a@b.com") works.
    ///
    /// Everything is lower-cased here and at every call site, because the
    /// browser sends lower-cased addresses while Identity stores the original
    /// casing. Without normalising, targeted messages are silently dropped for
    /// anyone who registered with a capital letter in their email.
    /// </summary>
    public class NameUserIdProvider : IUserIdProvider
    {
        public string? GetUserId(HubConnectionContext connection)
        {
            return connection.User?.Identity?.Name?.Trim().ToLowerInvariant();
        }
    }
}
