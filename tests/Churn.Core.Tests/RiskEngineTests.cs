using Churn.Core;

namespace Churn.Core.Tests;

/// <summary>
/// Tests for the pure risk engine. All inputs are tiny anonymized fixtures built
/// inline; no emails/real rows appear in assertions. Time is injected via
/// EvaluationContext.AsOf — the engine never reads a real clock.
/// </summary>
public class RiskEngineTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static EvaluationContext Ctx(int horizon = 14, long snap = 1, int mv = 1)
        => new(AsOf, horizon, snap, mv);
    private static ModelSettings Baseline() => ModelSettings.Baseline();

    private static Account Acc(long id = 1, long balance = 100_000,
        long withdraw = 0, long replen = 200_000, long bonus = 0,
        BonusState bs = BonusState.Locked) =>
        new(id, "u@x.test", balance, withdraw, replen, bonus, bs);

    private static Service Svc(long id, long owner, ServiceStatus st,
        int daysToExpire, PaymentTerm term = PaymentTerm.Month,
        bool auto = false, long price = 500) =>
        new(id, $"svc-{id}", owner, term, auto,
            AsOf.AddDays(-30), AsOf.AddDays(daysToExpire), st, price);

    // --- every service status ------------------------------------------------
    [Theory]
    [InlineData(ServiceStatus.Active)]
    [InlineData(ServiceStatus.Suspended)]
    [InlineData(ServiceStatus.ProlongWait)]
    public void Assessable_statuses_produce_a_score(ServiceStatus st)
    {
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, st, 5) }, Ctx(), Baseline());
        Assert.False(r.NoAssessment);
        Assert.InRange(r.Score, 0, 100);
        Assert.NotEqual(RiskLevel.NoAssessment, r.Level);
    }

    [Theory]
    [InlineData(ServiceStatus.Blocked)]
    [InlineData(ServiceStatus.Deleted)]
    public void Non_prolongable_statuses_give_no_assessment_when_only_service(ServiceStatus st)
    {
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, st, 5) }, Ctx(), Baseline());
        Assert.True(r.NoAssessment);
        Assert.Equal(-1, r.Score);
        Assert.Equal(RiskLevel.NoAssessment, r.Level);
    }

    [Fact]
    public void No_services_means_no_assessment()
    {
        var r = RiskEngine.Evaluate(Acc(), Array.Empty<Service>(), Ctx(), Baseline());
        Assert.True(r.NoAssessment);
    }

    [Fact]
    public void Missing_account_means_no_assessment()
    {
        var r = RiskEngine.Evaluate(null, new[] { Svc(1, 1, ServiceStatus.Active, 5) }, Ctx(), Baseline());
        Assert.True(r.NoAssessment);
    }

    // --- autoProlong on/off ----------------------------------------------------
    [Fact]
    public void Expiring_with_autoprolong_off_raises_feature()
    {
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 3, auto: false) }, Ctx(), Baseline());
        Assert.Contains(r.ReasonCodes, c => c == "expiring_no_autoprolong");
    }

    [Fact]
    public void Expiring_with_autoprolong_on_skips_feature()
    {
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 3, auto: true) }, Ctx(), Baseline());
        Assert.DoesNotContain(r.ReasonCodes, c => c == "expiring_no_autoprolong");
    }

    // --- sufficient / insufficient funds ---------------------------------------
    [Fact]
    public void Sufficient_fund_avoids_insufficient_funds()
    {
        var r = RiskEngine.Evaluate(Acc(balance: 100_000), new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx(), Baseline());
        Assert.DoesNotContain(r.ReasonCodes, c => c == "insufficient_funds");
    }

    [Fact]
    public void Insufficient_fund_raises_feature_once()
    {
        // two due renewals of 500 each, balance covers neither
        var r = RiskEngine.Evaluate(Acc(balance: 0), new[]
        {
            Svc(1, 1, ServiceStatus.Active, 3, price: 500),
            Svc(2, 1, ServiceStatus.Active, 4, price: 500),
        }, Ctx(), Baseline());
        Assert.Single(r.Contributions, c => c.Code == "insufficient_funds");
    }

    [Fact]
    public void Fund_is_spent_once_not_per_service()
    {
        // balance 500 covers first due renewal (500) but not the second.
        var r = RiskEngine.Evaluate(Acc(balance: 500), new[]
        {
            Svc(1, 1, ServiceStatus.Active, 1, price: 500),
            Svc(2, 1, ServiceStatus.Active, 2, price: 500),
        }, Ctx(), Baseline());
        Assert.Contains(r.ReasonCodes, c => c == "insufficient_funds");
    }

    // --- bonus available / locked ---------------------------------------------
    [Fact]
    public void Unlocked_bonus_counts_toward_fund()
    {
        var r = RiskEngine.Evaluate(Acc(balance: 100, bonus: 500, bs: BonusState.Unlocked),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx(), Baseline());
        Assert.DoesNotContain(r.ReasonCodes, c => c == "insufficient_funds");
    }

    [Fact]
    public void Locked_bonus_does_not_count()
    {
        var r = RiskEngine.Evaluate(Acc(balance: 100, bonus: 500, bs: BonusState.Locked),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx(), Baseline());
        Assert.Contains(r.ReasonCodes, c => c == "insufficient_funds");
        Assert.Contains("bonus_locked", r.Warnings);
    }

    [Fact]
    public void Withdraw_balance_is_not_auto_available()
    {
        var r = RiskEngine.Evaluate(Acc(balance: 0, withdraw: 999_999),
            new[] { Svc(1, 1, ServiceStatus.Active, 3, price: 500) }, Ctx(), Baseline());
        Assert.Contains(r.ReasonCodes, c => c == "insufficient_funds");
    }

    // --- time boundary ----------------------------------------------------------
    [Fact]
    public void Service_beyond_horizon_is_not_due()
    {
        var r = RiskEngine.Evaluate(Acc(balance: 0), new[] { Svc(1, 1, ServiceStatus.Active, 40, auto: false) }, Ctx(), Baseline());
        Assert.DoesNotContain(r.ReasonCodes, c => c == "expiring_no_autoprolong");
        Assert.DoesNotContain(r.ReasonCodes, c => c == "insufficient_funds");
    }

    [Fact]
    public void Service_at_exact_horizon_is_due()
    {
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 14, auto: false) }, Ctx(), Baseline());
        Assert.Contains(r.ReasonCodes, c => c == "expiring_no_autoprolong");
    }

    // --- score bounded ------------------------------------------------------------
    [Fact]
    public void Score_is_bounded_0_to_100()
    {
        var many = Enumerable.Range(1, 10)
            .Select(i => Svc(i, 1, ServiceStatus.ProlongWait, 1)).ToList();
        var r = RiskEngine.Evaluate(Acc(balance: 0), many, Ctx(), Baseline());
        Assert.InRange(r.Score, 0, 100);
    }

    // --- determinism on same inputs + asOf -----------------------------------------
    [Fact]
    public void Same_inputs_same_asof_same_result()
    {
        var svcs = new[] { Svc(1, 1, ServiceStatus.ProlongWait, 2), Svc(2, 1, ServiceStatus.Suspended, 1) };
        var a = RiskEngine.Evaluate(Acc(), svcs, Ctx(), Baseline());
        var b = RiskEngine.Evaluate(Acc(), svcs, Ctx(), Baseline());
        Assert.Equal(a.Score, b.Score);
        Assert.Equal(a.Level, b.Level);
        Assert.Equal(a.ReasonCodes, b.ReasonCodes);
    }

    // --- reasons/contributions consistency ------------------------------------------
    [Fact]
    public void Reasons_match_contribution_codes()
    {
        var svcs = new[] { Svc(1, 1, ServiceStatus.ProlongWait, 2) };
        var r = RiskEngine.Evaluate(Acc(balance: 0), svcs, Ctx(), Baseline());
        var codes = r.Contributions.Select(c => c.Code).Distinct().OrderBy(x => x);
        Assert.Equal(codes, r.ReasonCodes.OrderBy(x => x));
    }

    [Fact]
    public void Score_reflects_sum_of_contributions()
    {
        var m = Baseline();
        var svcs = new[] { Svc(1, 1, ServiceStatus.ProlongWait, 2) };
        var r = RiskEngine.Evaluate(Acc(), svcs, Ctx(), m);
        int raw = r.Contributions.Sum(c => c.Weight);
        int expected = (int)Math.Clamp(Math.Round(100.0 * raw / m.MaxRaw), 0, 100);
        Assert.Equal(expected, r.Score);
    }

    // --- provenance ---------------------------------------------------------------
    [Fact]
    public void Result_carries_asof_and_versions()
    {
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.Active, 3) },
            Ctx(snap: 7), Baseline() with { ModelVersion = 3 });
        Assert.Equal(AsOf, r.AsOf);
        Assert.Equal(7, r.SnapshotVersion);
        Assert.Equal(3, r.ModelVersion);
    }

    // --- loyalty indicator is derived, not measured -------------------------------
    [Fact]
    public void Loyalty_indicator_is_inverse_of_score()
    {
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.ProlongWait, 2) }, Ctx(), Baseline());
        Assert.Equal(100 - r.Score, RiskEngine.LoyaltyIndicator(r));
    }

    [Fact]
    public void Loyalty_indicator_undefined_when_no_assessment()
    {
        var r = RiskEngine.Evaluate(Acc(), Array.Empty<Service>(), Ctx(), Baseline());
        Assert.Equal(-1, RiskEngine.LoyaltyIndicator(r));
    }

    // --- level thresholds -----------------------------------------------------------
    [Fact]
    public void Threshold_boundaries()
    {
        // single prolong_wait => weight 40/115 -> score ~35 -> medium
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.ProlongWait, 2) }, Ctx(), Baseline());
        Assert.Equal(RiskLevel.Medium, r.Level);
    }
}
