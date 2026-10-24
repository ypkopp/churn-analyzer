using Microsoft.EntityFrameworkCore;

namespace Churn.Web.Data;

/// <summary>
/// EF Core context for the app. Stores the atomic source snapshot (JSONB),
/// model settings, versions and users. PostgreSQL only — no InMemory/SQLite.
/// </summary>
public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<SnapshotRow> Snapshots => Set<SnapshotRow>();
    public DbSet<ModelSettingsRow> ModelSettings => Set<ModelSettingsRow>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();
    public DbSet<CustomerResult> CustomerResults => Set<CustomerResult>();
    public DbSet<SummaryResult> SummaryResults => Set<SummaryResult>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<SnapshotRow>(e =>
        {
            e.ToTable("snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.SnapshotVersion).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.CreatedAt).IsRequired();
            e.HasIndex(x => x.SnapshotVersion).IsUnique();
        });
        b.Entity<ModelSettingsRow>(e =>
        {
            e.ToTable("model_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.ModelVersion).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.UpdatedAt).IsRequired();
        });
        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Login).IsUnique();
            e.Property(x => x.Login).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired();
        });
        b.Entity<EvalRun>(e =>
        {
            e.ToTable("eval_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.EvaluationId).IsRequired();
            e.HasIndex(x => x.EvaluationId).IsUnique();
            e.Property(x => x.AsOf).IsRequired();
            e.Property(x => x.SnapshotVersion).IsRequired();
            e.Property(x => x.ModelVersion).IsRequired();
        });
        b.Entity<CustomerResult>(e =>
        {
            e.ToTable("customer_results");
            e.HasKey(x => x.Id);
            e.Property(x => x.EvaluationId).IsRequired();
            e.HasIndex(x => new { x.EvaluationId, x.AccountId }).IsUnique();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        });
        b.Entity<SummaryResult>(e =>
        {
            e.ToTable("summary_results");
            e.HasKey(x => x.Id);
            e.Property(x => x.EvaluationId).IsRequired();
            e.HasIndex(x => x.EvaluationId).IsUnique();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        });
    }
}

public sealed class SnapshotRow
{
    public long Id { get; set; }
    public long SnapshotVersion { get; set; }
    public string Payload { get; set; } = "";   // serialized SourceSnapshot jsonb
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ModelSettingsRow
{
    public long Id { get; set; }
    public int ModelVersion { get; set; }
    public string Payload { get; set; } = "";   // serialized ModelSettings jsonb
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppUser
{
    public long Id { get; set; }
    public string Login { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One evaluation batch: all results share evaluationId+asOf+versions.</summary>
public sealed class EvalRun
{
    public long Id { get; set; }
    public Guid EvaluationId { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public long SnapshotVersion { get; set; }
    public int ModelVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
