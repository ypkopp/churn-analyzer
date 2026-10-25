namespace Churn.Web.Data;

/// <summary>
/// Single non-writing validator used by PATCH /model/settings, POST
/// /api/v1/analytics/whatif and PUT /api/v1/source/snapshot. Rejects the whole
/// payload before any write — an invalid request never touches the source
/// snapshot, the stored settings or the last published batch. Ranges are the
/// explicit model contract, not arbitrary frontend limits.
/// </summary>
public static class InputValidator
{
    // Known model keys — anything else is rejected, not silently ignored.
    private static readonly HashSet<string> WeightKeys = new(StringComparer.Ordinal)
        { "expiring_no_autoprolong", "insufficient_funds", "prolong_wait", "suspended" };
    private static readonly Dictionary<string, int> BaselineWeights = new(StringComparer.Ordinal)
    {
        ["expiring_no_autoprolong"] = 30, ["insufficient_funds"] = 25,
        ["prolong_wait"] = 40, ["suspended"] = 10
    };
    private static readonly HashSet<string> ThresholdKeys = new(StringComparer.Ordinal)
        { "high", "medium" };
    private static readonly HashSet<string> PaymentTerms = new(StringComparer.Ordinal)
        { "hour", "week", "month", "quarter_year", "half_year", "year" };
    private static readonly HashSet<string> ServiceStatuses = new(StringComparer.Ordinal)
        { "active", "suspended", "prolong_wait", "blocked", "deleted" };
    private static readonly HashSet<string> TxStatuses = new(StringComparer.Ordinal)
        { "performed", "not_performed" };
    private static readonly HashSet<string> TxTypes = new(StringComparer.Ordinal)
        { "prolong", "order" };
    private static readonly HashSet<string> BonusStates = new(StringComparer.Ordinal)
        { "locked", "unlocked" };

    /// <summary>Validate model settings. Returns null when OK, else a reason.</summary>
    public static string? ModelSettings(ModelSettingsDto? dto)
    {
        if (dto is null) return "settings required";
        if (dto.HorizonDays < 1 || dto.HorizonDays > 3660)
            return "horizonDays out of range 1..3660";
        if (dto.Weights is null) return "weights required";
        if (dto.Thresholds is null) return "thresholds required";

        // unknown keys are rejected, not silently dropped
        foreach (var k in dto.Weights.Keys)
            if (!WeightKeys.Contains(k)) return $"unknown weight key '{k}'";
        foreach (var k in dto.Thresholds.Keys)
            if (!ThresholdKeys.Contains(k)) return $"unknown threshold key '{k}'";

        foreach (var (k, v) in dto.Weights)
            if (v < 0) return $"weight '{k}' is negative";
        // Effective sum = supplied weights plus baseline defaults for the keys
        // NOT supplied — this is what ToCoreSettings will actually use as the
        // normalization denominator. Validating only the supplied sum lets an
        // int.MaxValue on one key silently overflow the effective denominator.
        long total = 0;
        foreach (var (k, def) in BaselineWeights)
        {
            var v = dto.Weights.TryGetValue(k, out var sup) ? sup : def;
            total += v;
            if (total > int.MaxValue) return "effective weights sum overflow";
        }
        if (total == 0) return "weights sum is zero — an empty model is invalid_settings";

        // thresholds: 0..100 and medium must be strictly below high
        foreach (var (k, v) in dto.Thresholds)
            if (v < 0 || v > 100) return $"threshold '{k}' out of range 0..100";
        var high = dto.Thresholds.TryGetValue("high", out var h) ? h : 50;
        var med = dto.Thresholds.TryGetValue("medium", out var m) ? m : 20;
        if (med >= high) return "medium threshold must be below high";
        return null;
    }

    /// <summary>Validate a whole source snapshot — rejects the entire payload.</summary>
    public static string? Snapshot(SourceSnapshotDto? s)
    {
        if (s is null) return "snapshot required";
        if (s.Accounts is null || s.Services is null || s.Transactions is null)
            return "accounts/services/transactions must not be null";

        var accIds = new HashSet<long>();
        foreach (var a in s.Accounts)
        {
            if (a is null) return "null account element";
            if (!accIds.Add(a.Id)) return "duplicate account id";
            if (a.Balance < 0 || a.WithdrawBalance < 0 || a.TotalReplenished < 0 || a.BonusBalance < 0)
                return "negative money in account";
            if (!BonusStates.Contains(a.BonusState ?? "")) return "unknown bonusState";
        }
        var svcIds = new HashSet<long>();
        foreach (var v in s.Services)
        {
            if (v is null) return "null service element";
            if (!svcIds.Add(v.Id)) return "duplicate service id";
            if (!accIds.Contains(v.OwnerId)) return "service ownerId missing";
            if (!ServiceStatuses.Contains(v.Status ?? "")) return "unknown service status";
            if (!PaymentTerms.Contains(v.PaymentTerm ?? "")) return "unknown paymentTerm";
            if (v.Price < 0) return "negative price";
            if (v.ExpiresAt < v.CreatedAt) return "expiresAt < createdAt";
        }
        var txIds = new HashSet<long>();
        foreach (var t in s.Transactions)
        {
            if (t is null) return "null transaction element";
            if (!txIds.Add(t.Id)) return "duplicate transaction id";
            if (!svcIds.Contains(t.ServiceId)) return "transaction serviceId missing";
            if (!TxStatuses.Contains(t.Status ?? "")) return "unknown transaction status";
            if (!TxTypes.Contains(t.Type ?? "")) return "unknown transaction type";
            if (t.Amount < 0) return "negative amount";
            if (t.Status == "not_performed" && t.PerformedAt is not null) return "not_performed tx has performedAt";
            if (t.Status == "performed" && t.PerformedAt is null) return "performed tx missing performedAt";
        }
        return null;
    }
}
