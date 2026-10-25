using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Churn.IntegrationTests;

/// <summary>
/// P01 — unified input validation for PATCH /model/settings, PUT
/// /api/v1/source/snapshot and POST /api/v1/analytics/whatif, against a real
/// PostgreSQL container. Every negative case must return 400/4xx and leave the
/// source snapshot, stored settings and last published batch untouched.
/// The periodic refresher is disabled in the factory (REFRESH=0) so batch
/// counts are deterministic inside a test.
/// </summary>
public class InputValidationTests : IClassFixture<PgAppFactory>
{
    private readonly PgAppFactory _f;
    public InputValidationTests(PgAppFactory f) => _f = f;

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

    private static object Settings(int horizon = 14, Dictionary<string, int>? weights = null,
        Dictionary<string, int>? thresholds = null) => new
        {
            modelVersion = 0, horizonDays = horizon,
            weights = weights ?? new Dictionary<string, int>
                { ["expiring_no_autoprolong"] = 30, ["insufficient_funds"] = 25, ["prolong_wait"] = 40, ["suspended"] = 10 },
            thresholds = thresholds ?? new Dictionary<string, int> { ["high"] = 50, ["medium"] = 20 }
        };

    private static object Snapshot(int serviceId, int ownerId, long price = 500) => new
    {
        accounts = new[] { new { id = ownerId, email = "a@x.test", balance = 100, withdrawBalance = 0, totalReplenished = 0, bonusBalance = 0, bonusState = "locked" } },
        services = new[] { new { id = serviceId, name = "s1", ownerId, paymentTerm = "month", autoProlong = false, createdAt = "2026-10-01T00:00:00Z", expiresAt = "2026-11-01T00:00:00Z", status = "active", price } },
        transactions = new object[] { },
    };

    private async Task<long> BatchesCount(HttpClient c)
    {
        var r = await c.GetFromJsonAsync<List<Dictionary<string, object>>>("/api/v1/analytics/batches");
        return r!.Count;
    }
    private async Task<int?> ModelVersion(HttpClient c)
    {
        var r = await c.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/model/settings");
        return r!.TryGetValue("modelVersion", out var v) ? Convert.ToInt32(v) : null;
    }
    private async Task<long?> SnapshotVersion(HttpClient c)
    {
        var r = await c.GetFromJsonAsync<Dictionary<string, object>>("/api/v1/status");
        return r!.TryGetValue("snapshotVersion", out var v) && v is not null ? Convert.ToInt64(v) : null;
    }

