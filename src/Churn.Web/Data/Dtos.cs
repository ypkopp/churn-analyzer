namespace Churn.Web.Data;

/// <summary>
/// API/persistence DTOs. Money = integer EUR cents. No raw record leakage in
/// responses beyond what the authenticated marketer needs (email is required by
/// the ranking table per the case spec and is PII-marked).
/// </summary>
public sealed record AccountDto(long Id, string Email, long Balance,
    long WithdrawBalance, long TotalReplenished, long BonusBalance, string BonusState);

public sealed record ServiceDto(long Id, string Name, long OwnerId, string PaymentTerm,
    bool AutoProlong, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt,
    string Status, long Price);

public sealed record TransactionDto(long Id, int Amount, string Status,
    DateTimeOffset? PerformedAt, DateTimeOffset CreatedAt, string Type, long ServiceId);

/// <summary>Atomic replacement of the whole source snapshot.</summary>
public sealed record SourceSnapshotDto(
    IReadOnlyList<AccountDto> Accounts,
    IReadOnlyList<ServiceDto> Services,
    IReadOnlyList<TransactionDto> Transactions);

public sealed record ModelSettingsDto(int ModelVersion, int HorizonDays,
    Dictionary<string, int> Weights, Dictionary<string, int> Thresholds);

public sealed record LoginRequest(string Login, string Password);
public sealed record MeResponse(string Login, string Role);
public sealed record RotatePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record CreateUserRequest(string Login, string Password, string Role);
public sealed record SetUserStatusRequest(bool Disabled);

// --- retention tasks ---------------------------------------------------------
public sealed record CreateTaskRequest(long AccountId, string Title,
    long AssigneeUserId, DateTimeOffset? DueAt, string Priority, string? Description);
public sealed record UpdateTaskRequest(string? Title, DateTimeOffset? DueAt,
    string? Priority, string? Description);
public sealed record TaskStatusRequest(string Status, string? Note);
public sealed record TaskDto(long Id, long AccountId, string Title,
    long AssigneeUserId, string AssigneeLogin, DateTimeOffset? DueAt,
    string Priority, string? Description, string Status,
    Guid EvaluationId, DateTimeOffset AsOf, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, bool CustomerAvailable);

// --- contact messages + saved segments ----------------------------------------
public sealed record CreateMessageRequest(long AccountId, string Recipient,
    string Subject, string Body, string? TemplateKey);
public sealed record MessageDto(long Id, long AccountId, string Recipient,
    string Subject, string Body, string Status, string? TransportNote,
    Guid EvaluationId, DateTimeOffset AsOf, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);
public sealed record CreateSegmentRequest(string Name, string Query);
public sealed record SegmentDto(long Id, string Name, string Query, DateTimeOffset CreatedAt);
