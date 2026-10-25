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
    public DbSet<RetentionTask> RetentionTasks => Set<RetentionTask>();
    public DbSet<TaskEvent> TaskEvents => Set<TaskEvent>();
    public DbSet<ContactMessage> ContactMessages => Set<ContactMessage>();
    public DbSet<SavedSegment> SavedSegments => Set<SavedSegment>();

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
            e.Property(x => x.Role).IsRequired().HasDefaultValue(UserRoles.Analyst);
            e.Property(x => x.Disabled).IsRequired();
            e.Property(x => x.SecurityStamp).IsRequired().HasDefaultValue("");
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
        b.Entity<RetentionTask>(e =>
        {
            e.ToTable("retention_tasks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).IsRequired();
            e.HasIndex(x => x.AccountId);
            e.HasIndex(x => x.AssigneeUserId);
            e.HasIndex(x => x.Status);
        });
        b.Entity<TaskEvent>(e =>
        {
            e.ToTable("task_events");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TaskId);
        });
        b.Entity<ContactMessage>(e =>
        {
            e.ToTable("contact_messages");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.AccountId);
        });
        b.Entity<SavedSegment>(e =>
        {
            e.ToTable("saved_segments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.OwnerUserId);
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
    /// <summary>"admin" | "analyst" — drives server-side authorization.</summary>
    public string Role { get; set; } = UserRoles.Analyst;
    /// <summary>True = cannot log in and existing sessions are rejected.</summary>
    public bool Disabled { get; set; }
    /// <summary>Bumped on password change / disable; embedded in the auth
    /// cookie. A request whose stamp is stale is signed out server-side.</summary>
    public string SecurityStamp { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public static class UserRoles
{
    public const string Admin = "admin";
    public const string Analyst = "analyst";
}

/// <summary>A retention task created from a customer card. Bound to the
/// evaluationId/asOf it was decided on so context is preserved. Never
/// deleted when the customer leaves a later snapshot — the record just
/// loses live customer data.</summary>
public sealed class RetentionTask
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public long AccountId { get; set; }
    public long AssigneeUserId { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    /// <summary>"high" | "normal" | "low".</summary>
    public string Priority { get; set; } = "normal";
    public string? Description { get; set; }
    /// <summary>"new" | "in_progress" | "done" | "cancelled".</summary>
    public string Status { get; set; } = "new";
    public Guid EvaluationId { get; set; }      // decision context
    public DateTimeOffset AsOf { get; set; }    // batch asOf at creation
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>Append-only status-change journal for a retention task.</summary>
public sealed class TaskEvent
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string? FromStatus { get; set; }
    public string ToStatus { get; set; } = "";
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; }
    public long ByUserId { get; set; }
}

public static class TaskStatuses
{
    public const string New = "new";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
}

/// <summary>A message draft / sent attempt for a customer. Status tracks
/// draft -> prepared-for-mailto -> sent-or-error; never conflated.</summary>
public sealed class ContactMessage
{
    public long Id { get; set; }
    public long AccountId { get; set; }
    public string Recipient { get; set; } = "";   // customer email at write time
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    /// <summary>"draft"|"prepared"|"sent"|"error".</summary>
    public string Status { get; set; } = "draft";
    public string? TransportNote { get; set; }
    public Guid EvaluationId { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A user's saved filter preset — parameters only, never a stale
/// snapshot of customers. Evaluated against the current batch at open.</summary>
public sealed class SavedSegment
{
    public long Id { get; set; }
    public long OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string? Query { get; set; }            // q / riskLevel / sort / dir
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
