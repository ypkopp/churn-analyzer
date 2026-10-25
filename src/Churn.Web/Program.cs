using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Churn.Web.Data;

var builder = WebApplication.CreateBuilder(args);

// --- config ---------------------------------------------------------------
// Resolved lazily from IConfiguration at registration time so test hosts that
// inject ConnectionStrings:Db via ConfigureAppConfiguration are honored.
static string ConnStr(IConfiguration c) =>
    c.GetConnectionString("Db")
    ?? c["ConnectionStrings__Db"]
    ?? "Host=localhost;Port=5432;Database=churn;Username=churn;Password=churn";
var adminLogin = builder.Configuration["Auth:AdminLogin"]
    ?? Environment.GetEnvironmentVariable("Auth__AdminLogin") ?? "admin";
var adminPassword = builder.Configuration["Auth:AdminPassword"]
    ?? Environment.GetEnvironmentVariable("Auth__AdminPassword") ?? "";
var dpKeysPath = builder.Configuration["DataProtection:KeysPath"]
    ?? Environment.GetEnvironmentVariable("DataProtection__KeysPath")
    ?? Path.Combine(AppContext.BaseDirectory, "keys");
var isHttps = builder.Configuration.GetValue("Https:Enabled", false);
var bootstrapJson = builder.Configuration["Bootstrap:SeedFile"]; // optional one-shot import

// --- services -------------------------------------------------------------
builder.Services.AddDbContext<AppDb>(o => o.UseNpgsql(ConnStr(builder.Configuration)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SnapshotStore>();
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddSingleton<Churn.Web.Services.EvaluationService>();
builder.Services.AddSingleton<Churn.Web.Services.LlmService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<Churn.Web.Services.RefreshService>();

// Data Protection keys persisted outside the container so a normal restart
// doesn't invalidate issued auth cookies.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysPath))
    .SetApplicationName("churn-analyzer");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "churn.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = isHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        // API must return 401, not an HTML redirect to login.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/metrics"))
            { ctx.Response.StatusCode = 401; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/metrics"))
            { ctx.Response.StatusCode = 403; return Task.CompletedTask; }
            ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask;
        };
        // Server-side session revocation: every request re-checks the user's
        // security stamp and Disabled flag against the DB. A cookie issued
        // before a password change or disable is rejected — clearing only the
        // local cookie would not be enough.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            var name = ctx.Principal?.Identity?.Name;
            var stamp = ctx.Principal?.FindFirst("stamp")?.Value;
            if (string.IsNullOrEmpty(name)) { ctx.RejectPrincipal(); return; }
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Login == name);
            if (user is null || user.Disabled || user.SecurityStamp != stamp)
            { ctx.RejectPrincipal(); await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); }
        };
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("AuthenticatedOnly", p => p.RequireAuthenticatedUser());
    o.AddPolicy("AdminOnly", p => p.RequireRole(UserRoles.Admin));
    o.AddPolicy("AnalystOrAdmin", p => p.RequireRole(UserRoles.Admin, UserRoles.Analyst));
});
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = "churn.csrf"; });

// rate-limit login attempts (built-in ASP.NET Core)
var loginPermit = builder.Configuration.GetValue("RateLimit:LoginPermit", 5);
builder.Services.AddRateLimiter(o =>
{
    o.AddFixedWindowLimiter("login", opt =>
    {
        opt.PermitLimit = loginPermit; opt.Window = TimeSpan.FromMinutes(1); opt.QueueLimit = 0;
    });
    // analytics/whatif — a separate, gentler limiter keyed by the caller's
    // login, so a what-if burst never consumes the login budget.
    o.AddPolicy("analytics", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.User?.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

builder.Services.AddHealthChecks()
    .AddCheck<DbReadyCheck>("db", tags: new[] { "ready" })
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" });

builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    // bound the snapshot import body: reject absurdly large JSON payloads
    o.MultipartBodyLengthLimit = 8 * 1024 * 1024;
});
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 16 * 1024 * 1024);

builder.Services.AddRazorPages();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Trust only the known reverse-proxy hop inside the compose network.
    // Forwarded headers from arbitrary internet clients must not be honored.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor
        | ForwardedHeaders.XForwardedProto
        | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1;
    // Restrict to the compose network subnet (and loopback for local runs).
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Add(new System.Net.IPNetwork(
        System.Net.IPAddress.Parse("172.18.0.0"), 16));
    o.KnownIPNetworks.Add(new System.Net.IPNetwork(
        System.Net.IPAddress.Parse("10.0.0.0"), 8));
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

var app = builder.Build();

// --- migrate + optional one-shot bootstrap --------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    await db.Database.MigrateAsync();

    // initial admin user from config (hashed), only if none exists — bootstrap
    // mechanism only; env password is NOT re-applied to existing users.
    if (!string.IsNullOrEmpty(adminPassword) && !await db.Users.AnyAsync(u => u.Login == adminLogin))
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        var u = new AppUser { Login = adminLogin, Role = UserRoles.Admin,
            CreatedAt = TimeProvider.System.GetUtcNow(),
            SecurityStamp = Guid.NewGuid().ToString("N") };
        u.PasswordHash = hasher.HashPassword(u, adminPassword);
        db.Users.Add(u);
        await db.SaveChangesAsync();
    }
    // existing bootstrap admin (created before roles) gets Admin + a stamp once
    else if (await db.Users.FirstOrDefaultAsync(u => u.Login == adminLogin) is { } existing)
    {
        var dirty = false;
        if (existing.Role != UserRoles.Admin) { existing.Role = UserRoles.Admin; dirty = true; }
        if (string.IsNullOrEmpty(existing.SecurityStamp)) { existing.SecurityStamp = Guid.NewGuid().ToString("N"); dirty = true; }
        if (dirty) await db.SaveChangesAsync();
    }

    // optional one-shot bootstrap import (explicit, not on every restart)
    if (!string.IsNullOrEmpty(bootstrapJson) && File.Exists(bootstrapJson))
    {
        var store = scope.ServiceProvider.GetRequiredService<SnapshotStore>();
        if (await store.CurrentSnapshotVersionAsync() is null)
        {
            var snap = JsonSerializer.Deserialize<SourceSnapshotDto>(
                await File.ReadAllTextAsync(bootstrapJson),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (snap is not null) await store.ReplaceAsync(snap);
        }
    }
}

app.UseRateLimiter();
app.UseForwardedHeaders();
app.UseStaticFiles();          // wwwroot: css/js/img (tokens, components, brand)
app.UseAuthentication();
app.UseAuthorization();
var antiforgery = app.Services.GetRequiredService<IAntiforgery>();

// CSRF check for state-changing API calls (cookie-auth). GET/HEAD exempt.
app.Use(async (ctx, next) =>
{
    var mutating = HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method) ||
                   HttpMethods.IsPatch(ctx.Request.Method) || HttpMethods.IsDelete(ctx.Request.Method);
    if (mutating && ctx.Request.Path.StartsWithSegments("/api"))
    {
        try { await antiforgery.ValidateRequestAsync(ctx); }
        catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 403; return; }
    }
    await next();
});

