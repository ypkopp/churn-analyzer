using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Testcontainers.PostgreSql;

namespace Churn.IntegrationTests;

/// <summary>
/// Boots the real app against a throwaway REAL PostgreSQL container
/// (Testcontainers) — not InMemory/SQLite. Each test class gets its own DB.
/// </summary>
public class PgAppFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .WithDatabase("churn_test")
        .WithUsername("churn")
        .WithPassword("test_pw")
        .Build();

    public string ConnStr => _pg.GetConnectionString();

    public async Task InitializeAsync() => await _pg.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        var conn = _pg.GetConnectionString();
        var keys = Path.Combine(Path.GetTempPath(), "churn-dpkeys-" + Guid.NewGuid().ToString("N"));
        // UseSetting is applied before the minimal-host entry point evaluates its
        // bootstrap configuration (DB migrations, admin creation, rate limiter).
        builder.UseSetting("ConnectionStrings:Db", conn);
        builder.UseSetting("Auth:AdminLogin", "admin");
        builder.UseSetting("Auth:AdminPassword", "test-admin-pw-123");
        builder.UseSetting("DataProtection:KeysPath", keys);
        builder.UseSetting("Https:Enabled", "false");
        builder.UseSetting("RateLimit:LoginPermit", "1000");
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Db"] = conn,
                ["Auth:AdminLogin"] = "admin",
                ["Auth:AdminPassword"] = "test-admin-pw-123",
                ["DataProtection:KeysPath"] = keys,
                ["Https:Enabled"] = "false",
                ["RateLimit:LoginPermit"] = "1000",
            });
        });
    }

    public new async Task DisposeAsync() => await _pg.DisposeAsync();
}