    // ---------- model settings: every invalid case must not write ----------
    [Theory]
    [InlineData("null_settings", null)]
    [InlineData("horizon_0", 0)]
    [InlineData("horizon_3661", 3661)]
    [InlineData("horizon_negative", -5)]
    public async Task Invalid_settings_are_rejected_and_unchanged(string kind, object? horizon)
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        var mvBefore = await ModelVersion(c);
        var body = kind == "null_settings"
            ? (object)new { expectedVersion = (int?)null, settings = (object?)null }
            : (object)new { expectedVersion = (int?)null, settings = Settings((int)(horizon ?? 14)) };
        var r = await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t, body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(mvBefore, await ModelVersion(c));   // no write
    }

    public static IEnumerable<object[]> BadSettings()
    {
        yield return new object[] { "unknown_weight", Settings(weights: new() { ["expiring_no_autoprolong"] = 30, ["bogus_key"] = 5 }) };
        yield return new object[] { "unknown_threshold", Settings(thresholds: new() { ["high"] = 50, ["bogus"] = 9 }) };
        yield return new object[] { "negative_weight", Settings(weights: new() { ["expiring_no_autoprolong"] = -1 }) };
        yield return new object[] { "all_zero", Settings(weights: new() { ["a"] = 0, ["b"] = 0 }) };
        yield return new object[] { "overflow", Settings(weights: new() { ["expiring_no_autoprolong"] = int.MaxValue, ["insufficient_funds"] = int.MaxValue }) };
        yield return new object[] { "threshold_101", Settings(thresholds: new() { ["high"] = 101 }) };
        yield return new object[] { "threshold_negative", Settings(thresholds: new() { ["medium"] = -1 }) };
        yield return new object[] { "medium_gte_high", Settings(thresholds: new() { ["high"] = 50, ["medium"] = 50 }) };
        yield return new object[] { "null_weights", new { modelVersion = 0, horizonDays = 14, weights = (Dictionary<string, int>?)null, thresholds = new Dictionary<string, int>() } };
        yield return new object[] { "null_thresholds", new { modelVersion = 0, horizonDays = 14, weights = new Dictionary<string, int>(), thresholds = (Dictionary<string, int>?)null } };
    }

    [Theory]
    [MemberData(nameof(BadSettings))]
    public async Task Bad_settings_payloads_are_rejected(string _, object settings)
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        var before = await ModelVersion(c);
        var r = await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t,
            new { expectedVersion = (int?)null, settings }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.Contains("invalid_settings", body);
        Assert.Equal(before, await ModelVersion(c));
    }

    [Fact]
    public async Task Valid_settings_still_persist()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        var r = await c.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t,
            new { expectedVersion = (int?)null, settings = Settings(21, new() { ["prolong_wait"] = 55 }) }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // ---------- what-if: same validator, no writes -------------------------
    public static IEnumerable<object[]> BadWhatIf()
    {
        yield return new object[] { "negative", new { settings = new { modelVersion = 0, horizonDays = 14, weights = new Dictionary<string, int> { ["expiring_no_autoprolong"] = -3 }, thresholds = new Dictionary<string, int>() } } };
        yield return new object[] { "horizon0", new { settings = new { modelVersion = 0, horizonDays = 0, weights = new Dictionary<string, int>(), thresholds = new Dictionary<string, int>() } } };
        yield return new object[] { "unknown", new { settings = new { modelVersion = 0, horizonDays = 14, weights = new Dictionary<string, int> { ["zzz"] = 1 }, thresholds = new Dictionary<string, int>() } } };
    }

    [Theory]
    [MemberData(nameof(BadWhatIf))]
    public async Task Bad_whatif_is_400_and_writes_nothing(string _, object body)
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        var batches = await BatchesCount(c); var mv = await ModelVersion(c); var sv = await SnapshotVersion(c);
        var r = await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/analytics/whatif", t, body));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(batches, await BatchesCount(c));
        Assert.Equal(mv, await ModelVersion(c));
        Assert.Equal(sv, await SnapshotVersion(c));
    }

    [Fact]
    public async Task Whatif_without_settings_is_baseline_only_not_a_write()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        var batches = await BatchesCount(c); var mv = await ModelVersion(c);
        var r = await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/analytics/whatif", t, new { settings = (object?)null }));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var j = await r.Content.ReadAsStringAsync();
        Assert.Contains("scenario_not_applied", j);
        Assert.Equal(batches, await BatchesCount(c));
        Assert.Equal(mv, await ModelVersion(c));
    }

    // ---------- snapshot: every invalid case rejected whole ----------------
    public static IEnumerable<object[]> BadSnapshots()
    {
        yield return new object[] { "null_accounts", new { accounts = (object[]?)null, services = new object[] { }, transactions = new object[] { } } };
        yield return new object[] { "dup_tx", new { accounts = new object[] { }, services = new object[] { }, transactions = new[] { new { id = 1, amount = 1, status = "performed", performedAt = "2026-10-01T00:00:00Z", createdAt = "2026-10-01T00:00:00Z", type = "order", serviceId = 1 }, new { id = 1, amount = 1, status = "performed", performedAt = "2026-10-01T00:00:00Z", createdAt = "2026-10-01T00:00:00Z", type = "order", serviceId = 1 } } } };
        yield return new object[] { "unknown_status", new { accounts = new object[] { }, services = new[] { new { id = 1, name = "s", ownerId = 1, paymentTerm = "month", autoProlong = false, createdAt = "2026-10-01T00:00:00Z", expiresAt = "2026-11-01T00:00:00Z", status = "bogus", price = 5 } }, transactions = new object[] { } } };
        yield return new object[] { "unknown_term", new { accounts = new object[] { }, services = new[] { new { id = 1, name = "s", ownerId = 1, paymentTerm = "decade", autoProlong = false, createdAt = "2026-10-01T00:00:00Z", expiresAt = "2026-11-01T00:00:00Z", status = "active", price = 5 } }, transactions = new object[] { } } };
        yield return new object[] { "negative_price", new { accounts = new object[] { }, services = new[] { new { id = 1, name = "s", ownerId = 1, paymentTerm = "month", autoProlong = false, createdAt = "2026-10-01T00:00:00Z", expiresAt = "2026-11-01T00:00:00Z", status = "active", price = -5 } }, transactions = new object[] { } } };
        yield return new object[] { "negative_balance", new { accounts = new[] { new { id = 1, email = "a@x.test", balance = -1, withdrawBalance = 0, totalReplenished = 0, bonusBalance = 0, bonusState = "locked" } }, services = new object[] { }, transactions = new object[] { } } };
        yield return new object[] { "bad_bonus", new { accounts = new[] { new { id = 1, email = "a@x.test", balance = 1, withdrawBalance = 0, totalReplenished = 0, bonusBalance = 0, bonusState = "bogus" } }, services = new object[] { }, transactions = new object[] { } } };
        yield return new object[] { "bad_tx_status", new { accounts = new object[] { }, services = new object[] { }, transactions = new[] { new { id = 1, amount = 1, status = "bogus", performedAt = "2026-10-01T00:00:00Z", createdAt = "2026-10-01T00:00:00Z", type = "order", serviceId = 1 } } } };
        yield return new object[] { "bad_tx_type", new { accounts = new object[] { }, services = new object[] { }, transactions = new[] { new { id = 1, amount = 1, status = "performed", performedAt = "2026-10-01T00:00:00Z", createdAt = "2026-10-01T00:00:00Z", type = "bogus", serviceId = 1 } } } };
    }

    [Theory]
    [MemberData(nameof(BadSnapshots))]
    public async Task Bad_snapshots_are_rejected_and_old_kept(string _, object snap)
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        // seed a valid snapshot first so 'kept' is observable
        var ok = await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, Snapshot(1, 1)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var svBefore = await SnapshotVersion(c);
        var r = await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, snap));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("invalid_snapshot", await r.Content.ReadAsStringAsync());
        Assert.Equal(svBefore, await SnapshotVersion(c));
    }

    [Fact]
    public async Task Valid_snapshot_still_passes_and_recreates_batch()
    {
        var c = Client(); await Login(c); var t = await Csrf(c);
        var before = await BatchesCount(c);
        var r = await c.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, Snapshot(9, 9)));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True(await BatchesCount(c) > before);
    }
}
