using System.Text.Json;

namespace Churn.Web.Data;

/// <summary>
/// Materialized results for ONE evaluation batch. All customers/customer/summary
/// responses for a given evaluationId share snapshotVersion, modelVersion and
/// asOf. Results are persisted so a restart never shows an old result under a
/// new version tuple, and a partial batch is never published.
/// </summary>
public sealed class CustomerResult
{
    public long Id { get; set; }
    public Guid EvaluationId { get; set; }
    public long AccountId { get; set; }
    public string Payload { get; set; } = "";   // serialized CustomerResultDto (jsonb)
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SummaryResult
{
    public long Id { get; set; }
    public Guid EvaluationId { get; set; }
    public string Payload { get; set; } = "";   // serialized SummaryResultDto (jsonb)
    public DateTimeOffset CreatedAt { get; set; }
}

// --- API response DTOs (no raw source rows; money in EUR cents) -------------
public sealed record CustomerRankDto(
    long AccountId, string Email, long Balance, long TotalReplenished,
    int? RiskScore, string RiskLevel, IReadOnlyList<string> Reasons,
    int? LoyaltyIndicator, bool NoAssessment);

public sealed record CustomerDetailDto(
    long AccountId, string Email, long Balance, long WithdrawBalance,
    long TotalReplenished, long BonusBalance, string BonusState,
    int? RiskScore, string RiskLevel, IReadOnlyList<string> Reasons,
    int? LoyaltyIndicator, bool NoAssessment,
    IReadOnlyList<FeatureDto> Features,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ServiceLineDto> Services,
    MoneyDto? Money,
    string LoyaltySemantics);

public sealed record FeatureDto(string Code, string Detail, int Weight);
public sealed record ServiceLineDto(long Id, string Name, string PaymentTerm,
    bool AutoProlong, string Status, long Price, DateTimeOffset ExpiresAt);

public sealed record MoneyDto(
    string Unit, long RenewalCostInHorizon, long AvailableFund,
    long UncoveredAmount, long? AtRiskAmount, string Semantic,
    IReadOnlyList<MoneyLineDto> Lines, int HorizonDays);
public sealed record MoneyLineDto(long ServiceId, string Term, string Status,
    long PriceCents, DateTimeOffset DueAt, bool Overdue);

public sealed record SummaryDto(
    CountsByLevel Counts, MoneySummary Money,
    long SnapshotVersion, int ModelVersion, Guid EvaluationId, DateTimeOffset AsOf);
public sealed record CountsByLevel(int High, int Medium, int Low, int NoAssessment);
public sealed record MoneySummary(string Unit, long RenewalCostTotal,
    long AvailableFundTotal, long UncoveredTotal, long? AtRiskTotal,
    string Semantic, int HorizonDays);

public sealed record CustomerResultDto(
    CustomerRankDto Rank, CustomerDetailDto Detail,
    long SnapshotVersion, int ModelVersion, Guid EvaluationId, DateTimeOffset AsOf);

public sealed record ApiError(string Error, string? Detail = null);

/// <summary>PATCH body for model settings with optimistic concurrency.</summary>
public sealed record ModelSettingsPatch(int? ExpectedVersion, ModelSettingsDto Settings);