// --- endpoints ------------------------------------------------------------
app.MapGet("/healthz/live", () => Results.Ok(new { status = "live" }));
app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = c => c.Tags.Contains("ready"),
});

// antiforgery token for API clients: issues the csrf cookie + returns the
// request token that must be sent back in X-CSRF-TOKEN on mutating calls.
app.MapGet("/api/v1/auth/csrf", (HttpContext http) =>
{
    var tokens = antiforgery.GetAndStoreTokens(http);
    return Results.Ok(new { token = tokens.RequestToken });
});

// UI poll interval is a client concern — separate from server re-evaluation.
app.MapGet("/api/v1/ui-config", (IConfiguration cfg) =>
    Results.Ok(new { pollSeconds = cfg.GetValue("Ui:PollSeconds", 15) })).RequireAuthorization();

app.MapPost("/api/v1/auth/login", async (HttpContext http, AppDb db,
    IPasswordHasher<AppUser> hasher, LoginRequest req) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Login == req.Login);
    var ok = user is not null && !user.Disabled &&
        hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password)
            != PasswordVerificationResult.Failed;
    if (!ok) return Results.Unauthorized();   // don't reveal user existence / disabled state
    var claims = new[] {
        new Claim(ClaimTypes.Name, user!.Login),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim("stamp", user.SecurityStamp),
    };
    var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(id));
    return Results.NoContent();
}).RequireRateLimiting("login");

app.MapGet("/api/v1/auth/me", (ClaimsPrincipal p) =>
    p.Identity?.IsAuthenticated == true
        ? Results.Ok(new MeResponse(p.Identity.Name ?? "", p.FindFirst(ClaimTypes.Role)?.Value ?? ""))
        : Results.Unauthorized());

app.MapPost("/api/v1/auth/logout", async (HttpContext http) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

// Admin password rotation: authenticated caller supplies current + new password.
// The new password is hashed by the same IPasswordHasher (PBKDF2) and stored.
// Old session cookies issued before the change are not invalidated by this —
// callers should re-login; that is documented behavior, not a bug.
app.MapPost("/api/v1/auth/rotate-password", async (HttpContext http, AppDb db,
    IPasswordHasher<AppUser> hasher, ClaimsPrincipal p, RotatePasswordRequest req) =>
{
    if (p.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    var login = p.Identity.Name ?? "";
    var user = await db.Users.FirstOrDefaultAsync(u => u.Login == login);
    if (user is null) return Results.NotFound(Err("not_found", "user"));
    if (hasher.VerifyHashedPassword(user, user.PasswordHash, req.CurrentPassword)
            == PasswordVerificationResult.Failed)
        return Results.Json(Err("invalid_credentials", "current password wrong"),
            statusCode: StatusCodes.Status403Forbidden);
    if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 10)
        return Results.BadRequest(Err("weak_password", "min 10 chars"));
    user.PasswordHash = hasher.HashPassword(user, req.NewPassword);
    user.SecurityStamp = Guid.NewGuid().ToString("N");   // revoke other sessions
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

// --- admin: employee management (Admin role only) ---------------------------
app.MapGet("/api/v1/admin/users", async (AppDb db) =>
{
    var users = await db.Users.AsNoTracking().OrderBy(u => u.Login)
        .Select(u => new { u.Id, u.Login, u.Role, u.Disabled, u.CreatedAt }).ToListAsync();
    return Results.Ok(users);
}).RequireAuthorization("AdminOnly");

// create employee — admin sets a temporary password; role is admin|analyst.
app.MapPost("/api/v1/admin/users", async (AppDb db,
    IPasswordHasher<AppUser> hasher, ClaimsPrincipal p, CreateUserRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Login) || req.Login.Length < 2)
        return Results.BadRequest(Err("invalid_login", "min 2 chars"));
    if (req.Role != UserRoles.Admin && req.Role != UserRoles.Analyst)
        return Results.BadRequest(Err("invalid_role", "admin|analyst"));
    if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 10)
        return Results.BadRequest(Err("weak_password", "min 10 chars"));
    if (await db.Users.AnyAsync(u => u.Login == req.Login))
        return Results.Conflict(Err("duplicate_login", "user exists"));
    var u = new AppUser { Login = req.Login, Role = req.Role,
        CreatedAt = TimeProvider.System.GetUtcNow(),
        SecurityStamp = Guid.NewGuid().ToString("N") };
    u.PasswordHash = hasher.HashPassword(u, req.Password);
    db.Users.Add(u); await db.SaveChangesAsync();
    return Results.Created($"/api/v1/admin/users/{u.Id}", new { u.Id, u.Login, u.Role });
}).RequireAuthorization("AdminOnly");

