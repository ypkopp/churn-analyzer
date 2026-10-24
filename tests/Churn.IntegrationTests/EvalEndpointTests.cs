using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Churn.IntegrationTests;

/// <summary>
/// A07: evaluation-driven endpoints against real PostgreSQL. Verifies a single
/// published batch backs list/summary, optimistic concurrency on settings, and
/// the explicit no_assessment / no_data states.
/// </summary>
public class EvalEndpointTests : IClassFixture<PgAppFactory>
{
    private readonly PgAppFactory _f;
    public EvalEndpointTests(PgAppFactory f) => _f = f;

    private HttpClient Client() =>
        _f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task<string> Csrf(HttpClient c) =>
        (await c.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/auth/csrf"))!["token"];

    private static HttpRequestMessage Mut(HttpMethod m, string url, string csrf, object body) =>
        new(m, url) { Content = JsonContent.Create(body), Headers = { { "X-CSRF-TOKEN", csrf } } };

    private static async Task Login(HttpClient c)
    {
        var t = await Csrf(c);
        await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/login", t,
            new { login = "admin", password = "test-admin-pw-123" }));
    }

    private static object Snap() => new
    {
        accounts = new[] { new { id = 1, email = "a@x.test", balance = 0, withdrawBalance = 0, totalReplenished = 0, bonusBalance = 0, bonusState = "locked" },
                            new { id = 2, email = "b@x.test", balance = 0, withdrawBalance = 0, totalReplenished = 0, bonusBalance = 0, bonusState = "locked" } },
        services = new[] { new { id = 1, name = "s1", ownerId = 1, paymentTerm = "month", autoProlong = false, createdAt = "2026-10-01T00:00:00Z", expiresAt = "2026-10-25T00:00:00Z", status = "active", price = 500 },
                           new { id = 2, name = "s2", ownerId = 2, paymentTerm = "month", autoProlong = false, createdAt = "2026-10-01T00:00:00Z", expiresAt = "2026-10-25T00:00:00Z", status = "deleted", price = 500 } },
        transactions = new object[] { },
    };

    [Fact]
    public async Task Snapshot_drives_real_assessments()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        var put = await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, Snap()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var list = await c.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/customers");
        Assert.True(list!.ContainsKey("evaluationId"));
    }

    [Fact]
    public async Task List_and_summary_share_batch_identity()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, Snap()));
        var list = await c.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/customers");
        var summ = await c.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/analytics/summary");
        Assert.Equal(list!["evaluationId"].ToString(), summ!["evaluationId"].ToString());
        Assert.Equal(list["snapshotVersion"].ToString(), summ["snapshotVersion"].ToString());
        Assert.Equal(list["modelVersion"].ToString(), summ["modelVersion"].ToString());
    }

    [Fact]
    public async Task No_assessment_is_explicit_not_zero()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, Snap()));
        var list = await c.GetFromJsonAsync<System.Text.Json.JsonDocument>("/api/v1/customers");
        var hasNoAssess = false;
        foreach (var item in list!.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.GetProperty("noAssessment").GetBoolean())
            {
                hasNoAssess = true;
                Assert.Equal(System.Text.Json.JsonValueKind.Null, item.GetProperty("riskScore").ValueKind);
            }
        }
        Assert.True(hasNoAssess, "expected at least one no_assessment (deleted service)");
    }

    [Fact]
    public async Task Settings_conflict_returns_409()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        // set a baseline settings first
        await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t,
            new { expectedVersion = (int?)null, settings = new { modelVersion = 0, horizonDays = 14, weights = new Dictionary<string, int>(), thresholds = new Dictionary<string, int>() } }));
        var stale = await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t,
            new { expectedVersion = 999, settings = new { modelVersion = 0, horizonDays = 14, weights = new Dictionary<string, int>(), thresholds = new Dictionary<string, int>() } }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Coefficient_change_recreates_batch()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, Snap()));
        var before = await c.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/analytics/summary");
        var r = await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t,
            new { expectedVersion = (int?)null, settings = new { modelVersion = 0, horizonDays = 14, weights = new Dictionary<string, int> { ["expiring_no_autoprolong"] = 90 }, thresholds = new Dictionary<string, int>() } }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var after = await c.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/analytics/summary");
        Assert.NotEqual(before!["evaluationId"].ToString(), after!["evaluationId"].ToString());
    }
}
