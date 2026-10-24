using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
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
    });
builder.Services.AddAuthorization(o =>
{
    // Endpoints that must never be reachable anonymously — cookie-auth spec surface.
    o.AddPolicy("AuthenticatedOnly", p => p.RequireAuthenticatedUser());
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

    // initial admin user from config (hashed), only if none exists
    if (!string.IsNullOrEmpty(adminPassword) && !await db.Users.AnyAsync(u => u.Login == adminLogin))
    {
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        var u = new AppUser { Login = adminLogin, CreatedAt = TimeProvider.System.GetUtcNow() };
        u.PasswordHash = hasher.HashPassword(u, adminPassword);
        db.Users.Add(u);
        await db.SaveChangesAsync();
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
    var ok = user is not null &&
        hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password)
            != PasswordVerificationResult.Failed;
    if (!ok) return Results.Unauthorized();   // don't reveal user existence
    var claims = new[] { new Claim(ClaimTypes.Name, user!.Login) };
    var id = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(id));
    return Results.NoContent();
}).RequireRateLimiting("login");

app.MapGet("/api/v1/auth/me", (ClaimsPrincipal p) =>
    p.Identity?.IsAuthenticated == true
        ? Results.Ok(new MeResponse(p.Identity.Name ?? ""))
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
    await db.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization();

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

// --- snapshot: atomic replace + version, then re-evaluate -------------------
app.MapPut("/api/v1/source/snapshot", async (SnapshotStore store,
    Churn.Web.Services.EvaluationService eval, SourceSnapshotDto snap) =>
{
    var (ok, version, error) = await store.ReplaceAsync(snap);
    if (!ok) return Results.BadRequest(Err("invalid_snapshot", error));
    var run = await eval.EvaluateAndPublishAsync();
    return Results.Ok(new { snapshotVersion = version, run?.EvaluationId, run?.AsOf });
}).RequireAuthorization();

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
}).RequireAuthorization();

// --- /metrics: Prometheus exposition from the latest PUBLISHED batch --------
// Values all come from ONE eval run — no recalculation on scrape.
app.MapGet("/metrics", async (AppDb db) =>
{
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
}).RequireAuthorization();

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
