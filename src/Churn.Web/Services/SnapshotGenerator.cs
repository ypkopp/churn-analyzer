namespace Churn.Web.Services;

using Churn.Web.Data;

/// <summary>
/// Deterministic synthetic snapshot generator — the same contract as
/// data/raw/generate.py (seeded RNG, calendar paymentTerm arithmetic via
/// DateTimeOffset.AddMonths which clamps to the last day of the month).
/// Produces a SourceSnapshotDto; the caller routes it through the normal
/// PUT /source/snapshot path so validation + atomic replace stay the only
/// way data enters the store. Used by POST /api/v1/source/generate for the
/// "generate demo snapshot" button — Admin only.
/// </summary>
public static class SnapshotGenerator
{
    private static readonly string[] Terms =
        { "hour", "week", "month", "quarter_year", "half_year", "year" };
    private static readonly string[] States =
        { "active", "blocked", "deleted", "suspended", "prolong_wait" };

    /// <summary>Generate a deterministic snapshot. Same seed → same data.</summary>
    public static SourceSnapshotDto Generate(int accounts, int services, int transactions,
        int seed, DateTimeOffset reference)
    {
        if (accounts < 1 || services < 1 || transactions < 0)
            throw new ArgumentOutOfRangeException(nameof(accounts),
                "accounts/services >= 1, transactions >= 0");
        var rng = new Random(seed);

        var accs = new List<AccountDto>(accounts);
        for (int i = 1; i <= accounts; i++)
        {
            long balance = rng.Next(0, 100_001);
            accs.Add(new AccountDto(i, $"user-{i:04d}@example.test", balance,
                rng.NextInt64(0, balance + 1),
                balance + rng.NextInt64(0, 500_001),
                rng.NextInt64(0, 10_001),
                (i - 1) % 2 == 0 ? "locked" : "unlocked"));
        }

        var svcs = new List<ServiceDto>(services);
        for (int i = 1; i <= services; i++)
        {
            var term = Terms[(i - 1) % Terms.Length];
            var status = States[(i - 1) % States.Length];
            var created = status switch
            {
                "active" => reference.AddMinutes(-30),
                "deleted" or "prolong_wait" => reference.AddDays(-400),
                _ => reference.AddDays(-rng.Next(1, 91)),
            };
            svcs.Add(new ServiceDto(i, $"service-{i:04d}", (i - 1) % accounts + 1,
                term, rng.Next(2) == 1, created, AddTerm(created, term),
                status, rng.NextInt64(1, 1001)));
        }

        var txs = new List<TransactionDto>(transactions);
        for (int i = 1; i <= transactions; i++)
        {
            var s = svcs[(i - 1) % svcs.Count];
            var created = s.CreatedAt.AddMinutes(rng.Next(0, 6));
            var performed = i % 3 != 0;
            var amount = i == 1 ? 0 : i == 2 ? 1000 : rng.Next(0, 1001);
            txs.Add(new TransactionDto(i, amount, performed ? "performed" : "not_performed",
                performed ? created.AddSeconds(rng.Next(1, 61)) : null,
                created, i <= services ? "order" : "prolong", s.Id));
        }
        return new SourceSnapshotDto(accs, svcs, txs);
    }

    // Same calendar-month rule as the Python generator: AddMonths clamps to the
    // last day of the target month — no synthetic 30-day reinterpretation.
    private static DateTimeOffset AddTerm(DateTimeOffset v, string term) => term switch
    {
        "hour" => v.AddHours(1),
        "week" => v.AddDays(7),
        "month" => v.AddMonths(1),
        "quarter_year" => v.AddMonths(3),
        "half_year" => v.AddMonths(6),
        "year" => v.AddMonths(12),
        _ => v.AddMonths(1),
    };
}