// enable/disable an account — admin only; a disabled user cannot log in and
// their existing cookies are rejected on the next request (stamp check).
app.MapPatch("/api/v1/admin/users/{id:long}/status", async (long id, AppDb db,
    ClaimsPrincipal p, SetUserStatusRequest req) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    if (user is null) return Results.NotFound(Err("not_found", "user"));
    // an admin cannot disable themselves — prevents lockout
    if (user.Login == p.Identity?.Name) return Results.BadRequest(Err("self_disable", "cannot disable own account"));
    user.Disabled = req.Disabled;
    if (req.Disabled) user.SecurityStamp = Guid.NewGuid().ToString("N"); // revoke sessions
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AdminOnly");

// minimal protected status endpoint (foundation — not full customers API)
app.MapGet("/api/v1/status", async (SnapshotStore store) =>
{
    var ver = await store.CurrentSnapshotVersionAsync();
    var settings = await store.CurrentSettingsAsync();
    return Results.Ok(new { snapshotVersion = ver, modelVersion = settings?.ModelVersion });
}).RequireAuthorization();

// ===========================================================================
// A07 — evaluation-driven endpoints. Reads serve the LATEST published batch;
// they never trigger an independent recalculation, so list/detail/summary for
// a request share evaluationId + snapshotVersion + modelVersion + asOf.
// ===========================================================================
var Jopts = new JsonSerializerOptions(JsonSerializerDefaults.Web);
static object Err(string e, string? d = null) => new ApiError(e, d);

// --- analytics: upcoming renewals + explainable priorities -------------------
// Serves ONLY the latest published batch (no recompute). Buckets are grouped
// by the service expiry date (schedule, not a churn date); reasons count
// distinct customers per feature code and a customer may carry several.
app.MapGet("/api/v1/analytics/renewals", async (AppDb db) =>
{
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    if (run is null) return Results.Ok(new { state = "no_data" });
    var rows = await db.CustomerResults.Where(x => x.EvaluationId == run.EvaluationId).ToListAsync();

    var buckets = new Dictionary<string, RenewalBucketDto>(StringComparer.Ordinal);
    var reasonCounts = new Dictionary<string, int>(StringComparer.Ordinal);
    int horizon = 0;

    foreach (var r in rows)
    {
        var dto = JsonSerializer.Deserialize<CustomerResultDto>(r.Payload, Jopts)!;
        var level = dto.Rank.RiskLevel;
        horizon = dto.Detail.Money?.HorizonDays ?? horizon;
        foreach (var line in dto.Detail.Money?.Lines ?? (IReadOnlyList<MoneyLineDto>)Array.Empty<MoneyLineDto>())
        {
            var day = line.DueAt.ToString("yyyy-MM-dd");
            if (!buckets.TryGetValue(day, out var b))
                b = new RenewalBucketDto(day, 0, 0, 0, 0, 0);
            buckets[day] = b with {
                Overdue = b.Overdue + (line.Overdue ? 1 : 0),
                High = b.High + (level == "high" ? 1 : 0),
                Medium = b.Medium + (level == "medium" ? 1 : 0),
                Low = b.Low + (level == "low" ? 1 : 0),
                NoAssessment = b.NoAssessment + (level == "no_assessment" ? 1 : 0),
            };
        }
        foreach (var code in dto.Rank.Reasons ?? (IReadOnlyList<string>)Array.Empty<string>())
            reasonCounts[code] = reasonCounts.TryGetValue(code, out var n) ? n + 1 : 1;
    }

    var ordered = buckets.Values.OrderBy(b => b.Date).ToList();
    var reasons = reasonCounts.OrderByDescending(k => k.Value).ThenBy(k => k.Key)
        .Select(k => new ReasonCountDto(k.Key, k.Value)).ToList();
    return Results.Ok(new RenewalsAnalyticsDto(ordered, reasons,
        run.SnapshotVersion, run.ModelVersion, run.EvaluationId, run.AsOf, horizon));
}).RequireAuthorization("AnalystOrAdmin");

app.MapGet("/api/v1/customers", async (HttpContext http, AppDb db,
    string? q, string? sort, string? dir, string? riskLevel, int? page, int? pageSize) =>
{
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    if (run is null) return Results.Ok(new { state = "no_data", items = Array.Empty<object>(), total = 0 });

    var rows = await db.CustomerResults.Where(x => x.EvaluationId == run.EvaluationId).ToListAsync();
    var items = rows.Select(r => JsonSerializer.Deserialize<CustomerResultDto>(r.Payload, Jopts)!.Rank).ToList();

    if (riskLevel is not null) items = items.Where(i => i.RiskLevel == riskLevel).ToList();
    var needle = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
    if (needle is not null)
        items = items.Where(i => i.Email.Contains(needle, StringComparison.OrdinalIgnoreCase)
                              || i.AccountId.ToString().Contains(needle)).ToList();
    var asc = !string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase);
    items = sort?.ToLowerInvariant() switch
    {
        "email" => asc ? items.OrderBy(i => i.Email).ThenBy(i => i.AccountId).ToList()
                       : items.OrderByDescending(i => i.Email).ThenBy(i => i.AccountId).ToList(),
        "balance" => asc ? items.OrderBy(i => i.Balance).ThenBy(i => i.AccountId).ToList()
                        : items.OrderByDescending(i => i.Balance).ThenBy(i => i.AccountId).ToList(),
        "totalreplenished" => asc ? items.OrderBy(i => i.TotalReplenished).ThenBy(i => i.AccountId).ToList()
                                  : items.OrderByDescending(i => i.TotalReplenished).ThenBy(i => i.AccountId).ToList(),
        "riskscore" => asc ? items.OrderBy(i => i.RiskScore).ThenBy(i => i.AccountId).ToList()
                           : items.OrderByDescending(i => i.RiskScore).ThenBy(i => i.AccountId).ToList(),
        _ => asc ? items.OrderBy(i => i.AccountId).ToList()
                 : items.OrderByDescending(i => i.AccountId).ToList(),
    };

    var total = items.Count;
    var p = Math.Max(1, page ?? 1); var ps = Math.Clamp(pageSize ?? 50, 1, 500);
    var pageItems = items.Skip((p - 1) * ps).Take(ps).ToList();
    var counts = new
    {
        all = rows.Count,
        high = items.Count(i => i.RiskLevel == "high"),
        medium = items.Count(i => i.RiskLevel == "medium"),
        low = items.Count(i => i.RiskLevel == "low"),
        none = items.Count(i => i.RiskLevel == "no_assessment"),
    };
    return Results.Ok(new
    {
        items = pageItems, total, counts,
        run.SnapshotVersion, run.ModelVersion, run.EvaluationId, run.AsOf,
    });
}).RequireAuthorization();

app.MapGet("/api/v1/customers/{id:long}", async (long id, AppDb db) =>
{
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    if (run is null) return Results.NotFound(Err("no_data", "no snapshot evaluated yet"));
    var row = await db.CustomerResults
        .Where(x => x.EvaluationId == run.EvaluationId && x.AccountId == id)
        .FirstOrDefaultAsync();
    if (row is null) return Results.NotFound(Err("not_found", "customer not in current snapshot"));
    var dto = JsonSerializer.Deserialize<CustomerResultDto>(row.Payload, Jopts)!;
    // attach service lines from the stored snapshot for this owner
    var snapRow = await db.Snapshots.OrderByDescending(s => s.SnapshotVersion).FirstAsync();
    var snap = JsonSerializer.Deserialize<SourceSnapshotDto>(snapRow.Payload, Jopts)!;
    var svcLines = snap.Services.Where(s => s.OwnerId == id)
        .Select(s => new ServiceLineDto(s.Id, s.Name, s.PaymentTerm, s.AutoProlong,
            s.Status, s.Price, s.ExpiresAt)).ToList();
    var detail = dto.Detail with { Services = svcLines };
    // include the SAME batch identity as list/summary so clients never mix versions
    return Results.Ok(new
    {
        detail,
        dto.SnapshotVersion, dto.ModelVersion, dto.EvaluationId, dto.AsOf,
    });
}).RequireAuthorization();

app.MapGet("/api/v1/analytics/summary", async (AppDb db) =>
{
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    if (run is null) return Results.Ok(new { state = "no_data" });
    var row = await db.SummaryResults.FirstOrDefaultAsync(x => x.EvaluationId == run.EvaluationId);
    if (row is null) return Results.Ok(new { state = "no_data" });
    return Results.Ok(JsonSerializer.Deserialize<SummaryDto>(row.Payload, Jopts)!);
}).RequireAuthorization();

