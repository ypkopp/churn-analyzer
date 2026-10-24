using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Churn.Core;
using Churn.Web.Data;

namespace Churn.Web.Services;

/// <summary>
/// Synchronous evaluation coordinator. Runs Churn.Core over the current snapshot
/// + settings and publishes ONE consistent batch: eval_run + per-customer results
/// + summary in a single transaction. A failed run commits nothing, so the last
/// good published batch stays authoritative — never a partial set.
///
/// Every customer/customer{}/summary read for a batch shares evaluationId,
/// snapshotVersion, modelVersion and asOf. Time changes alone can produce a new
/// batch (new evaluationId, same versions) — the periodic refresher triggers
/// that, not each HTTP request.
/// </summary>
public sealed class EvaluationService(IServiceScopeFactory scopeFactory, TimeProvider clock)
{
    private static readonly JsonSerializerOptions J = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Evaluate the CURRENT snapshot+settings at clock.GetUtcNow() and publish
    /// the batch atomically. Returns the published run, or null when there is no
    /// snapshot yet. Concurrent callers are serialized; a newer version is never
    /// overwritten by a slower older one.
    /// </summary>
    public async Task<EvalRun?> EvaluateAndPublishAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var snapRow = await db.Snapshots.OrderByDescending(s => s.SnapshotVersion).FirstOrDefaultAsync(ct);
            if (snapRow is null) return null;                      // no data -> explicit empty state
            var settingsRow = await db.ModelSettings.OrderByDescending(m => m.ModelVersion).FirstOrDefaultAsync(ct);

            var snap = JsonSerializer.Deserialize<SourceSnapshotDto>(snapRow.Payload, J)!;
            var settings = ToCore(settingsRow) ?? ModelSettings.Baseline();
            var accounts = snap.Accounts.Select(ToCore).ToList();
            var services = snap.Services.Select(ToCore).ToList();
            var asOf = clock.GetUtcNow();
            var ctx = new EvaluationContext(asOf, settings.HorizonDays,
                snapRow.SnapshotVersion, settings.ModelVersion);

            // Run risk + money per account (pure, deterministic for this asOf).
            var batch = new List<(Account a, CustomerAssessment risk, MoneyAssessment money)>();
            foreach (var a in accounts)
            {
                var risk = RiskEngine.Evaluate(a, services, ctx, settings);
                var money = MoneyEngine.Evaluate(a, services, risk, ctx);
                batch.Add((a, risk, money));
            }

            var evaluationId = Guid.NewGuid();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var run = new EvalRun
            {
                EvaluationId = evaluationId, AsOf = asOf,
                SnapshotVersion = snapRow.SnapshotVersion,
                ModelVersion = settings.ModelVersion,
                CreatedAt = asOf,
            };
            db.EvalRuns.Add(run);

