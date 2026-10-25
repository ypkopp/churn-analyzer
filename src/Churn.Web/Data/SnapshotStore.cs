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

    /// <summary>Referential + format validation. Rejects the whole snapshot —
    /// delegates to the shared non-writing validator so PATCH/PUT/whatif all
    /// enforce the same contract.</summary>
    private static string? Validate(SourceSnapshotDto s) => InputValidator.Snapshot(s);

    public async Task<ModelSettingsRow?> CurrentSettingsAsync(CancellationToken ct = default) =>
        await db.ModelSettings.OrderByDescending(m => m.ModelVersion).FirstOrDefaultAsync(ct);

    /// <summary>Validate then persist a new settings row (new modelVersion).</summary>
    public async Task<(bool ok, ModelSettingsRow? row, string? error)> SaveSettingsAsync(
        ModelSettingsDto dto, CancellationToken ct = default)
    {
        var invalid = InputValidator.ModelSettings(dto);
        if (invalid is not null) return (false, null, invalid);
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
