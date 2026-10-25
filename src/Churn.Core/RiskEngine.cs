namespace Churn.Core;

/// <summary>
/// Pure risk-scoring engine. No DB, no network, no clock access — time arrives
/// only via EvaluationContext.AsOf. Score is a bounded priority index, not a
/// probability. The "loyalty" figure is a derived indicator (100 - score), not a
/// measured client trait. Money estimate is intentionally NOT produced here.
/// </summary>
public static class RiskEngine
{
    // Statuses that participate in normal prolong consideration.
    private static readonly HashSet<ServiceStatus> Prolongable =
        new() { ServiceStatus.Active, ServiceStatus.Suspended, ServiceStatus.ProlongWait };

    public static CustomerAssessment Evaluate(
        Account? account,
        IReadOnlyList<Service> services,
        EvaluationContext ctx,
        ModelSettings model)
    {
        var warnings = new List<string> { "no_history_status", "synthetic_fixture", "no_replenishment_txns" };
        var contributions = new List<FeatureContribution>();
        var reasons = new List<string>();

        // --- no_assessment gate -------------------------------------------------
        // Not "no transactions" — transactions carry no reliability signal here.
        // No relevant services => we cannot assess, so we say so explicitly.
        var owned = account is null
            ? new List<Service>()
            : services.Where(s => s.OwnerId == account.Id).ToList();
        var relevant = owned.Where(s => Prolongable.Contains(s.Status)).ToList();

        if (account is null || relevant.Count == 0)
        {
            return new CustomerAssessment(
                AccountId: account?.Id ?? 0,
                Level: RiskLevel.NoAssessment,
                Score: -1,
                ReasonCodes: new[] { "no_assessment" },
                Contributions: Array.Empty<FeatureContribution>(),
                Warnings: warnings,
                NoAssessment: true,
                SnapshotVersion: ctx.SnapshotVersion,
                ModelVersion: model.ModelVersion,
                AsOf: ctx.AsOf);
        }

        if (account!.BonusState == BonusState.Locked) warnings.Add("bonus_locked");

        var horizonEnd = ctx.AsOf.AddDays(ctx.HorizonDays);

        // Due renewals: prolongable, expiring within horizon. Order is fixed by
        // BalanceFund (expiresAt, then id) — deterministic, shared with money.
        var due = relevant.Where(s => s.ExpiresAt <= horizonEnd).ToList();

        // Spend the shared fund once; insufficient_funds iff any due renewal is
        // left uncovered. Same rule as the money engine (BalanceFund) — no
        // second, divergent implementation.
        bool anyUncovered = BalanceFund.UncoveredAfterSpending(due, account) > 0;

        // --- per-service feature accumulation ----------------------------------
        // raw is long: several services each contributing a large weight would
        // overflow int. The normalization clamps to [0,100] regardless, but the
        // accumulation itself must not wrap negative.
        long raw = 0;
        var seen = new HashSet<string>();
        void Add(string code, string detail, int weight)
        {
            raw += weight;
            contributions.Add(new FeatureContribution(code, detail, weight));
            if (seen.Add(code)) reasons.Add(code); // one reason code per feature type
        }

        foreach (var s in relevant)
        {
            bool dueInHorizon = s.ExpiresAt <= horizonEnd;

            if (s.Status == ServiceStatus.ProlongWait)
                Add("prolong_wait", $"service:{s.Id} awaiting payment", model.WeightProlongWait);

            if (s.Status == ServiceStatus.Suspended)
                Add("suspended", $"service:{s.Id} suspended by user", model.WeightSuspended);

            if (dueInHorizon && !s.AutoProlong
                && (s.Status == ServiceStatus.Active || s.Status == ServiceStatus.Suspended))
                Add("expiring_no_autoprolong", $"service:{s.Id} expires without autoprolong",
                    model.WeightExpiringNoAutoprolong);
        }

        // Insufficient funds applies once per account (fund already spent above).
        if (anyUncovered)
            Add("insufficient_funds", "due renewals exceed available fund",
                model.WeightInsufficientFunds);

        // --- normalize + level --------------------------------------------------
        long maxRaw = Math.Max(1L, model.MaxRaw);
        int score = (int)Math.Clamp((long)Math.Round(100.0 * raw / maxRaw), 0, 100);

        var level = score >= model.HighThreshold ? RiskLevel.High
                  : score >= model.MediumThreshold ? RiskLevel.Medium
                  : RiskLevel.Low;

        return new CustomerAssessment(
            AccountId: account.Id,
            Level: level,
            Score: score,
            ReasonCodes: reasons,
            Contributions: contributions,
            Warnings: warnings,
            NoAssessment: false,
            SnapshotVersion: ctx.SnapshotVersion,
            ModelVersion: model.ModelVersion,
            AsOf: ctx.AsOf);
    }

    /// <summary>Derived loyalty indicator = 100 - score. NOT a measured trait.</summary>
    public static int LoyaltyIndicator(CustomerAssessment a) =>
        a.NoAssessment ? -1 : 100 - a.Score;
}