            foreach (var (a, risk, money) in batch)
            {
                db.CustomerResults.Add(new CustomerResult
                {
                    EvaluationId = evaluationId, AccountId = a.Id,
                    Payload = JsonSerializer.Serialize(ToDto(a, risk, money, run), J),
                    CreatedAt = asOf,
                });
            }
            db.SummaryResults.Add(new SummaryResult
            {
                EvaluationId = evaluationId,
                Payload = JsonSerializer.Serialize(ToSummary(batch, run, settings.HorizonDays), J),
                CreatedAt = asOf,
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return run;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Latest published run (results already consistent for it).</summary>
    public async Task<EvalRun?> LatestRunAsync(CancellationToken ct = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        return await db.EvalRuns.OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
    }

    // --- mapping ------------------------------------------------------------
    private static ModelSettings? ToCore(ModelSettingsRow? row)
    {
        if (row is null) return null;
        var d = JsonSerializer.Deserialize<ModelSettingsDto>(row.Payload,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        int W(string k, int def) => d.Weights.TryGetValue(k, out var v) ? v : def;
        int T(string k, int def) => d.Thresholds.TryGetValue(k, out var v) ? v : def;
        return new ModelSettings(d.ModelVersion == 0 ? row.ModelVersion : d.ModelVersion,
            d.HorizonDays,
            W("expiring_no_autoprolong", 30), W("insufficient_funds", 25),
            W("prolong_wait", 40), W("suspended", 10),
            T("high", 50), T("medium", 20));
    }

    private static Account ToCore(AccountDto a) => new(a.Id, a.Email, a.Balance,
        a.WithdrawBalance, a.TotalReplenished, a.BonusBalance,
        a.BonusState == "unlocked" ? BonusState.Unlocked : BonusState.Locked);

    private static Service ToCore(ServiceDto s) => new(s.Id, s.Name, s.OwnerId,
        ParseTerm(s.PaymentTerm), s.AutoProlong, s.CreatedAt, s.ExpiresAt,
        ParseStatus(s.Status), s.Price);

    private static PaymentTerm ParseTerm(string s) => s switch
    {
        "hour" => PaymentTerm.Hour, "week" => PaymentTerm.Week, "month" => PaymentTerm.Month,
        "quarter_year" => PaymentTerm.QuarterYear, "half_year" => PaymentTerm.HalfYear,
        "year" => PaymentTerm.Year, _ => PaymentTerm.Month
    };
    private static ServiceStatus ParseStatus(string s) => s switch
    {
        "active" => ServiceStatus.Active, "suspended" => ServiceStatus.Suspended,
        "prolong_wait" => ServiceStatus.ProlongWait, "blocked" => ServiceStatus.Blocked,
        "deleted" => ServiceStatus.Deleted, _ => ServiceStatus.Active
    };

    private static CustomerResultDto ToDto(Account a, CustomerAssessment r,
        MoneyAssessment m, EvalRun run)
    {
        var rank = new CustomerRankDto(a.Id, a.Email, a.Balance, a.TotalReplenished,
            r.NoAssessment ? null : r.Score, LevelName(r), r.ReasonCodes,
            RiskEngine.LoyaltyIndicator(r) < 0 ? null : RiskEngine.LoyaltyIndicator(r),
            r.NoAssessment);
        var money = new MoneyDto("EUR_cents", m.RenewalCostInHorizon, m.AvailableFund,
            m.UncoveredAmount, m.AtRiskAmount, m.Semantic,
            m.Lines.Select(l => new MoneyLineDto(l.ServiceId, l.Term.ToString(),
                l.Status.ToString(), l.PriceCents, l.DueAt, l.DueAt < run.AsOf)).ToList(),
            m.HorizonDays);
        var detail = new CustomerDetailDto(a.Id, a.Email, a.Balance, a.WithdrawBalance,
            a.TotalReplenished, a.BonusBalance, a.BonusState.ToString().ToLowerInvariant(),
            r.NoAssessment ? null : r.Score, LevelName(r), r.ReasonCodes,
            r.NoAssessment ? null : RiskEngine.LoyaltyIndicator(r), r.NoAssessment,
            r.Contributions.Select(c => new FeatureDto(c.Code, c.Detail, c.Weight)).ToList(),
            r.Warnings, Array.Empty<ServiceLineDto>(),
            money,
            "derived_indicator: 100-score, not a measured trait");
        return new CustomerResultDto(rank, detail, run.SnapshotVersion,
            run.ModelVersion, run.EvaluationId, run.AsOf);
    }

    private static string LevelName(CustomerAssessment r) => r.NoAssessment
        ? "no_assessment"
        : r.Level.ToString().ToLowerInvariant();

    private static SummaryDto ToSummary(
        List<(Account a, CustomerAssessment risk, MoneyAssessment money)> batch,
        EvalRun run, int horizonDays)
    {
        var counts = new CountsByLevel(
            batch.Count(x => x.risk.Level == RiskLevel.High),
            batch.Count(x => x.risk.Level == RiskLevel.Medium),
            batch.Count(x => x.risk.Level == RiskLevel.Low),
            batch.Count(x => x.risk.NoAssessment));
        long renewal = 0, fund = 0, uncovered = 0, atRisk = 0;
        foreach (var (_, _, money) in batch)
        {
            renewal += money.RenewalCostInHorizon;
            fund += money.AvailableFund;
            uncovered += money.UncoveredAmount;
            if (money.AtRiskAmount.HasValue) atRisk += money.AtRiskAmount.Value;
        }
        var moneySummary = new MoneySummary("EUR_cents", renewal, fund, uncovered,
            atRisk, "heuristic_money_index", horizonDays);
        return new SummaryDto(counts, moneySummary, run.SnapshotVersion,
            run.ModelVersion, run.EvaluationId, run.AsOf);
    }
}
