using Churn.Core;

namespace Churn.Core.Tests;

/// <summary>
/// Money engine tests. Integer cents only; the breakdown (lines) must reconcile
/// with the reported totals. atRiskAmount is a scenario index weighting, never a
/// probability. Missing assessment => NoEstimate / null, never 0.
/// </summary>
public class MoneyEngineTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static EvaluationContext Ctx(int horizon = 14) => new(AsOf, horizon, 1, 1);
    private static ModelSettings M() => ModelSettings.Baseline();

    private static Account Acc(long id = 1, long balance = 0, long withdraw = 0,
        long replen = 0, long bonus = 0, BonusState bs = BonusState.Locked) =>
        new(id, "u@x.test", balance, withdraw, replen, bonus, bs);

    private static Service Svc(long id, long owner, ServiceStatus st, int daysToExpire,
        PaymentTerm term = PaymentTerm.Month, bool auto = false, long price = 500) =>
        new(id, $"s{id}", owner, term, auto, AsOf.AddDays(-30), AsOf.AddDays(daysToExpire), st, price);

    private static CustomerAssessment RiskOf(Account a, Service[] svcs, EvaluationContext c) =>
        RiskEngine.Evaluate(a, svcs, c, M());

    private static MoneyAssessment MoneyOf(Account a, Service[] svcs, EvaluationContext c) =>
        MoneyEngine.Evaluate(a, svcs, RiskOf(a, svcs, c), c);

    // zero balance -> uncovered == renewal cost
    [Fact]
    public void Zero_balance_leaves_full_renewal_uncovered()
    {
        var svcs = new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) };
        var m = MoneyOf(Acc(balance: 0), svcs, Ctx());
        Assert.Equal(500, m.RenewalCostInHorizon);
        Assert.Equal(0, m.AvailableFund);
        Assert.Equal(500, m.UncoveredAmount);
    }

    // large balance covers everything
    [Fact]
    public void Large_balance_leaves_nothing_uncovered()
    {
        var svcs = new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) };
        var m = MoneyOf(Acc(balance: 999_999), svcs, Ctx());
        Assert.Equal(0, m.UncoveredAmount);
    }

    // unlocked bonus counts, locked does not
    [Fact]
    public void Unlocked_bonus_adds_to_fund()
    {
        var m = MoneyOf(Acc(balance: 100, bonus: 400, bs: BonusState.Unlocked),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx());
        Assert.Equal(500, m.AvailableFund);
        Assert.Equal(0, m.UncoveredAmount);
    }

    [Fact]
    public void Locked_bonus_does_not_add_to_fund()
    {
        var m = MoneyOf(Acc(balance: 100, bonus: 400, bs: BonusState.Locked),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx());
        Assert.Equal(100, m.AvailableFund);
        Assert.Equal(400, m.UncoveredAmount);
    }

    // withdrawBalance must NOT be added to the fund
    [Fact]
    public void Withdraw_balance_is_not_part_of_fund()
    {
        var m = MoneyOf(Acc(balance: 0, withdraw: 999_999),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx());
        Assert.Equal(0, m.AvailableFund);
        Assert.Equal(500, m.UncoveredAmount);
    }

    // totalReplenished is a historical counter, not current funds
    [Fact]
    public void TotalReplenished_is_not_current_fund()
    {
        var m = MoneyOf(Acc(balance: 0, replen: 999_999),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx());
        Assert.Equal(0, m.AvailableFund);
    }

    // several services & terms sum into one renewal cost
    [Fact]
    public void Multiple_services_sum_renewal_cost()
    {
        var svcs = new[]
        {
            Svc(1, 1, ServiceStatus.Active, 3, PaymentTerm.Month, price: 500),
            Svc(2, 1, ServiceStatus.Active, 4, PaymentTerm.Year, price: 700),
        };
        var m = MoneyOf(Acc(balance: 0), svcs, Ctx());
        Assert.Equal(1200, m.RenewalCostInHorizon);
        Assert.Equal(m.Lines.Sum(l => l.PriceCents), m.RenewalCostInHorizon); // reconciles
    }

    // equal due dates -> deterministic order (by id)
    [Fact]
    public void Equal_due_dates_order_by_id()
    {
        var svcs = new[]
        {
            Svc(3, 1, ServiceStatus.Active, 5),
            Svc(1, 1, ServiceStatus.Active, 5),
            Svc(2, 1, ServiceStatus.Active, 5),
        };
        var m = MoneyOf(Acc(), svcs, Ctx());
        Assert.Equal(new long[] { 1, 2, 3 }, m.Lines.Select(l => l.ServiceId).ToArray());
    }

    // input permutation does not change totals
    [Fact]
    public void Input_permutation_keeps_totals()
    {
        var a = new[] { Svc(1, 1, ServiceStatus.Active, 5, price: 300), Svc(2, 1, ServiceStatus.Active, 3, price: 200) };
        var m1 = MoneyOf(Acc(balance: 250), a, Ctx());
        var m2 = MoneyOf(Acc(balance: 250), a.Reverse().ToArray(), Ctx());
        Assert.Equal(m1.RenewalCostInHorizon, m2.RenewalCostInHorizon);
        Assert.Equal(m1.UncoveredAmount, m2.UncoveredAmount);
        Assert.Equal(m1.AtRiskAmount, m2.AtRiskAmount);
    }

    // horizon boundary inclusive
    [Fact]
    public void Service_at_exact_horizon_is_included()
    {
        var m = MoneyOf(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 14) }, Ctx());
        Assert.Equal(500, m.RenewalCostInHorizon);
    }

    [Fact]
    public void Service_beyond_horizon_is_excluded()
    {
        var m = MoneyOf(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 40) }, Ctx());
        Assert.Equal(0, m.RenewalCostInHorizon);
        Assert.Equal(MoneyState.NoDueInHorizon, m.State);
    }

    // all six paymentTerms produce billable events inside the horizon
    [Theory]
    [InlineData(PaymentTerm.Hour)]
    [InlineData(PaymentTerm.Week)]
    [InlineData(PaymentTerm.Month)]
    [InlineData(PaymentTerm.QuarterYear)]
    [InlineData(PaymentTerm.HalfYear)]
    [InlineData(PaymentTerm.Year)]
    public void All_payment_terms_counted_when_due(PaymentTerm term)
    {
        var m = MoneyOf(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 1, term) }, Ctx());
        Assert.Single(m.Lines);
        Assert.Equal(term, m.Lines[0].Term);
    }

    // blocked/deleted never enter future renewals
    [Theory]
    [InlineData(ServiceStatus.Blocked)]
    [InlineData(ServiceStatus.Deleted)]
    public void Blocked_or_deleted_not_in_renewals(ServiceStatus st)
    {
        var m = MoneyOf(Acc(), new[] { Svc(1, 1, st, 2) }, Ctx());
        Assert.Equal(0, m.RenewalCostInHorizon);
        Assert.Empty(m.Lines);
    }

    // suspended still bills
    [Fact]
    public void Suspended_service_is_billed()
    {
        var m = MoneyOf(Acc(), new[] { Svc(1, 1, ServiceStatus.Suspended, 3) }, Ctx());
        Assert.Equal(500, m.RenewalCostInHorizon);
    }

    // prolong_wait counted once (overdue + next period not double-counted)
    [Fact]
    public void Prolong_wait_counted_once()
    {
        var m = MoneyOf(Acc(), new[] { Svc(1, 1, ServiceStatus.ProlongWait, 1, price: 500) }, Ctx());
        Assert.Single(m.Lines);
        Assert.Equal(500, m.RenewalCostInHorizon);
    }

    // no applicable services => NoEstimate / null, not 0
    [Fact]
    public void No_applicable_services_gives_no_estimate()
    {
        var m = MoneyOf(Acc(), new[] { Svc(1, 1, ServiceStatus.Deleted, 2) }, Ctx());
        Assert.Equal(MoneyState.NoEstimate, m.State);
        Assert.Null(m.AtRiskAmount);
        Assert.Null(m.ScoreWeight);
    }

    // atRisk = renewalCost * score/100, weighting not probability
    [Fact]
    public void AtRisk_is_renewalCost_weighted_by_score()
    {
        var a = Acc(balance: 0);
        var svcs = new[] { Svc(1, 1, ServiceStatus.ProlongWait, 1, price: 500) };
        var risk = RiskOf(a, svcs, Ctx());
        var m = MoneyEngine.Evaluate(a, svcs, risk, Ctx());
        // risk for single prolong_wait: weight 40/115 -> score ~35
        long expected = (long)Math.Round(500m * risk.Score / 100m, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, m.AtRiskAmount);
        Assert.Equal(risk.Score, m.ScoreWeight);
        Assert.Equal("heuristic_money_index", m.Semantic);
    }

    // breakdown reconciles: sum(lines) == renewalCost
    [Fact]
    public void Line_breakdown_reconciles_with_total()
    {
        var svcs = Enumerable.Range(1, 5).Select(i =>
            Svc(i, 1, ServiceStatus.Active, i, price: i * 100)).ToArray();
        var m = MoneyOf(Acc(balance: 0), svcs, Ctx());
        Assert.Equal(m.Lines.Sum(l => l.PriceCents), m.RenewalCostInHorizon);
    }

    // overflow-safe arithmetic on large money values
    [Fact]
    public void Large_amounts_do_not_overflow()
    {
        var a = Acc(balance: long.MaxValue / 2, bonus: 0);
        var svcs = new[] { Svc(1, 1, ServiceStatus.Active, 1, price: long.MaxValue / 4) };
        var m = MoneyOf(a, svcs, Ctx());
        Assert.True(m.RenewalCostInHorizon >= 0);
        Assert.True(m.AvailableFund >= 0);
    }

    // rounding rule: AwayFromZero on the score weighting
    [Fact]
    public void Weighting_rounds_away_from_zero()
    {
        // construct a score that yields a fractional weighting; verify integer rule
        var a = Acc(balance: 0);
        var svcs = new[] { Svc(1, 1, ServiceStatus.ProlongWait, 1, price: 999) }; // odd cents
        var risk = RiskOf(a, svcs, Ctx());
        var m = MoneyEngine.Evaluate(a, svcs, risk, Ctx());
        long expected = (long)Math.Round(999m * risk.Score / 100m, MidpointRounding.AwayFromZero);
        Assert.Equal(expected, m.AtRiskAmount);
    }
}
