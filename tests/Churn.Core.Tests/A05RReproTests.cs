using Churn.Core;

namespace Churn.Core.Tests;

/// <summary>
/// A05R regression: reproduce the exact reported example end-to-end and lock the
/// shared baseline semantics. Baseline denominator = sum of all four weights
/// (30+25+40+10 = 105), NOT the previously mis-stated 115. Money horizon = the
/// single next materialized renewal per service (no generated schedule).
/// </summary>
public class A05RReproTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static EvaluationContext Ctx(int h = 14) => new(AsOf, h, 1, 1);
    private static ModelSettings M() => ModelSettings.Baseline();

    private static Account Acc(long id = 1, long bal = 0, long wd = 0, long repl = 0,
        long bonus = 0, BonusState bs = BonusState.Locked) =>
        new(id, "u@x.test", bal, wd, repl, bonus, bs);

    private static Service Svc(long id, long owner, ServiceStatus st, int daysToExp,
        PaymentTerm term = PaymentTerm.Month, bool auto = false, long price = 500) =>
        new(id, $"s{id}", owner, term, auto, AsOf.AddDays(-30), AsOf.AddDays(daysToExp), st, price);

    // --- the exact reported example -----------------------------------------
    [Fact]
    public void Exact_example_prolong_wait_500_balance0_bonus_locked()
    {
        var a = Acc(bal: 0, bonus: 0, bs: BonusState.Locked);
        var s = Svc(1, 1, ServiceStatus.ProlongWait, daysToExp: 5, price: 500);
        var ctx = Ctx();
        var model = M();

        var risk = RiskEngine.Evaluate(a, new[] { s }, ctx, model);
        var money = MoneyEngine.Evaluate(a, new[] { s }, risk, ctx);

        // reasons & contributions
        Assert.Equal(new[] { "prolong_wait", "insufficient_funds" }, risk.ReasonCodes);
        var contrib = risk.Contributions.ToDictionary(c => c.Code, c => c.Weight);
        Assert.Equal(40, contrib["prolong_wait"]);
        Assert.Equal(25, contrib["insufficient_funds"]);

        // raw / denominator / score
        int raw = risk.Contributions.Sum(c => c.Weight);
        Assert.Equal(65, raw);
        Assert.Equal(105, model.MaxRaw);                 // denominator = ALL weights
        Assert.Equal(62, risk.Score);                    // round(100*65/105)=61.9->62
        Assert.Equal(RiskLevel.High, risk.Level);

        // money breakdown
        Assert.Equal(500, money.RenewalCostInHorizon);
        Assert.Equal(0, money.AvailableFund);
        Assert.Equal(500, money.UncoveredAmount);
        Assert.Equal(310, money.AtRiskAmount);           // round(500*62/100)
        Assert.Equal(risk.Score, money.ScoreWeight);
        Assert.Equal("heuristic_money_index", money.Semantic);
        Assert.Single(money.Lines);
        Assert.Equal(money.Lines.Sum(l => l.PriceCents), money.RenewalCostInHorizon);
    }

    // --- denominator is the sum of ALL weights, not triggered-only ------------
    [Fact]
    public void Denominator_is_sum_of_all_weights()
    {
        var m = M();
        Assert.Equal(
            m.WeightExpiringNoAutoprolong + m.WeightInsufficientFunds +
            m.WeightProlongWait + m.WeightSuspended,
            m.MaxRaw);
        Assert.Equal(105, m.MaxRaw);
    }

    // --- horizon = ONE nearest materialized renewal, not a schedule ------------
    [Fact]
    public void Only_nearest_materialized_renewal_counts()
    {
        // one service due in horizon -> exactly one money line (not a series)
        var m = MoneyEngine.Evaluate(Acc(),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) },
            RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx(), M()),
            Ctx());
        Assert.Single(m.Lines);
        Assert.Equal(500, m.RenewalCostInHorizon);
    }

    // lower bound: overdue prolongable service still counts (nearest due)
    [Fact]
    public void Overdue_active_still_counts_as_due()
    {
        var past = Svc(1, 1, ServiceStatus.Active, daysToExp: -2, price: 500); // expired 2 days ago
        var m = MoneyEngine.Evaluate(Acc(), new[] { past },
            RiskEngine.Evaluate(Acc(), new[] { past }, Ctx(), M()), Ctx());
        Assert.Equal(500, m.RenewalCostInHorizon);
    }

    // uncovered == 0 but atRisk > 0 is allowed: atRisk weights renewalCost by
    // score (priority), independent of whether the fund covers it.
    [Fact]
    public void Uncovered_zero_but_atRisk_positive_is_allowed()
    {
        var a = Acc(bal: 1_000_000); // huge fund
        var s = Svc(1, 1, ServiceStatus.ProlongWait, 3, price: 500);
        var risk = RiskEngine.Evaluate(a, new[] { s }, Ctx(), M());
        var m = MoneyEngine.Evaluate(a, new[] { s }, risk, Ctx());
        Assert.Equal(0, m.UncoveredAmount);            // fund covers it
        Assert.True(m.AtRiskAmount > 0);               // still weighted by score
        Assert.Equal((long)Math.Round(500m * risk.Score / 100m, MidpointRounding.AwayFromZero),
                     m.AtRiskAmount);
    }

    // line sum reconciles with per-customer total
    [Fact]
    public void Line_sum_reconciles_with_customer_total()
    {
        var svcs = Enumerable.Range(1, 4).Select(i => Svc(i, 1, ServiceStatus.Active, i, price: i * 100)).ToArray();
        var m = MoneyEngine.Evaluate(Acc(bal: 0), svcs,
            RiskEngine.Evaluate(Acc(), svcs, Ctx(), M()), Ctx());
        Assert.Equal(m.Lines.Sum(l => l.PriceCents), m.RenewalCostInHorizon);
    }

    // rounding consistency: integer cents only, never negative, deterministic
    [Fact]
    public void Rounding_is_deterministic_and_integer()
    {
        var a = Acc(bal: 0);
        var s = Svc(1, 1, ServiceStatus.ProlongWait, 1, price: 999);
        var r = RiskEngine.Evaluate(a, new[] { s }, Ctx(), M());
        var m1 = MoneyEngine.Evaluate(a, new[] { s }, r, Ctx());
        var m2 = MoneyEngine.Evaluate(a, new[] { s }, r, Ctx());
        Assert.Equal(m1.AtRiskAmount, m2.AtRiskAmount);
        Assert.True(m1.AtRiskAmount >= 0);
        Assert.Equal(0, m1.AtRiskAmount.Value % 1); // integer cents
    }

    // no_estimate is explicit, not a zero substitute
    [Fact]
    public void No_estimate_is_explicit_not_zero()
    {
        var m = MoneyEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.Deleted, 2) },
            RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.Deleted, 2) }, Ctx(), M()), Ctx());
        Assert.Equal(MoneyState.NoEstimate, m.State);
        Assert.Null(m.AtRiskAmount);
        Assert.Null(m.ScoreWeight);
        Assert.NotEqual(0, m.AtRiskAmount ?? -1);
    }

    // all-zero weights = contributions off, NOT proven safety
    [Fact]
    public void All_zero_weights_are_disabled_contributions_not_safe()
    {
        var m = new ModelSettings(1, 14, 0, 0, 0, 0, 50, 20);
        var a = Acc(bal: 0);
        var s = Svc(1, 1, ServiceStatus.ProlongWait, 1, price: 500);
        var risk = RiskEngine.Evaluate(a, new[] { s }, Ctx(), m);
        Assert.Equal(0, risk.Score);            // contributions disabled -> 0 index
        Assert.Equal(RiskLevel.Low, risk.Level); // low by thresholds, not "proven safe"
        Assert.True(risk.Contributions.All(c => c.Weight == 0));
        // money still computes from renewalCost * 0/100 = 0 scenario index
        var money = MoneyEngine.Evaluate(a, new[] { s }, risk, Ctx());
        Assert.Equal(0, money.AtRiskAmount);
    }
}
