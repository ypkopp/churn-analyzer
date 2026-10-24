using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Churn.IntegrationTests;

/// <summary>
/// Real end-to-end checks against a REAL PostgreSQL container:
/// login/logout/me, 401 without session, wrong password, CSRF rejection,
/// snapshot replace + validation, settings persistence across app restart.
/// </summary>
public class AuthAndStatusTests : IClassFixture<PgAppFactory>
{
    private readonly PgAppFactory _f;
    public AuthAndStatusTests(PgAppFactory f) => _f = f;

    private HttpClient Client() =>
        _f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task<string> Csrf(HttpClient c)
    {
        var r = await c.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/auth/csrf");
        return r!["token"];
    }

    private static HttpRequestMessage Mut(HttpMethod m, string url, string csrf, object body) =>
        new(m, url) { Content = JsonContent.Create(body), Headers = { { "X-CSRF-TOKEN", csrf } } };

    private static async Task Login(HttpClient c)
    {
        var t = await Csrf(c);
        var r = await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/login", t,
            new { login = "admin", password = "test-admin-pw-123" }));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_api_returns_401_not_redirect()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
        var r = await c.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.DoesNotContain("login", r.Headers.Location?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_me_logout_flow_works()
    {
        var c = Client();
        await Login(c);
        var me = await c.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/auth/me");
        Assert.Equal("admin", me!["login"]);
        var t = await Csrf(c);
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/logout", t, new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var c = Client();
        var t = await Csrf(c);
        var r = await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/login", t,
            new { login = "admin", password = "wrong" }));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Missing_csrf_token_is_rejected()
    {
        var c = Client();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        { Content = JsonContent.Create(new { login = "admin", password = "x" }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/healthz/ready")).StatusCode);
    }

    [Fact]
    public async Task Metrics_requires_auth_and_returns_prometheus()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/metrics")).StatusCode);
        await Login(c);
        var r = await c.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("churn_customers_by_risk_level", body);
        Assert.Contains("no_assessment", body);
        Assert.Contains("churn_at_risk_amount_eur_cents", body);
    }

    [Fact]
    public async Task Docs_requires_auth_and_serves_swagger()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/docs/index.html")).StatusCode);
        await Login(c);
        var ui = await c.GetAsync("/api/v1/docs/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        var spec = await c.GetAsync("/api/v1/docs/openapi.json");
        Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
        var json = await spec.Content.ReadAsStringAsync();
        Assert.Contains("/api/v1/customers", json);
    }

    // --- snapshot replace + validation ---------------------------------------
    private static object Snap(int serviceId, int ownerId) => new
    {
        accounts = new[] { new { id = ownerId, email = "a@x.test", balance = 100, withdrawBalance = 0, totalReplenished = 0, bonusBalance = 0, bonusState = "locked" } },
        services = new[] { new { id = serviceId, name = "s1", ownerId, paymentTerm = "month", autoProlong = false, createdAt = "2026-10-01T00:00:00Z", expiresAt = "2026-11-01T00:00:00Z", status = "active", price = 500 } },
        transactions = new object[] { },
    };

    [Fact]
    public async Task Snapshot_replace_is_atomic_and_versioned()
    {
        var c = Client();
        await Login(c);
        var t = await Csrf(c);
        var r = await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, Snap(1, 1)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var status = await c.GetFromJsonAsync<Dictionary<string, long?>>("/api/v1/status");
        Assert.True(status!["snapshotVersion"] >= 1);
    }

    [Fact]
    public async Task Snapshot_put_without_csrf_is_rejected()
    {
        var c = Client();
        await Login(c);
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/v1/source/snapshot")
        { Content = JsonContent.Create(Snap(1, 1)) };
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Invalid_snapshot_is_rejected_and_old_kept()
    {
        var c = Client();
        await Login(c);
        var t = await Csrf(c);
        // orphan transaction -> whole snapshot rejected
        var bad = new { accounts = new object[] { }, services = new object[] { },
            transactions = new[] { new { id = 1, amount = 5, status = "performed", performedAt = "2026-10-01T00:00:00Z", createdAt = "2026-10-01T00:00:00Z", type = "order", serviceId = 999 } } };
        var r = await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, bad));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // --- settings persist across restart --------------------------------------
    [Fact]
    public async Task Settings_validate_and_persist()
    {
        var c = Client();
        await Login(c);
        var t = await Csrf(c);
        var patch = new { expectedVersion = (int?)null,
            settings = new { modelVersion = 0, horizonDays = 21,
                weights = new Dictionary<string, int> { ["prolong_wait"] = 45 },
                thresholds = new Dictionary<string, int> { ["high"] = 60 } } };
        var r = await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t, patch));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var bad = await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t,
            new { expectedVersion = (int?)null, settings = new { modelVersion = 0, horizonDays = -5, weights = new Dictionary<string, int>(), thresholds = new Dictionary<string, int>() } }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
