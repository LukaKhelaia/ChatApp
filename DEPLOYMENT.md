# Deploying ChatApp

The app ships as a container. `Dockerfile` builds it, `render.yaml` describes
the service, and the database lives on Neon rather than on the host — so the
data outlives any redeploy, and the free Postgres that Render deletes after
30 days never enters the picture.

Nothing below costs money.

---

## 1. The database (Neon)

Neon's free tier does not expire. A project takes about a minute to create.

1. <https://neon.tech> → sign in with GitHub → **New project**.
2. Region: **AWS eu-central-1 (Frankfurt)** — same region as the web service,
   so the round trip to the database is a couple of milliseconds rather than a
   couple of hundred.
3. Copy the **pooled** connection string it shows you. It looks like:

   ```
   postgresql://neondb_owner:PASSWORD@ep-something-pooler.c-2.eu-central-1.aws.neon.tech/neondb?sslmode=require
   ```

   The `-pooler` host is the one to use. The direct host runs out of
   connections quickly on the free plan.

There is nothing to create inside the database. The app runs its own
migrations at startup, so the tables appear on the first boot.

## 2. Email (Brevo) — optional but do it

Skipping this does not break anything: the password-reset code is written to
the Render log instead, and the flow still works end to end. It just means
nobody but you can reset a password.

1. <https://brevo.com> → sign up → **Senders, Domains & Dedicated IPs** →
   **Add a sender**. Use an address you can open; Brevo emails it a
   confirmation link that has to be clicked.
2. **SMTP & API** → **API Keys** → **Generate a new API key**. Copy it now —
   Brevo shows it once.

Free tier is 300 emails a day, which is far more than a portfolio project
sends.

## 3. The web service (Render)

1. <https://render.com> → sign in with GitHub.
2. **New +** → **Blueprint**.
3. Pick the `ChatApp` repository. Render reads `render.yaml` and offers one
   service called `chatapp`.
4. It then asks for the three values marked `sync: false` in the blueprint:

   | Key              | Value                                                  |
   |------------------|--------------------------------------------------------|
   | `DATABASE_URL`   | the Neon pooled connection string from step 1           |
   | `BREVO_API_KEY`  | the Brevo API key from step 2 (leave blank to skip mail)|
   | `EMAIL_FROM`     | the sender address you verified with Brevo              |

5. **Apply**. The first build takes roughly five minutes — it is a full
   `dotnet publish` inside the container.

The URL is `https://chatapp-<something>.onrender.com`. `autoDeploy: true` is
set, so every push to `main` from then on redeploys by itself.

### Checking it came up

The service log should show, in this order:

```
Database migration completed.
Email provider: Brevo
Now listening on: http://[::]:10000
```

`https://<your-url>/healthz` answers `ok` without a login. That is the
endpoint Render polls; `/` is not usable for it, because `/` redirects to the
login page and a redirect does not read as healthy.

---

## What the free plan actually means

The service sleeps after **15 minutes** with no traffic. The next request
wakes it, which takes **about a minute** — the visitor sees a blank tab for
that long, and any chat window left open loses its SignalR connection when the
sleep happens.

For a link on a CV that matters, and Render's Starter plan ($7/month) removes
it. Two things soften it in the meantime:

- Logins survive the sleep. The Data Protection key ring is stored in Postgres
  (`DataProtectionKeys`), so the cookie signed before the nap is still valid
  after it. Without that, every wake would sign everybody out.
- Group pictures survive redeploys, for the same reason — they are rows in
  `GroupImages`, not files in the container.

If you want to keep it awake, point a free uptime pinger (UptimeRobot,
cron-job.org) at `/healthz` every 10 minutes. Note that Render's free plan has
a monthly instance-hour budget, and pinging spends it.

---

## Configuration reference

The connection string is resolved in this order, first non-empty wins
(`ResolveConnectionString` in `Program.cs`):

1. `ConnectionStrings:DefaultConnection` — appsettings, user-secrets, or the
   `ConnectionStrings__DefaultConnection` environment variable.
2. `CONNECTION_STRING` — a plain Npgsql connection string.
3. `DATABASE_URL` — a `postgresql://` URL, which is what Neon, Render, Heroku
   and Fly all hand out. Converted to Npgsql form in code.

`appsettings.json` is in source control and therefore holds **no** credentials —
its `DefaultConnection` is deliberately empty. Filling it in would silently win
over `DATABASE_URL` and the deployed app would talk to the wrong database.
Local values belong in `appsettings.Development.json`, which is git-ignored.

### Password reset

Which route the six-digit code takes is decided by configuration, checked in
this order:

1. **Brevo HTTP API** — `BREVO_API_KEY` (or `Email:BrevoApiKey`) plus
   `EMAIL_FROM`. This is the one to use on Render: it is an ordinary HTTPS
   call, and hosts commonly block the outbound SMTP ports.
2. **SMTP** — `SMTP_HOST`, `SMTP_PORT`, `SMTP_USER`, `SMTP_PASSWORD`,
   `EMAIL_FROM`. For Gmail that is `smtp.gmail.com`, port 587, and an *app
   password* (Google account → Security → 2-Step Verification → App
   passwords), not the account password. Good for running locally.
3. **Neither** — the code goes to the application log and the flow still
   works. That is the default on a fresh clone, so nothing has to be set up
   before the feature can be tried.

The codes are stored as SHA-256 hashes, expire after 15 minutes, are
single-use, allow five wrong guesses, and can only be requested once a minute
per account. The endpoints never reveal whether an address has an account.

---

## When something is wrong

**Build fails on `dotnet restore`** — usually a package version that does not
exist. The log names it.

**`No database connection string found`** — `DATABASE_URL` is not set on the
service. Environment → check it is there and starts with `postgresql://`.

**`Database migration failed` in the log, but the app still starts** — the app is
running against a database it could not migrate, so most pages will throw. The
usual cause is a connection string pointing at a database another version of
the app already owns. Check the host in the string.

**Port binding errors** — `PORT` is injected by Render at runtime and expanded
by the shell in the Dockerfile's `ENTRYPOINT`. It cannot be baked in with
`ENV`, which is what an earlier version tried; that resolved to an empty port
at build time and the app never bound.

**Everyone is logged out after a deploy** — the `DataProtectionKeys` table is
missing or the app is pointed at a different database than before.
`SetApplicationName("ChatApp")` in `Program.cs` also has to stay exactly as it
is; it is mixed into every purpose string, so changing it invalidates every
cookie and token the old name protected.
