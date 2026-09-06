using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using ChatApp.Data;
using ChatApp.Models; // ApplicationUser
using ChatApp.Hubs;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Connection string.
// Read from configuration ("ConnectionStrings:DefaultConnection") or from the
// CONNECTION_STRING / DATABASE_URL environment variables, so no credentials
// ever need to live in source control.
// ---------------------------------------------------------------------------
var connectionString = ResolveConnectionString(builder.Configuration);

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));

// Identity
builder.Services.AddDefaultIdentity<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Lockout.MaxFailedAccessAttempts = 10;
})
    .AddEntityFrameworkStores<ApplicationDbContext>();

// Password-reset mail. Reads its provider from configuration; with nothing
// configured it writes the code to the log instead, so a fresh clone still
// works end to end. See DEPLOYMENT.md.
builder.Services.AddHttpClient();
builder.Services.AddScoped<ChatApp.Services.IAppEmailSender, ChatApp.Services.EmailSender>();

// ---------------------------------------------------------------------------
// Data Protection.
// This key ring signs and encrypts the auth cookie, the "remember me" token,
// the antiforgery token and Identity's own tokens. Left alone it is written to
// the local filesystem, which in a container is thrown away on every restart -
// on a free host that sleeps, that means everybody is signed out several times
// a day and any form left open comes back with an antiforgery failure. Keeping
// it in Postgres makes the ring outlive the container.
//
// SetApplicationName has to stay fixed: it is mixed into every purpose string,
// so changing it invalidates everything the old name protected.
// ---------------------------------------------------------------------------
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>()
    .SetApplicationName("ChatApp");

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddSignalR();

// IMPORTANT: this app addresses SignalR clients by user name (email), e.g.
// Clients.User("someone@example.com"). Without this provider SignalR uses the
// Identity GUID instead and every targeted message is silently dropped.
builder.Services.AddSingleton<IUserIdProvider, NameUserIdProvider>();

// Running behind a reverse proxy (Render, Fly, Azure, nginx): honour the
// X-Forwarded-Proto / X-Forwarded-For headers so the app knows the original
// request was https.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// Apply migrations on startup so a fresh database is usable right after deploy.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Database.Migrate();
        logger.LogInformation("Database migration completed.");
        logger.LogInformation("Email provider: {Provider}",
            ChatApp.Services.EmailSender.DescribeProvider(app.Configuration));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database migration failed.");
    }
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}
else
{
    app.UseDeveloperExceptionPage();
    // Only redirect to https locally. In a container the platform terminates
    // TLS in front of the app, and redirecting there causes a redirect loop.
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// Render (and every other platform health check) wants a cheap, anonymous 200.
// "/" is not that: it redirects to the login page, and a redirect is not a
// healthy answer as far as the platform is concerned.
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapRazorPages();
app.MapHub<ChatHub>("/chathub");

app.Run();


static string ResolveConnectionString(IConfiguration configuration)
{
    // 1. Standard ASP.NET configuration: appsettings.json, user-secrets, or the
    //    ConnectionStrings__DefaultConnection environment variable.
    var fromConfig = configuration.GetConnectionString("DefaultConnection");
    if (!string.IsNullOrWhiteSpace(fromConfig))
        return fromConfig;

    // 2. A plain Npgsql connection string in CONNECTION_STRING.
    var raw = Environment.GetEnvironmentVariable("CONNECTION_STRING");
    if (!string.IsNullOrWhiteSpace(raw))
        return raw;

    // 3. A postgres:// URL in DATABASE_URL (what Neon, Render, Heroku and Fly hand out).
    var url = Environment.GetEnvironmentVariable("DATABASE_URL");
    if (!string.IsNullOrWhiteSpace(url))
        return ConvertPostgresUrl(url);

    throw new InvalidOperationException(
        "No database connection string found. Set ConnectionStrings__DefaultConnection, " +
        "CONNECTION_STRING, or DATABASE_URL.");
}

static string ConvertPostgresUrl(string url)
{
    // postgres://user:password@host:port/database?sslmode=require
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var user = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
    var database = uri.AbsolutePath.TrimStart('/');
    var port = uri.Port > 0 ? uri.Port : 5432;

    return $"Host={uri.Host};Port={port};Username={user};Password={password};Database={database};" +
           "Pooling=true;SSL Mode=Require;Trust Server Certificate=true;";
}
