namespace Churn.Core;

/// <summary>
/// Money estimate for an account: scenario split of the at-risk renewal amount.
/// Integer EUR cents only — no float/double. One explainable baseline:
///   atRiskAmount = round( renewalCostInHorizon * score / 100 ), AwayFromZero.
/// score/100 is a scenario-index weight, NOT a probability / expected loss.
/// When the customer has no assessment, the estimate is NoEstimate — never 0.
/// </summary>
public static class MoneyEngine
{
    // Statuses that can still generate a billable renewal in the horizon.
    private static readonly HashSet<ServiceStatus> Billable =
        new() { ServiceStatus.Active, ServiceStatus.Suspended, ServiceStatus.ProlongWait };

    public static MoneyAssessment Evaluate(
        Account? account,
        IReadOnlyList<Service> services,
        CustomerAssessment risk,
        EvaluationContext ctx)
    {
        var horizonEnd = ctx.AsOf.AddDays(ctx.HorizonDays);
        var owned = account is null
            ? new List<Service>()
            : services.Where(s => s.OwnerId == account.Id).ToList();

        // Billable events: billable status AND expiresAt inside the horizon.
        // One renewalCost contribution per due service — overdue + next period are
        // NOT double-counted (prolong_wait counted once via its price).
        var dueServices = owned
            .Where(s => Billable.Contains(s.Status) && s.ExpiresAt <= horizonEnd)
            .OrderBy(s => s.ExpiresAt).ThenBy(s => s.Id)
            .ToList();

        long renewalCost = 0;
        var lines = new List<MoneyLine>();
        foreach (var s in dueServices)
        {
            renewalCost = checked(renewalCost + s.Price);
            lines.Add(new MoneyLine(s.Id, s.PaymentTerm, s.Status, s.Price, s.ExpiresAt));
        }

        if (account is null || risk.NoAssessment)
        {
            return new MoneyAssessment(
                AccountId: account?.Id ?? 0,
                State: MoneyState.NoEstimate,
                RenewalCostInHorizon: renewalCost,
                AvailableFund: 0,
                UncoveredAmount: 0,
                AtRiskAmount: null,           // explicit absence, not 0
                ScoreWeight: null,
                Semantic: "heuristic_money_index",
                Lines: lines,
                HorizonDays: ctx.HorizonDays,
                SnapshotVersion: ctx.SnapshotVersion,
                ModelVersion: ctx.ModelVersion,
                AsOf: ctx.AsOf);
        }

        long fund = BalanceFund.Available(account);
        long uncovered = Math.Max(0, renewalCost - fund);

        // score/100 applied as a scenario weight; integer-safe, no float money.
        long atRisk = renewalCost == 0 ? 0
            : (long)Math.Round((decimal)renewalCost * risk.Score / 100m,
                               MidpointRounding.AwayFromZero);

        return new MoneyAssessment(
            AccountId: account.Id,
            State: renewalCost == 0 ? MoneyState.NoDueInHorizon : MoneyState.Estimated,
            RenewalCostInHorizon: renewalCost,
            AvailableFund: fund,
            UncoveredAmount: uncovered,
            AtRiskAmount: atRisk,
            ScoreWeight: risk.Score,         // 0..100 index, for transparency
            Semantic: "heuristic_money_index",
            Lines: lines,
            HorizonDays: ctx.HorizonDays,
            SnapshotVersion: ctx.SnapshotVersion,
            ModelVersion: ctx.ModelVersion,
            AsOf: ctx.AsOf);
    }
}

public enum MoneyState { Estimated, NoDueInHorizon, NoEstimate }

public sealed record MoneyLine(
    long ServiceId, PaymentTerm Term, ServiceStatus Status,
    long PriceCents, DateTimeOffset DueAt);

public sealed record MoneyAssessment(
    long AccountId,
    MoneyState State,
    long RenewalCostInHorizon,
    long AvailableFund,
    long UncoveredAmount,
    long? AtRiskAmount,          // null when NoEstimate — explicit, not 0
    int? ScoreWeight,
    string Semantic,
    IReadOnlyList<MoneyLine> Lines,
    int HorizonDays,
    long SnapshotVersion,
    int ModelVersion,
    DateTimeOffset AsOf);
