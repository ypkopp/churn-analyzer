using Churn.Core;

namespace Churn.Core.Tests;

/// <summary>
/// A04 hardening: coefficient influence, normalization/invalid settings,
/// multi-service aggregation, input-order independence, deterministic fund
/// distribution on equal dates, and a dedicated no_assessment reason.
/// </summary>
public class RiskEngineHardeningTests
{
    private static readonly DateTimeOffset AsOf = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
    private static EvaluationContext Ctx(int horizon = 14) => new(AsOf, horizon, 1, 1);

    private static Account Acc(long id = 1, long balance = 0, long bonus = 0,
        BonusState bs = BonusState.Locked) =>
        new(id, "u@x.test", balance, 0, 0, bonus, bs);

    private static Service Svc(long id, long owner, ServiceStatus st, int daysToExpire,
        bool auto = false, long price = 500) =>
        new(id, $"s{id}", owner, PaymentTerm.Month, auto,
            AsOf.AddDays(-30), AsOf.AddDays(daysToExpire), st, price);

    // changing one coefficient changes the score
    [Fact]
    public void Increasing_a_weight_raises_the_score()
    {
        var svcs = new[] { Svc(1, 1, ServiceStatus.ProlongWait, 2) };
        var lowW = ModelSettings.Baseline() with { WeightProlongWait = 10 };
        var highW = ModelSettings.Baseline() with { WeightProlongWait = 80 };
        var sLow = RiskEngine.Evaluate(Acc(), svcs, Ctx(), lowW).Score;
        var sHigh = RiskEngine.Evaluate(Acc(), svcs, Ctx(), highW).Score;
        Assert.True(sHigh > sLow, $"higher weight should not lower score ({sLow}->{sHigh})");
    }

    // normalization keeps score within [0,100] even with huge weights
    [Fact]
    public void Score_stays_bounded_with_extreme_weights()
    {
        var m = ModelSettings.Baseline() with { WeightProlongWait = 10_000 };
        var svcs = new[] { Svc(1, 1, ServiceStatus.ProlongWait, 1) };
        var r = RiskEngine.Evaluate(Acc(), svcs, Ctx(), m);
        Assert.InRange(r.Score, 0, 100);
    }

    // invalid settings: all-zero weights -> normalization guard (MaxRaw>=1)
    [Fact]
    public void All_zero_weights_yield_zero_score_not_crash()
    {
        var m = new ModelSettings(1, 14, 0, 0, 0, 0, 50, 20);
        var r = RiskEngine.Evaluate(Acc(), new[] { Svc(1, 1, ServiceStatus.ProlongWait, 2) }, Ctx(), m);
        Assert.Equal(0, r.Score);
        Assert.Equal(RiskLevel.Low, r.Level);
    }

    // aggregation over several services
    [Fact]
    public void Multiple_services_accumulate_contributions()
    {
        var svcs = new[]
        {
            Svc(1, 1, ServiceStatus.ProlongWait, 2),
            Svc(2, 1, ServiceStatus.Suspended, 1),
            Svc(3, 1, ServiceStatus.Active, 3, auto: false),
        };
        var r = RiskEngine.Evaluate(Acc(balance: 0), svcs, Ctx(), BaselineKeep());
        Assert.Contains("prolong_wait", r.ReasonCodes);
        Assert.Contains("suspended", r.ReasonCodes);
        Assert.Contains("expiring_no_autoprolong", r.ReasonCodes);
        Assert.Contains("insufficient_funds", r.ReasonCodes);
        Assert.True(r.Contributions.Sum(c => c.Weight) > 0);
        static ModelSettings BaselineKeep() => ModelSettings.Baseline();
    }

    // order of input records must not change the result
    [Fact]
    public void Input_order_does_not_change_result()
    {
        var a = new[]
        {
            Svc(1, 1, ServiceStatus.ProlongWait, 2),
            Svc(2, 1, ServiceStatus.Suspended, 5),
            Svc(3, 1, ServiceStatus.Active, 1, auto: false),
        };
        var r1 = RiskEngine.Evaluate(Acc(balance: 0), a, Ctx(), ModelSettings.Baseline());
        var r2 = RiskEngine.Evaluate(Acc(balance: 0), a.Reverse().ToArray(), Ctx(), ModelSettings.Baseline());
        Assert.Equal(r1.Score, r2.Score);
        Assert.Equal(r1.Level, r2.Level);
        Assert.Equal(r1.ReasonCodes.OrderBy(x => x), r2.ReasonCodes.OrderBy(x => x));
    }

    // deterministic fund distribution when several renewals share the same date
    [Fact]
    public void Equal_due_dates_distribute_deterministically()
    {
        // same expiry, different ids — order must be deterministic (by id tie-break)
        var svcs = new[]
        {
            Svc(3, 1, ServiceStatus.Active, 5, price: 500),
            Svc(1, 1, ServiceStatus.Active, 5, price: 500),
            Svc(2, 1, ServiceStatus.Active, 5, price: 500),
        };
        var r1 = RiskEngine.Evaluate(Acc(balance: 500), svcs, Ctx(), ModelSettings.Baseline());
        var r2 = RiskEngine.Evaluate(Acc(balance: 500), svcs.Reverse().ToArray(), Ctx(), ModelSettings.Baseline());
        Assert.Equal(r1.Score, r2.Score);   // 500 covers one of three -> insufficient either way
        Assert.Contains("insufficient_funds", r1.ReasonCodes);
    }

    // dedicated no_assessment reason when account has no applicable services
    [Fact]
    public void No_applicable_services_gives_no_assessment_reason()
    {
        var svcs = new[] { Svc(1, 1, ServiceStatus.Blocked, 2), Svc(2, 1, ServiceStatus.Deleted, 2) };
        var r = RiskEngine.Evaluate(Acc(), svcs, Ctx(), ModelSettings.Baseline());
        Assert.True(r.NoAssessment);
        Assert.Contains("no_assessment", r.ReasonCodes);
        Assert.Equal(-1, r.Score);
    }

    // score still reflects contributions even when raw exceeds maxRaw
    [Fact]
    public void Score_clamps_when_raw_exceeds_maxraw()
    {
        var many = Enumerable.Range(1, 6).Select(i => Svc(i, 1, ServiceStatus.ProlongWait, 1)).ToArray();
        var r = RiskEngine.Evaluate(Acc(), many, Ctx(), ModelSettings.Baseline());
        Assert.Equal(100, r.Score); // 6*40=240 > maxRaw 115 -> clamps to 100
    }
}
