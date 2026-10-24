using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Churn.Web.Data;

/// <summary>
/// Atomic snapshot + settings store. Snapshot replacement is a single DB
/// transaction: validate-then-insert-new-version; an invalid snapshot is
/// rejected BEFORE any write, so the last good snapshot stays intact.
/// Settings are validated before save and are never overwritten by defaults.
/// </summary>
public sealed class SnapshotStore(AppDb db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);

    public async Task<long?> CurrentSnapshotVersionAsync(CancellationToken ct = default) =>
        await db.Snapshots.MaxAsync(s => (long?)s.SnapshotVersion, ct);

    public async Task<SnapshotRow?> CurrentSnapshotAsync(CancellationToken ct = default) =>
        await db.Snapshots.OrderByDescending(s => s.SnapshotVersion).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Atomic replace: runs validation first; on success writes a NEW row with
    /// snapshotVersion = prev+1 inside one transaction. Returns new version, or
    /// null when validation fails (old snapshot untouched).
    /// </summary>
    public async Task<(bool ok, long? version, string? error)> ReplaceAsync(
        SourceSnapshotDto snap, CancellationToken ct = default)
    {
        var error = Validate(snap);
        if (error is not null) return (false, null, error);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var prev = await CurrentSnapshotVersionAsync(ct) ?? 0;
        var row = new SnapshotRow
        {
            SnapshotVersion = prev + 1,
            Payload = JsonSerializer.Serialize(snap, J),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Snapshots.Add(row);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (true, row.SnapshotVersion, null);
    }

    /// <summary>Referential + format validation. Rejects the whole snapshot.</summary>
    private static string? Validate(SourceSnapshotDto s)
    {
        var accIds = s.Accounts.Select(a => a.Id).ToHashSet();
        if (accIds.Count != s.Accounts.Count) return "duplicate account ids";
        var svcIds = s.Services.Select(v => v.Id).ToHashSet();
        if (svcIds.Count != s.Services.Count) return "duplicate service ids";
        if (s.Services.Any(v => !accIds.Contains(v.OwnerId))) return "service ownerId missing";
        if (s.Transactions.Any(t => !svcIds.Contains(t.ServiceId))) return "transaction serviceId missing";
        if (s.Transactions.Any(t => t.Status == "not_performed" && t.PerformedAt is not null))
            return "not_performed tx has performedAt";
        if (s.Transactions.Any(t => t.Status == "performed" && t.PerformedAt is null))
            return "performed tx missing performedAt";
        if (s.Services.Any(v => v.ExpiresAt < v.CreatedAt)) return "expiresAt < createdAt";
        return null;
    }

    public async Task<ModelSettingsRow?> CurrentSettingsAsync(CancellationToken ct = default) =>
        await db.ModelSettings.OrderByDescending(m => m.ModelVersion).FirstOrDefaultAsync(ct);

    /// <summary>Validate then persist a new settings row (new modelVersion).</summary>
    public async Task<(bool ok, ModelSettingsRow? row, string? error)> SaveSettingsAsync(
        ModelSettingsDto dto, CancellationToken ct = default)
    {
        if (dto.HorizonDays < 1 || dto.HorizonDays > 3660) return (false, null, "horizonDays out of range");
        if (dto.Weights.Any(w => w.Value < 0)) return (false, null, "negative weight");
        var prev = await CurrentSettingsAsync(ct);
        var row = new ModelSettingsRow
        {
            ModelVersion = (prev?.ModelVersion ?? 0) + 1,
            Payload = JsonSerializer.Serialize(dto, J),
            UpdatedAt = clock.GetUtcNow(),
        };
        db.ModelSettings.Add(row);
        await db.SaveChangesAsync(ct);
        return (true, row, null);
    }
}