// --- AI brief (optional): de-identified aggregate -> operator-configured LLM.
// Manual explicit call only; cached per evaluationId so polling never fires.
// When the key/endpoint is missing or not allowed the feature reports as
// unavailable — the rest of analytics keeps working.
var aiCache = new System.Collections.Concurrent.ConcurrentDictionary<Guid, (string brief, string model)>();
app.MapPost("/api/v1/ai/brief", async (AppDb db, Churn.Web.Services.LlmService llm,
    ClaimsPrincipal p, CancellationToken ct) =>
{
    if (!llm.Enabled)
        return Results.Json(Err("ai_not_configured", "AI brief is disabled on this stand"), statusCode: 404);
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
    if (run is null) return Results.NotFound(Err("no_data", "no evaluation batch"));

    if (aiCache.TryGetValue(run.EvaluationId, out var hit))
        return Results.Ok(new { brief = hit.brief, model = hit.model, run.EvaluationId, run.AsOf, cached = true });

    // de-identified aggregate: counts, money totals (EUR cents), renewal buckets
    // and reason codes — no emails, no service names, no raw snapshot.
    var rows = await db.CustomerResults.Where(x => x.EvaluationId == run.EvaluationId).ToListAsync(ct);
    int high = 0, medium = 0, low = 0, noAssess = 0;
    var reasons = new Dictionary<string, int>();
    var buckets = new Dictionary<string, int>();
    long renewal = 0, fund = 0, uncovered = 0, atRisk = 0;
    foreach (var r in rows)
    {
        var dto = JsonSerializer.Deserialize<CustomerResultDto>(r.Payload, Jopts)!;
        switch (dto.Rank.RiskLevel)
        { case "high": high++; break; case "medium": medium++; break;
          case "low": low++; break; default: noAssess++; break; }
        foreach (var c in dto.Rank.Reasons ?? Array.Empty<string>())
            reasons[c] = reasons.TryGetValue(c, out var n) ? n + 1 : 1;
        var m = dto.Detail.Money;
        if (m is not null)
        { renewal += m.RenewalCostInHorizon; fund += m.AvailableFund;
          uncovered += m.UncoveredAmount; if (m.AtRiskAmount.HasValue) atRisk += m.AtRiskAmount.Value;
          foreach (var l in m.Lines) { var d = l.DueAt.ToString("yyyy-MM-dd"); buckets[d] = buckets.TryGetValue(d, out var n) ? n + 1 : 1; } }
    }
    var aggregate = new
    {
        batch = new { run.EvaluationId, run.AsOf, run.SnapshotVersion, run.ModelVersion },
        counts = new { high, medium, low, no_assessment = noAssess },
        money_eur_cents = new { renewal_cost_total = renewal, available_fund_total = fund,
            uncovered_total = uncovered, at_risk_total = atRisk, semantic = "heuristic_money_index" },
        renewals_by_day = buckets.OrderBy(k => k.Key).Select(k => new { date = k.Key, lines = k.Value }),
        reasons = reasons.OrderByDescending(k => k.Value).Select(k => new { code = k.Key, customers = k.Value }),
        notes = "score=priority_index_not_probability; money=heuristic_index_not_expected_loss; no_assessment=insufficient_data"
    };

    var res = await llm.BriefAsync(aggregate, run.EvaluationId.ToString(), ct);
    if (!res.Ok) return Results.Json(Err("ai_unavailable", res.Reason), statusCode: 502);
    aiCache[run.EvaluationId] = (res.Brief!, res.Model ?? "");
    return Results.Ok(new { brief = res.Brief, model = res.Model, run.EvaluationId, run.AsOf, cached = false });
}).RequireAuthorization("AnalystOrAdmin").RequireRateLimiting("analytics");

// --- snapshot: atomic replace + version, then re-evaluate -------------------
// Only Admin may replace the source snapshot.
app.MapPut("/api/v1/source/snapshot", async (SnapshotStore store,
    Churn.Web.Services.EvaluationService eval, SourceSnapshotDto snap) =>
{
    var (ok, version, error) = await store.ReplaceAsync(snap);
    if (!ok) return Results.BadRequest(Err("invalid_snapshot", error));
    var run = await eval.EvaluateAndPublishAsync();
    return Results.Ok(new { snapshotVersion = version, run?.EvaluationId, run?.AsOf });
}).RequireAuthorization("AdminOnly");

// --- model settings: optimistic concurrency via expectedVersion -------------
app.MapGet("/api/v1/model/settings", async (SnapshotStore store) =>
{
    var row = await store.CurrentSettingsAsync();
    if (row is null)
        return Results.Ok(new { modelVersion = 0, horizonDays = 14,
            weights = new { }, thresholds = new { } });
    var dto = JsonSerializer.Deserialize<ModelSettingsDto>(row.Payload, Jopts)!;
    // authoritative version is the row's monotonically increasing ModelVersion,
    // not whatever the payload happens to carry — expectedVersion must match it.
    return Results.Ok(dto with { ModelVersion = row.ModelVersion });
}).RequireAuthorization();

app.MapPatch("/api/v1/model/settings", async (SnapshotStore store,
    Churn.Web.Services.EvaluationService eval,
    ModelSettingsPatch patch, HttpContext http) =>
{
    var current = await store.CurrentSettingsAsync();
    var currentVersion = current?.ModelVersion ?? 0;
    // optimistic concurrency: caller must declare the version they saw
    if (patch.ExpectedVersion is not null && patch.ExpectedVersion != currentVersion)
        return Results.Json(Err("version_conflict",
            $"expected {patch.ExpectedVersion}, current {currentVersion}"),
            statusCode: StatusCodes.Status409Conflict);
    var (ok, row, error) = await store.SaveSettingsAsync(patch.Settings);
    if (!ok) return Results.BadRequest(Err("invalid_settings", error));
    var run = await eval.EvaluateAndPublishAsync();
    return Results.Ok(new { modelVersion = row!.ModelVersion, run?.EvaluationId, run?.AsOf });
}).RequireAuthorization("AnalystOrAdmin");

// --- retention tasks --------------------------------------------------------
// Helper: project a task + whether its customer still exists in the latest batch.
static async Task<TaskDto> TaskDtoOf(AppDb db, RetentionTask t)
{
    var assignee = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == t.AssigneeUserId);
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    var avail = run is not null && await db.CustomerResults
        .AnyAsync(x => x.EvaluationId == run.EvaluationId && x.AccountId == t.AccountId);
    return new TaskDto(t.Id, t.AccountId, t.Title, t.AssigneeUserId,
        assignee?.Login ?? "?", t.DueAt, t.Priority, t.Description, t.Status,
        t.EvaluationId, t.AsOf, t.CreatedAt, t.CompletedAt, avail);
}

// list tasks — filter by status/assignee/account, search by title.
app.MapGet("/api/v1/tasks", async (AppDb db, string? status, long? assignee, long? accountId, string? q) =>
{
    var query = db.RetentionTasks.AsNoTracking().AsQueryable();
    if (!string.IsNullOrEmpty(status)) query = query.Where(t => t.Status == status);
    if (assignee.HasValue) query = query.Where(t => t.AssigneeUserId == assignee.Value);
    if (accountId.HasValue) query = query.Where(t => t.AccountId == accountId.Value);
    if (!string.IsNullOrWhiteSpace(q)) query = query.Where(t => t.Title.Contains(q));
    var tasks = await query.OrderByDescending(t => t.CreatedAt).ToListAsync();
    var list = new List<TaskDto>();
    foreach (var t in tasks) list.Add(await TaskDtoOf(db, t));
    return Results.Ok(list);
}).RequireAuthorization("AnalystOrAdmin");

