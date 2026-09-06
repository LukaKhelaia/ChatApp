# ChatApp

A real-time chat application: private conversations and group chats, delivered
over a persistent WebSocket connection rather than by polling. Built with
ASP.NET Core 8 on the server and vanilla ES modules in the browser — no
front-end framework.

**Live demo:** _(add the Render URL here after the first deploy)_

---

## What it does

**Messaging**

- One-to-one chats and group chats, both live over SignalR.
- Replies that quote the message they answer, scoped correctly — a reply in a
  group can only point at a message in that group.
- Emoji reactions, one per person per message.
- File and image attachments, stored in the database and served through an
  endpoint that checks the requester is party to the message.
- Links in messages are detected and made clickable, with the text escaped
  first — the message body is never injected as raw HTML.
- Delete for me, on both private and group messages, without touching what
  anyone else sees.
- Message history is paged by id cursor, forty at a time, and older pages load
  when you scroll to the top with the scroll position preserved across the
  prepend.
- Day separators, delivery and read receipts, and typing indicators.

**People**

- Friend requests: send, accept, decline, remove. You can only add friends to a
  group.
- Blocking, enforced on the server for every path a message could take.
- Online / offline presence with a "last seen" fallback, pushed to everyone who
  cares when it changes.
- User search that hides accounts that have blocked you.

**Groups**

- Create a group with a name, a picture and a member list.
- A creator and appointed admins. Admins can add and kick members; they cannot
  touch each other, and only the creator can appoint them.
- Per-member unread counts, computed in SQL rather than by loading messages.

**Accounts**

- Register, sign in, sign out (ASP.NET Core Identity).
- Change nickname, avatar, email and password.
- Changing an email requires a code sent to the new address.
- Forgotten-password reset by six-digit emailed code — hashed at rest, expires
  in 15 minutes, single-use, five attempts, one request a minute, and the
  endpoints never reveal whether an address has an account.

---

## Stack

| Layer | What and why |
|---|---|
| Server | ASP.NET Core 8 — MVC controllers for the JSON API, Razor Pages for the Identity area |
| Real time | SignalR over WebSockets, with a custom `IUserIdProvider` so clients are addressed by email rather than by Identity GUID |
| Data | Entity Framework Core 8 + Npgsql on PostgreSQL, code-first, migrations applied at startup |
| Auth | ASP.NET Core Identity, cookie based; the Data Protection key ring is persisted to Postgres so sessions survive a container restart |
| Front end | Vanilla JavaScript as ES modules, Bootstrap 5 for the grid and base components, hand-written CSS for everything that is actually visible |
| Email | Brevo HTTP API, with an SMTP fallback and a log-only mode so a fresh clone works with no configuration |
| Container | Multi-stage Dockerfile — SDK image builds, runtime image ships |
| Tests | xUnit against an in-memory provider |

---

## Things in here worth a look

These are the parts that took thought rather than typing.

**Presence is pushed, not polled.** `ChatHub` tracks connections per user and
announces a transition only when the count goes from zero to one or one to
zero — opening a second tab does not tell everybody you just came online.
`LastSeen` on the user row covers the offline case.

**Unread counts are computed in the database.** `GroupUnreadAsync` uses
correlated subqueries and a `GroupBy` so counting happens in Postgres. The
obvious version — load the messages, count them in C# — is what it replaced.

**History pages by id cursor, not `OFFSET`.** `OFFSET 200` makes the database
walk 200 rows it then discards, and it skips or repeats rows when something is
inserted mid-scroll. `WHERE Id < @before ORDER BY Id DESC LIMIT 40` does
neither.

**Rate limiting.** `Services/RateLimiter.cs` is a per-key token bucket with a
sweep for stale keys, applied to the hub methods a client could otherwise call
in a loop.

**Uploads live in the database.** Attachments and group pictures are `bytea`
columns, not files in `wwwroot`. A container's disk is rebuilt on every deploy,
so anything written to it at runtime is gone the next time you push.

**The front end is 20 ES modules with one entry point.** `chat-boot.js` imports
the side-effect modules in order and starts the connection. Cross-file mutable
state lives in one object in `chat-state.js`, because a module's exported
binding is read-only at the importing end — there is exactly one place a value
like "which chat is open" can be written.

**The message row is built in one place.** All four bubble variants — sent,
received, group, with or without an avatar — come out of `buildMessageRow` in
`chat-render.js`, so the four rendering paths cannot drift apart.

---

## Running it locally

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) and a
PostgreSQL database. A free [Neon](https://neon.tech) project is the easiest.

```bash
git clone https://github.com/draftfile203/ChatApp.git
cd ChatApp
```

Create `appsettings.Development.json` (it is git-ignored):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=YOUR-HOST;Database=neondb;Username=YOUR-USER;Password=YOUR-PASSWORD;SSL Mode=Require;Trust Server Certificate=true"
  }
}
```

Then:

```bash
dotnet run
```

The migrations run on startup, so the tables create themselves on first boot.
Email is optional — with nothing configured, password-reset codes are written
to the console and the flow still works end to end.

Run the tests with:

```bash
dotnet test
```

## Deploying

See [DEPLOYMENT.md](DEPLOYMENT.md) — Docker image, Render blueprint, Neon
database, and the environment variables each one needs.

---

## Layout

```
Controllers/      JSON API - messages, groups, friends, blocks, reactions,
                  attachments, account settings, password reset
Hubs/ChatHub.cs   SignalR: send, typing, read receipts, presence, membership
Models/           EF entities
Data/             DbContext and the design-time factory
Migrations/       Hand-written EF migrations
Services/         Email sending, rate limiting
Views/            Razor views - Chat.cshtml is the chat page's markup
Areas/Identity/   Login, register, profile and settings pages
wwwroot/js/chat/  The chat client, 20 ES modules
wwwroot/css/      site.css (shell, forms, dialogs) and chat.css (the chat)
Tests/            xUnit - friendship rules, block rules, reply scoping,
                  the rate limiter
```
