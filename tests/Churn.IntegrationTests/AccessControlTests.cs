using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Churn.IntegrationTests;

/// <summary>
/// ACCESS: role-based access and server-side session revocation against a real
/// PostgreSQL container. Covers admin/analyst authorization, self-escalation
/// guard, password rotation, and stale-session rejection after change/disable.
/// </summary>
public class AccessControlTests : IClassFixture<PgAppFactory>
{
    private readonly PgAppFactory _f;
    public AccessControlTests(PgAppFactory f) => _f = f;

    private HttpClient Client() =>
        _f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    private static async Task<string> Csrf(HttpClient c) =>
        (await c.GetFromJsonAsync<Dictionary<string, string>>("/api/v1/auth/csrf"))!["token"];

    private static HttpRequestMessage Mut(HttpMethod m, string url, string csrf, object body) =>
        new(m, url) { Content = JsonContent.Create(body), Headers = { { "X-CSRF-TOKEN", csrf } } };

    private static async Task Login(HttpClient c, string user, string pw)
    {
        var t = await Csrf(c);
        var r = await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/login", t, new { login = user, password = pw }));
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    private static async Task<string> CreateUser(HttpClient adminClient, string login, string role)
    {
        var t = await Csrf(adminClient);
        var r = await adminClient.SendAsync(Mut(HttpMethod.Post, "/api/v1/admin/users", t,
            new { login, password = "pw-" + login + "-123", role }));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return "pw-" + login + "-123";
    }

    [Fact]
    public async Task Analyst_cannot_manage_users_or_replace_snapshot()
    {
        var admin = Client(); await Login(admin, "admin", "test-admin-pw-123");
        var pw = await CreateUser(admin, "analyst1", "analyst");

        var an = Client(); await Login(an, "analyst1", pw);
        var t = await Csrf(an);
        // admin endpoint forbidden for analyst
        Assert.Equal(HttpStatusCode.Forbidden,
            (await an.GetAsync("/api/v1/admin/users")).StatusCode);
        // snapshot replace is admin-only
        var snap = new { accounts = new object[] { }, services = new object[] { }, transactions = new object[] { } };
        Assert.Equal(HttpStatusCode.Forbidden,
            (await an.SendAsync(Mut(HttpMethod.Put, "/api/v1/source/snapshot", t, snap))).StatusCode);
        // but settings PATCH is allowed for analyst
        var ok = await an.SendAsync(Mut(HttpMethod.Patch, "/api/v1/model/settings", t,
            new { expectedVersion = (int?)null, settings = new { modelVersion = 0, horizonDays = 14, weights = new Dictionary<string, int>(), thresholds = new Dictionary<string, int>() } }));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Admin_cannot_disable_own_account()
    {
        var admin = Client(); await Login(admin, "admin", "test-admin-pw-123");
        var users = await admin.GetFromJsonAsync<List<Dictionary<string, object>>>("/api/v1/admin/users");
        var me = users!.First(u => u["login"].ToString() == "admin");
        var id = System.Text.Json.JsonSerializer.SerializeToElement(me["id"]).GetInt64();
        var t = await Csrf(admin);
        var r = await admin.SendAsync(Mut(HttpMethod.Patch, $"/api/v1/admin/users/{id}/status", t, new { disabled = true }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Disabled_user_cannot_login_and_old_session_is_rejected()
    {
        var admin = Client(); await Login(admin, "admin", "test-admin-pw-123");
        var pw = await CreateUser(admin, "victim1", "analyst");

        var v = Client(); await Login(v, "victim1", pw);
        Assert.Equal(HttpStatusCode.OK, (await v.GetAsync("/api/v1/auth/me")).StatusCode);

        // disable the victim — bumps the security stamp
        var users = await admin.GetFromJsonAsync<List<Dictionary<string, object>>>("/api/v1/admin/users");
        var id = System.Text.Json.JsonSerializer.SerializeToElement(
            users!.First(u => u["login"].ToString() == "victim1")["id"]).GetInt64();
        var t = await Csrf(admin);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.SendAsync(Mut(HttpMethod.Patch, $"/api/v1/admin/users/{id}/status", t, new { disabled = true }))).StatusCode);

        // victim's existing cookie is now rejected server-side, not just cleared
        Assert.Equal(HttpStatusCode.Unauthorized, (await v.GetAsync("/api/v1/auth/me")).StatusCode);
        // and they cannot log back in
        var v2 = Client();
        var tt = await Csrf(v2);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await v2.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/login", tt,
                new { login = "victim1", password = pw }))).StatusCode);
    }

    [Fact]
    public async Task Password_rotation_invalidates_old_session_and_accepts_new()
    {
        var admin = Client(); await Login(admin, "admin", "test-admin-pw-123");
        var pw = await CreateUser(admin, "rot1", "analyst");

        var c = Client(); await Login(c, "rot1", pw);
        var t = await Csrf(c);
        var rot = await c.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/rotate-password", t,
            new { currentPassword = pw, newPassword = "new-rot1-pw-123" }));
        Assert.Equal(HttpStatusCode.NoContent, rot.StatusCode);

        // the session issued before rotation is rejected on the next call
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/v1/auth/me")).StatusCode);
        // old password fails, new succeeds
        var old = Client(); var ot = await Csrf(old);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await old.SendAsync(Mut(HttpMethod.Post, "/api/v1/auth/login", ot,
                new { login = "rot1", password = pw }))).StatusCode);
        var nw = Client(); await Login(nw, "rot1", "new-rot1-pw-123");
        Assert.Equal(HttpStatusCode.OK, (await nw.GetAsync("/api/v1/auth/me")).StatusCode);
    }
}