// create from a customer card — bound to the current batch for decision context.
app.MapPost("/api/v1/tasks", async (AppDb db, ClaimsPrincipal p, CreateTaskRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(Err("invalid_title", "required"));
    if (req.Priority is not ("high" or "normal" or "low")) req = req with { Priority = "normal" };
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    var me = await db.Users.FirstOrDefaultAsync(u => u.Login == p.Identity!.Name);
    var assignee = await db.Users.FirstOrDefaultAsync(u => u.Id == req.AssigneeUserId && !u.Disabled);
    if (assignee is null) return Results.BadRequest(Err("invalid_assignee", "active user required"));
    var t = new RetentionTask
    {
        Title = req.Title.Trim(), AccountId = req.AccountId,
        AssigneeUserId = assignee.Id, DueAt = req.DueAt, Priority = req.Priority,
        Description = req.Description, Status = TaskStatuses.New,
        EvaluationId = run?.EvaluationId ?? Guid.Empty,
        AsOf = run?.AsOf ?? TimeProvider.System.GetUtcNow(),
        CreatedAt = TimeProvider.System.GetUtcNow(),
    };
    db.RetentionTasks.Add(t);
    await db.SaveChangesAsync();
    db.TaskEvents.Add(new TaskEvent { TaskId = t.Id, ToStatus = TaskStatuses.New,
        ByUserId = me?.Id ?? 0, At = t.CreatedAt });
    await db.SaveChangesAsync();
    return Results.Created($"/api/v1/tasks/{t.Id}", await TaskDtoOf(db, t));
}).RequireAuthorization("AnalystOrAdmin");

// edit fields (title/due/priority/description) — not status (use the status endpoint).
app.MapPatch("/api/v1/tasks/{id:long}", async (long id, AppDb db, UpdateTaskRequest req) =>
{
    var t = await db.RetentionTasks.FirstOrDefaultAsync(x => x.Id == id);
    if (t is null) return Results.NotFound(Err("not_found", "task"));
    if (req.Title is not null) t.Title = req.Title.Trim();
    if (req.DueAt is not null) t.DueAt = req.DueAt;
    if (req.Priority is not null) t.Priority = req.Priority;
    if (req.Description is not null) t.Description = req.Description;
    await db.SaveChangesAsync();
    return Results.Ok(await TaskDtoOf(db, t));
}).RequireAuthorization("AnalystOrAdmin");

// change status with an append-only journal entry.
app.MapPost("/api/v1/tasks/{id:long}/status", async (long id, AppDb db,
    ClaimsPrincipal p, TaskStatusRequest req) =>
{
    var t = await db.RetentionTasks.FirstOrDefaultAsync(x => x.Id == id);
    if (t is null) return Results.NotFound(Err("not_found", "task"));
    if (req.Status is not (TaskStatuses.New or TaskStatuses.InProgress or TaskStatuses.Done or TaskStatuses.Cancelled))
        return Results.BadRequest(Err("invalid_status", "new|in_progress|done|cancelled"));
    var me = await db.Users.FirstOrDefaultAsync(u => u.Login == p.Identity!.Name);
    db.TaskEvents.Add(new TaskEvent { TaskId = t.Id, FromStatus = t.Status,
        ToStatus = req.Status, Note = req.Note, At = TimeProvider.System.GetUtcNow(),
        ByUserId = me?.Id ?? 0 });
    t.Status = req.Status;
    t.CompletedAt = req.Status is TaskStatuses.Done or TaskStatuses.Cancelled
        ? TimeProvider.System.GetUtcNow() : null;
    await db.SaveChangesAsync();
    return Results.Ok(await TaskDtoOf(db, t));
}).RequireAuthorization("AnalystOrAdmin");

// journal for one task — append-only status history.
app.MapGet("/api/v1/tasks/{id:long}/events", async (long id, AppDb db) =>
{
    var ev = await db.TaskEvents.Where(x => x.TaskId == id).OrderBy(x => x.At).ToListAsync();
    return Results.Ok(ev.Select(e => new { e.FromStatus, e.ToStatus, e.Note, e.At, e.ByUserId }));
}).RequireAuthorization("AnalystOrAdmin");

// tasks linked to one customer (for the card) — plus whether customer is in
// the current batch (so a stale task shows "customer data unavailable").
app.MapGet("/api/v1/customers/{id:long}/tasks", async (long id, AppDb db) =>
{
    var tasks = await db.RetentionTasks.Where(t => t.AccountId == id)
        .OrderByDescending(t => t.CreatedAt).ToListAsync();
    var list = new List<TaskDto>();
    foreach (var t in tasks) list.Add(await TaskDtoOf(db, t));
    return Results.Ok(list);
}).RequireAuthorization("AnalystOrAdmin");

// --- contact messages: draft / mailto-prepared / sent (if transport set) ------
app.MapGet("/api/v1/messages", async (long? accountId, AppDb db) =>
{
    var q = db.ContactMessages.AsNoTracking().AsQueryable();
    if (accountId.HasValue) q = q.Where(m => m.AccountId == accountId.Value);
    var list = await q.OrderByDescending(m => m.CreatedAt).ToListAsync();
    return Results.Ok(list.Select(m => new MessageDto(m.Id, m.AccountId, m.Recipient,
        m.Subject, m.Body, m.Status, m.TransportNote, m.EvaluationId, m.AsOf,
        m.CreatedAt, m.SentAt)));
}).RequireAuthorization("AnalystOrAdmin");

app.MapPost("/api/v1/messages", async (AppDb db, ClaimsPrincipal p, CreateMessageRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Subject) || string.IsNullOrWhiteSpace(req.Body))
        return Results.BadRequest(Err("invalid_message", "subject+body required"));
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    var m = new ContactMessage
    {
        AccountId = req.AccountId, Recipient = req.Recipient,
        Subject = req.Subject.Trim(), Body = req.Body, Status = "draft",
        EvaluationId = run?.EvaluationId ?? Guid.Empty,
        AsOf = run?.AsOf ?? TimeProvider.System.GetUtcNow(),
        CreatedAt = TimeProvider.System.GetUtcNow(),
    };
    db.ContactMessages.Add(m); await db.SaveChangesAsync();
    return Results.Created($"/api/v1/messages/{m.Id}",
        new MessageDto(m.Id, m.AccountId, m.Recipient, m.Subject, m.Body,
            m.Status, m.TransportNote, m.EvaluationId, m.AsOf, m.CreatedAt, m.SentAt));
}).RequireAuthorization("AnalystOrAdmin");

