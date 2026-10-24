namespace Churn.Core;

/// <summary>
/// Source-shaped entities. Money is integer EUR cents (no float, no currency
/// arithmetic semantics beyond "cents"). Ids are ints as in the fixture.
/// These are plain data records — no behaviour, no I/O.
/// </summary>
public sealed record Account(
    long Id,
    string Email,
    long Balance,
    long WithdrawBalance,
    long TotalReplenished,
    long BonusBalance,
    BonusState BonusState);

public sealed record Service(
    long Id,
    string Name,
    long OwnerId,
    PaymentTerm PaymentTerm,
    bool AutoProlong,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ServiceStatus Status,
    long Price);

public sealed record Transaction(
    long Id,
    int Amount,
    TransactionStatus Status,
    DateTimeOffset? PerformedAt,
    DateTimeOffset CreatedAt,
    TransactionType Type,
    long ServiceId);
