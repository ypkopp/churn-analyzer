namespace Churn.Core;

/// <summary>
/// One-shot distribution of an account's shared available fund across due
/// renewals. Single source of truth for BOTH the risk feature
/// "insufficient_funds" and the money engine's "uncoveredAmount" — so the two
/// models never disagree about how the shared balance is spent.
///
/// Fund = balance + (bonusBalance if bonusState == Unlocked).
/// withdrawBalance is NOT auto-available; totalReplenished is a historical
/// counter, not current funds. No float/double: integer EUR cents throughout.
/// </summary>
public static class BalanceFund
{
    /// <summary>Available fund for an account under the contract rules.</summary>
    public static long Available(Account a) =>
        checked(a.Balance + (a.BonusState == BonusState.Unlocked ? a.BonusBalance : 0));

    /// <summary>
    /// Spends the fund once over due renewals ordered by ExpiresAt (earliest
    /// first); ties broken by ServiceId for a deterministic order.
    /// Returns the uncovered remainder (>= 0). Never throws on insufficient funds.
    /// </summary>
    public static long UncoveredAfterSpending(IEnumerable<Service> due, Account account)
    {
        long remaining = Available(account);
        long uncovered = 0;
        foreach (var s in due.OrderBy(s => s.ExpiresAt).ThenBy(s => s.Id))
        {
            if (remaining >= s.Price) remaining -= s.Price;
            else uncovered = checked(uncovered + s.Price);
        }
        return uncovered;
    }
}