// mark as prepared-for-mailto (a draft the user opened in their mail client).
app.MapPost("/api/v1/messages/{id:long}/prepared", async (long id, AppDb db) =>
{
    var m = await db.ContactMessages.FirstOrDefaultAsync(x => x.Id == id);
    if (m is null) return Results.NotFound(Err("not_found", "message"));
    m.Status = "prepared"; await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AnalystOrAdmin");

// send via configured SMTP/API transport. When unset, reports unavailable
// (not a fake success). SMTP/API secrets live only in server env.
app.MapPost("/api/v1/messages/{id:long}/send", async (long id, AppDb db,
    IConfiguration cfg) =>
{
    var m = await db.ContactMessages.FirstOrDefaultAsync(x => x.Id == id);
    if (m is null) return Results.NotFound(Err("not_found", "message"));
    var transport = cfg["Smtp:Transport"] ?? Environment.GetEnvironmentVariable("Smtp__Transport");
    if (string.IsNullOrEmpty(transport))
    {
        m.Status = "error"; m.TransportNote = "smtp_not_configured";
        await db.SaveChangesAsync();
        return Results.Json(Err("transport_unavailable",
            "email transport is not configured on this stand"), statusCode: 503);
    }
    try
    {
        // place-holder for real SMTP/API send — transport value is an opaque URI.
        m.Status = "error"; m.TransportNote = "transport_stub";
        await db.SaveChangesAsync();
        return Results.Json(Err("transport_stub", "transport configured but send not implemented"), statusCode: 503);
    }
    catch { m.Status = "error"; m.TransportNote = "send_failed"; await db.SaveChangesAsync();
        return Results.Json(Err("send_failed"), statusCode: 502); }
}).RequireAuthorization("AnalystOrAdmin");

// --- saved segments (per-user filter presets, evaluated on current batch) -----
app.MapGet("/api/v1/segments", async (AppDb db, ClaimsPrincipal p) =>
{
    var me = await db.Users.FirstOrDefaultAsync(u => u.Login == p.Identity!.Name);
    if (me is null) return Results.Unauthorized();
    var list = await db.SavedSegments.Where(s => s.OwnerUserId == me.Id)
        .OrderBy(s => s.CreatedAt).ToListAsync();
    return Results.Ok(list.Select(s => new SegmentDto(s.Id, s.Name, s.Query ?? "", s.CreatedAt)));
}).RequireAuthorization("AnalystOrAdmin");

app.MapPost("/api/v1/segments", async (AppDb db, ClaimsPrincipal p, CreateSegmentRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(Err("invalid_name", "required"));
    var me = await db.Users.FirstOrDefaultAsync(u => u.Login == p.Identity!.Name);
    if (me is null) return Results.Unauthorized();
    var s = new SavedSegment { OwnerUserId = me.Id, Name = req.Name.Trim(),
        Query = req.Query ?? "", CreatedAt = TimeProvider.System.GetUtcNow() };
    db.SavedSegments.Add(s); await db.SaveChangesAsync();
    return Results.Created($"/api/v1/segments/{s.Id}", new SegmentDto(s.Id, s.Name, s.Query, s.CreatedAt));
}).RequireAuthorization("AnalystOrAdmin");

app.MapPatch("/api/v1/segments/{id:long}", async (long id, AppDb db,
    ClaimsPrincipal p, CreateSegmentRequest req) =>
{
    var me = await db.Users.FirstOrDefaultAsync(u => u.Login == p.Identity!.Name);
    var s = await db.SavedSegments.FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me!.Id);
    if (s is null) return Results.NotFound(Err("not_found", "segment"));
    if (!string.IsNullOrWhiteSpace(req.Name)) s.Name = req.Name.Trim();
    if (req.Query is not null) s.Query = req.Query;
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AnalystOrAdmin");

app.MapDelete("/api/v1/segments/{id:long}", async (long id, AppDb db, ClaimsPrincipal p) =>
{
    var me = await db.Users.FirstOrDefaultAsync(u => u.Login == p.Identity!.Name);
    var s = await db.SavedSegments.FirstOrDefaultAsync(x => x.Id == id && x.OwnerUserId == me!.Id);
    if (s is null) return Results.NotFound(Err("not_found", "segment"));
    db.SavedSegments.Remove(s); await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AnalystOrAdmin");

// --- analytics: compare two saved batches + what-if scenario ------------------
// Lists stored evaluation runs so the UI can pick two real batches.
app.MapGet("/api/v1/analytics/batches", async (AppDb db) =>
{
    var runs = await db.EvalRuns.OrderByDescending(r => r.CreatedAt)
        .Take(50).ToListAsync();
    return Results.Ok(runs.Select(r => new { r.EvaluationId, r.AsOf,
        r.SnapshotVersion, r.ModelVersion }));
}).RequireAuthorization("AnalystOrAdmin");

// Compare two saved batches at accountId level — customers present on only
// one side are kept, not dropped. No claim that a single factor caused a diff.
app.MapGet("/api/v1/analytics/compare", async (AppDb db, Guid a, Guid b) =>
{
    var ra = await db.EvalRuns.FirstOrDefaultAsync(x => x.EvaluationId == a);
    var rb = await db.EvalRuns.FirstOrDefaultAsync(x => x.EvaluationId == b);
    if (ra is null || rb is null) return Results.NotFound(Err("not_found", "batch"));
    var rowsA = await db.CustomerResults.Where(x => x.EvaluationId == a).ToListAsync();
    var rowsB = await db.CustomerResults.Where(x => x.EvaluationId == b).ToListAsync();
    var sa = await db.SummaryResults.FirstOrDefaultAsync(x => x.EvaluationId == a);
    var sb = await db.SummaryResults.FirstOrDefaultAsync(x => x.EvaluationId == b);

    var mapA = rowsA.ToDictionary(x => x.AccountId, x => JsonSerializer.Deserialize<CustomerResultDto>(x.Payload, Jopts)!);
    var mapB = rowsB.ToDictionary(x => x.AccountId, x => JsonSerializer.Deserialize<CustomerResultDto>(x.Payload, Jopts)!);
    var ids = mapA.Keys.Union(mapB.Keys).OrderBy(x => x).ToList();

    var changes = ids.Select(id =>
    {
        var ca = mapA.GetValueOrDefault(id); var cb = mapB.GetValueOrDefault(id);
        // scoreDelta is a number ONLY when both sides have an assessment.
        // no_assessment / appeared / removed carry an explicit transition, not
        // a fabricated numeric delta (nothing to subtract from).
        if (ca is null)
            return new { accountId = id, state = "appeared", transition = "appeared",
                from = (object?)null, to = Rank(cb),
                scoreDelta = (int?)null, levelChanged = false, assessChanged = false, reasonChanged = false };
        if (cb is null)
            return new { accountId = id, state = "removed", transition = "removed",
                from = Rank(ca), to = (object?)null,
                scoreDelta = (int?)null, levelChanged = false, assessChanged = false, reasonChanged = false };
        var assessedBoth = !ca.Rank.NoAssessment && !cb.Rank.NoAssessment;
        int? scoreDelta = assessedBoth ? (cb.Rank.RiskScore ?? 0) - (ca.Rank.RiskScore ?? 0) : (int?)null;
        var assessChanged = ca.Rank.NoAssessment != cb.Rank.NoAssessment;
        var levelChanged = ca.Rank.RiskLevel != cb.Rank.RiskLevel;
        var reasonChanged = !new HashSet<string>(ca.Rank.Reasons ?? new List<string>())
            .SetEquals(cb.Rank.Reasons ?? new List<string>());
        var transition = assessChanged
            ? (ca.Rank.NoAssessment ? "no_assessment_to_assessed" : "assessed_to_no_assessment")
            : (levelChanged ? "level_changed" : "same");
        return new
        {
            accountId = id,
            // 'changed' only when something actually differs; a null delta is
            // not a delta, it is "no assessment on at least one side".
            state = levelChanged || assessChanged || scoreDelta != 0 || reasonChanged ? "changed" : "same",
            transition,
            from = Rank(ca), to = Rank(cb), scoreDelta, levelChanged, assessChanged, reasonChanged
        };
    }).ToList();

    var finalChanged = changes.Where(c => c.state == "changed" &&
        (c.levelChanged || c.assessChanged || c.reasonChanged || c.scoreDelta is not null)).ToList();
    var finalAppeared = changes.Where(c => c.state == "appeared").ToList();
    var finalRemoved = changes.Where(c => c.state == "removed").ToList();

    return Results.Ok(new
    {
        a = new { ra.EvaluationId, ra.AsOf, ra.SnapshotVersion, ra.ModelVersion },
        b = new { rb.EvaluationId, rb.AsOf, rb.SnapshotVersion, rb.ModelVersion },
        sameBatch = a == b,
        countsA = counts(mapA), countsB = counts(mapB),
        moneyA = sa is null ? null : JsonSerializer.Deserialize<SummaryDto>(sa.Payload, Jopts)?.Money,
        moneyB = sb is null ? null : JsonSerializer.Deserialize<SummaryDto>(sb.Payload, Jopts)?.Money,
        changed = finalChanged,
        appeared = finalAppeared,
        removed = finalRemoved,
        total = ids.Count
    });
    static object? Rank(CustomerResultDto? d) => d is null ? null : new
    {
        d.Rank.AccountId, d.Rank.Email, d.Rank.RiskScore, d.Rank.RiskLevel,
        d.Rank.Reasons, d.Rank.NoAssessment
    };
    static object counts(Dictionary<long, CustomerResultDto> m) => new
    {
        high = m.Values.Count(v => v.Rank.RiskLevel == "high"),
        medium = m.Values.Count(v => v.Rank.RiskLevel == "medium"),
        low = m.Values.Count(v => v.Rank.RiskLevel == "low"),
        no_assessment = m.Values.Count(v => v.Rank.NoAssessment)
    };
}).RequireAuthorization("AnalystOrAdmin");

// What-if: recompute risk+money on the CURRENT snapshot with proposed settings
// and the SAME asOf — without writing a batch or touching live settings.
// Marked "scenario, not applied" — never publishable.
app.MapPost("/api/v1/analytics/whatif", async (AppDb db,
    TimeProvider clock, WhatIfRequest req) =>
{
    var snapRow = await db.Snapshots.OrderByDescending(s => s.SnapshotVersion).FirstOrDefaultAsync();
    if (snapRow is null) return Results.Ok(new { state = "no_data" });
    var settingsRow = await db.ModelSettings.OrderByDescending(m => m.ModelVersion).FirstOrDefaultAsync();
    var baseSettings = Churn.Web.Services.EvaluationService.ToCoreSettings(settingsRow) ?? Churn.Core.ModelSettings.Baseline();
    // same non-writing validator as PATCH — explicit invalid fields -> 400,
    // missing settings -> documented baseline-only scenario.
    if (req?.Settings is not null)
    {
        var invalid = InputValidator.ModelSettings(req.Settings);
        if (invalid is not null)
            return Results.BadRequest(Err("invalid_settings", invalid));
    }
    var scenario = req?.Settings is not null
        ? Churn.Web.Services.EvaluationService.ToCoreSettings(req.Settings) : baseSettings;
    var snap = JsonSerializer.Deserialize<SourceSnapshotDto>(snapRow.Payload, Jopts)!;
    var asOf = clock.GetUtcNow();   // shared by baseline AND scenario — no time skew
    var ctxB = new Churn.Core.EvaluationContext(asOf, baseSettings.HorizonDays,
        snapRow.SnapshotVersion, baseSettings.ModelVersion);
    var ctxS = new Churn.Core.EvaluationContext(asOf, scenario.HorizonDays,
        snapRow.SnapshotVersion, scenario.ModelVersion);
    var accounts = snap.Accounts.Select(Churn.Web.Services.EvaluationService.ToCoreAccount).ToList();
    var services = snap.Services.Select(Churn.Web.Services.EvaluationService.ToCoreService).ToList();

    var diffs = accounts.Select(a =>
    {
        var rb = Churn.Core.RiskEngine.Evaluate(a, services, ctxB, baseSettings);
        var mb = Churn.Core.MoneyEngine.Evaluate(a, services, rb, ctxB);
        var rs = Churn.Core.RiskEngine.Evaluate(a, services, ctxS, scenario);
        var ms = Churn.Core.MoneyEngine.Evaluate(a, services, rs, ctxS);
        return new
        {
            a.Id, a.Email,
            baseScore = rb.NoAssessment ? (int?)null : rb.Score, baseLevel = Lvl(rb),
            scenScore = rs.NoAssessment ? (int?)null : rs.Score, scenLevel = Lvl(rs),
            // delta only when both sides are assessed — no fake number from absence
            scoreDelta = (!rb.NoAssessment && !rs.NoAssessment) ? rs.Score - rb.Score : (int?)null,
            levelChanged = Lvl(rb) != Lvl(rs), assessChanged = rb.NoAssessment != rs.NoAssessment,
            assessTransition = rb.NoAssessment != rs.NoAssessment
                ? (rb.NoAssessment ? "no_assessment_to_assessed" : "assessed_to_no_assessment") : "same",
            // money delta only when both sides have a money estimate; a computed
            // zero is a real zero, not a stand-in for "no estimate".
            moneyDelta = (mb.AtRiskAmount.HasValue && ms.AtRiskAmount.HasValue)
                ? ms.AtRiskAmount.Value - mb.AtRiskAmount.Value : (long?)null,
            reasonsChanged = !new HashSet<string>(rb.ReasonCodes).SetEquals(rs.ReasonCodes)
        };
    }).ToList();
    static string Lvl(Churn.Core.CustomerAssessment r) =>
        r.NoAssessment ? "no_assessment" : r.Level.ToString().ToLowerInvariant();

    var changed = diffs.Where(d => d.levelChanged || d.assessChanged || d.scoreDelta != 0 || d.moneyDelta != 0 || d.reasonsChanged)
        .OrderByDescending(d => Math.Abs(d.scoreDelta ?? 0)).ThenByDescending(d => Math.Abs(d.moneyDelta ?? 0)).Take(20).ToList();
    var noEstimate = diffs.Count(d => d.scoreDelta is null);
    long rbR = 0, rbU = 0, rbA = 0, rsA = 0;
    foreach (var a in accounts)
    {
        var rb = Churn.Core.RiskEngine.Evaluate(a, services, ctxB, baseSettings);
        var mb = Churn.Core.MoneyEngine.Evaluate(a, services, rb, ctxB);
        var rs = Churn.Core.RiskEngine.Evaluate(a, services, ctxS, scenario);
        var ms = Churn.Core.MoneyEngine.Evaluate(a, services, rs, ctxS);
        rbR += mb.RenewalCostInHorizon; rbU += mb.UncoveredAmount; rbA += mb.AtRiskAmount ?? 0; rsA += ms.AtRiskAmount ?? 0;
    }
    return Results.Ok(new
    {
        state = "ok", note = "scenario_not_applied", asOf,
        baseCounts = new { high = diffs.Count(d => d.baseLevel == "high"), medium = diffs.Count(d => d.baseLevel == "medium"), low = diffs.Count(d => d.baseLevel == "low"), no_assessment = diffs.Count(d => d.baseLevel == "no_assessment") },
        scenCounts = new { high = diffs.Count(d => d.scenLevel == "high"), medium = diffs.Count(d => d.scenLevel == "medium"), low = diffs.Count(d => d.scenLevel == "low"), no_assessment = diffs.Count(d => d.scenLevel == "no_assessment") },
        moneyBase = new { at_risk_total = rbA, renewal_cost = rbR, uncovered = rbU },
        moneyScen = new { at_risk_total = rsA },
        noEstimateCount = noEstimate,             // accounts with no score on at least one side
        coveredCount = diffs.Count(d => d.scoreDelta is not null),  // assessed on both sides
        changed, baseModelVersion = baseSettings.ModelVersion,
        snapshotVersion = snapRow.SnapshotVersion,
    });
}).RequireAuthorization("AnalystOrAdmin").RequireRateLimiting("analytics");

// --- /metrics: Prometheus exposition from the latest PUBLISHED batch --------
// Values all come from ONE eval run — no recalculation on scrape.
// Access: cookie session OR a dedicated scrape token (env Scraping__Token)
// sent as `Authorization: Bearer <token>` — a narrow credential that reaches
// only /metrics, never the customer APIs.
var scrapeToken = builder.Configuration["Scraping:Token"]
    ?? Environment.GetEnvironmentVariable("Scraping__Token");
bool ScrapeAllowed(HttpContext ctx) =>
    ctx.User.Identity?.IsAuthenticated == true
    || (!string.IsNullOrEmpty(scrapeToken)
        && string.Equals(ctx.Request.Headers.Authorization.ToString(),
            $"Bearer {scrapeToken}", StringComparison.Ordinal));
app.MapGet("/metrics", async (HttpContext http, AppDb db) =>
{
    if (!ScrapeAllowed(http)) return Results.Unauthorized();
    var run = await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync();
    var sb = new System.Text.StringBuilder();
    sb.AppendLine("# HELP churn_eval_data_present Whether an evaluation batch exists (1=yes,0=no).");
    sb.AppendLine("# TYPE churn_eval_data_present gauge");
    if (run is null)
    {
        sb.AppendLine("churn_eval_data_present 0");
        return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
    }
    var row = await db.SummaryResults.FirstOrDefaultAsync(x => x.EvaluationId == run.EvaluationId);
    var sum = row is null ? null : JsonSerializer.Deserialize<SummaryDto>(row.Payload, Jopts);
    sb.AppendLine("churn_eval_data_present 1");
    sb.AppendLine("# HELP churn_eval_last_success_seconds Unix seconds of last successful evaluation.");
    sb.AppendLine("# TYPE churn_eval_last_success_seconds gauge");
    sb.AppendLine($"churn_eval_last_success_seconds {(long)run.AsOf.ToUnixTimeSeconds()}");
    if (sum is not null)
    {
        var c = sum.Counts;
        sb.AppendLine("# HELP churn_customers_by_risk_level Customers count per risk level.");
        sb.AppendLine("# TYPE churn_customers_by_risk_level gauge");
        sb.AppendLine($"churn_customers_by_risk_level{{level=\"high\"}} {c.High}");
        sb.AppendLine($"churn_customers_by_risk_level{{level=\"medium\"}} {c.Medium}");
        sb.AppendLine($"churn_customers_by_risk_level{{level=\"low\"}} {c.Low}");
        sb.AppendLine($"churn_customers_by_risk_level{{level=\"no_assessment\"}} {c.NoAssessment}");
        var m = sum.Money;
        sb.AppendLine("# HELP churn_at_risk_amount_eur_cents Scenario at-risk amount (heuristic index, EUR cents).");
        sb.AppendLine("# TYPE churn_at_risk_amount_eur_cents gauge");
        sb.AppendLine($"churn_at_risk_amount_eur_cents{{unit=\"EUR_cents\",semantic=\"{m.Semantic}\"}} {(m.AtRiskTotal ?? 0)}");
        sb.AppendLine("# HELP churn_money_totals_eur_cents Money breakdown totals in EUR cents.");
        sb.AppendLine("# TYPE churn_money_totals_eur_cents gauge");
        sb.AppendLine($"churn_money_totals_eur_cents{{part=\"renewal_cost\"}} {m.RenewalCostTotal}");
        sb.AppendLine($"churn_money_totals_eur_cents{{part=\"available_fund\"}} {m.AvailableFundTotal}");
        sb.AppendLine($"churn_money_totals_eur_cents{{part=\"uncovered\"}} {m.UncoveredTotal}");
    }
    return Results.Text(sb.ToString(), "text/plain; version=0.0.4");
});

// --- Swagger: OpenAPI spec + Swagger UI, both behind the same auth ----------
// Guard the whole docs surface (UI shell AND spec JSON) with cookie-auth —
// UseSwagger/UseSwaggerUI run after this middleware, so an anonymous request
// is rejected before either serves a byte.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/v1/docs", StringComparison.OrdinalIgnoreCase) &&
        ctx.User.Identity?.IsAuthenticated != true)
    { ctx.Response.StatusCode = 401; return; }
    await next();
});
app.UseSwagger(c => { c.RouteTemplate = "api/v1/docs/{documentName}.json"; });
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/api/v1/docs/v1.json", "Churn Risk Analyzer API v1");
    c.RoutePrefix = "api/v1/docs";
});
app.MapGet("/api/v1/docs", () => Results.Redirect("/api/v1/docs/index.html")).RequireAuthorization();

app.MapRazorPages();
app.MapGet("/", () => Results.Redirect("/customers"));

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program { }

// Minimal readiness check: can we open the configured PostgreSQL connection.
// Avoids leaking diagnostics; returns only Healthy/Unhealthy.
internal sealed class DbReadyCheck(IServiceProvider sp) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await using var scope = sp.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy();
        }
        catch { return HealthCheckResult.Unhealthy(); }
    }
}
