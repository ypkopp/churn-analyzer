namespace Churn.Core;

/// <summary>
/// Immutable evaluation context: the single source of "now" for a calculation.
/// Domain code must never call DateTime.UtcNow — asOf is injected here and is
/// produced at the app boundary by a TimeProvider (System in prod, Fake in tests).
/// </summary>
public sealed record EvaluationContext(
    DateTimeOffset AsOf,
    int HorizonDays,
    long SnapshotVersion,
    int ModelVersion);

/// <summary>Model coefficients. Baseline values are proposed, not trained.</summary>
public sealed record ModelSettings(
    int ModelVersion,
    int HorizonDays,
    // feature weights
    int WeightExpiringNoAutoprolong,
    int WeightInsufficientFunds,
    int WeightProlongWait,
    int WeightSuspended,
    // level thresholds on normalized score [0..100]
    int HighThreshold,
    int MediumThreshold)
{
    public static ModelSettings Baseline(int modelVersion = 1) => new(
        ModelVersion: modelVersion,
        HorizonDays: 14,
        WeightExpiringNoAutoprolong: 30,
        WeightInsufficientFunds: 25,
        WeightProlongWait: 40,
        WeightSuspended: 10,
        HighThreshold: 50,
        MediumThreshold: 20);

    /// <summary>Max raw = sum of weights, used to normalize to [0..100].</summary>
    public int MaxRaw =>
        WeightExpiringNoAutoprolong + WeightInsufficientFunds +
        WeightProlongWait + WeightSuspended;
}

public sealed record FeatureContribution(string Code, string Detail, int Weight);

public sealed record CustomerAssessment(
    long AccountId,
    RiskLevel Level,
    int Score,                 // 0..100 priority index; -1 when NoAssessment
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<FeatureContribution> Contributions,
    IReadOnlyList<string> Warnings,
    bool NoAssessment,
    long SnapshotVersion,
    int ModelVersion,
    DateTimeOffset AsOf);
