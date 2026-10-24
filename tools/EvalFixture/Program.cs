// Official-fixture evaluation harness (read-only over data/raw/data.json).
// Prints ONLY aggregate distribution — no emails, no raw rows, no ids.
using System.Text.Json;
using Churn.Core;

var json = File.ReadAllText("data/raw/data.json");
using var doc = JsonDocument.Parse(json);
var root = doc.RootElement;

var accounts = root.GetProperty("accounts").EnumerateArray().Select(a => new Account(
    a.GetProperty("id").GetInt64(), a.GetProperty("email").GetString()!,
    a.GetProperty("balance").GetInt64(), a.GetProperty("withdrawBalance").GetInt64(),
    a.GetProperty("totalReplenished").GetInt64(), a.GetProperty("bonusBalance").GetInt64(),
    a.GetProperty("bonusState").GetString() == "unlocked" ? BonusState.Unlocked : BonusState.Locked)).ToList();

ServiceStatus MapStatus(string s) => s switch
{
    "active" => ServiceStatus.Active, "suspended" => ServiceStatus.Suspended,
    "prolong_wait" => ServiceStatus.ProlongWait, "blocked" => ServiceStatus.Blocked,
    "deleted" => ServiceStatus.Deleted, _ => throw new ArgumentException(s)
};
PaymentTerm MapTerm(string s) => s switch
{
    "hour" => PaymentTerm.Hour, "week" => PaymentTerm.Week, "month" => PaymentTerm.Month,
    "quarter_year" => PaymentTerm.QuarterYear, "half_year" => PaymentTerm.HalfYear,
    "year" => PaymentTerm.Year, _ => throw new ArgumentException(s)
};
static DateTimeOffset Ts(string s) => DateTimeOffset.Parse(s);

var services = root.GetProperty("services").EnumerateArray().Select(s => new Service(
    s.GetProperty("id").GetInt64(), s.GetProperty("name").GetString()!,
    s.GetProperty("ownerId").GetInt64(), MapTerm(s.GetProperty("paymentTerm").GetString()!),
    s.GetProperty("autoProlong").GetBoolean(), Ts(s.GetProperty("createdAt").GetString()!),
    Ts(s.GetProperty("expiresAt").GetString()!), MapStatus(s.GetProperty("status").GetString()!),
    s.GetProperty("price").GetInt64())).ToList();

// asOf = the dataset's fixed reference, per README/generator.
var asOf = new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
var ctx = new EvaluationContext(asOf, HorizonDays: 14, SnapshotVersion: 1, ModelVersion: 1);
var model = ModelSettings.Baseline();

var results = accounts.Select(a => RiskEngine.Evaluate(a, services, ctx, model)).ToList();
var money = accounts.Select(a => new { a, r = results.First(x => x.AccountId == a.Id) })
    .Select(x => MoneyEngine.Evaluate(x.a, services, x.r, ctx)).ToList();

var dist = results.GroupBy(r => r.Level).OrderBy(g => g.Key)
    .ToDictionary(g => g.Key.ToString(), g => g.Count());
var noAssess = results.Count(r => r.NoAssessment);
var reasonFreq = results.SelectMany(r => r.ReasonCodes)
    .GroupBy(c => c).OrderByDescending(g => g.Count())
    .ToDictionary(g => g.Key, g => g.Count());
var scored = results.Where(r => !r.NoAssessment).ToList();

var totalRenewal = money.Sum(m => m.RenewalCostInHorizon);
var totalFund = money.Sum(m => m.AvailableFund);
var totalUncovered = money.Sum(m => m.UncoveredAmount);
var totalAtRisk = money.Where(m => m.AtRiskAmount.HasValue).Sum(m => m.AtRiskAmount!.Value);
var moneyStates = money.GroupBy(m => m.State).OrderBy(g => g.Key)
    .ToDictionary(g => g.Key.ToString(), g => g.Count());
// reconciliation: per-line sum must equal reported renewal cost
bool reconciles = money.All(m => m.Lines.Sum(l => l.PriceCents) == m.RenewalCostInHorizon);

// --- A05R exact repro: one prolong_wait, price=500, balance=0, bonus locked ---
var rAccount = new Account(1, "x@x", 0, 0, 0, 0, BonusState.Locked);
var rSvc = new Service(1, "s1", 1, PaymentTerm.Month, false,
    asOf.AddDays(-30), asOf.AddDays(5), ServiceStatus.ProlongWait, 500);
var rCtx = new EvaluationContext(asOf, HorizonDays: 14, SnapshotVersion: 1, ModelVersion: 1);
var rRisk = RiskEngine.Evaluate(rAccount, new[] { rSvc }, rCtx, model);
var rMoney = MoneyEngine.Evaluate(rAccount, new[] { rSvc }, rRisk, rCtx);
Console.WriteLine("=== A05R exact example ===");
Console.WriteLine($"reasons        : {string.Join(",", rRisk.ReasonCodes)}");
Console.WriteLine($"contributions  : {string.Join(" | ", rRisk.Contributions.Select(c => c.Code + "=" + c.Weight))}");
Console.WriteLine($"raw            : {rRisk.Contributions.Sum(c => c.Weight)}");
Console.WriteLine($"denominator    : {model.MaxRaw}");
Console.WriteLine($"score          : {rRisk.Score}  level={rRisk.Level}");
Console.WriteLine($"renewalCost    : {rMoney.RenewalCostInHorizon}");
Console.WriteLine($"availableFund  : {rMoney.AvailableFund}");
Console.WriteLine($"uncovered      : {rMoney.UncoveredAmount}");
Console.WriteLine($"atRisk         : {rMoney.AtRiskAmount}");
Console.WriteLine();

Console.WriteLine("=== Official dataset evaluation (aggregate only) ===");
Console.WriteLine($"accounts evaluated : {results.Count}");
Console.WriteLine($"level distribution : {string.Join(", ", dist.Select(kv => kv.Key + "=" + kv.Value))}");
Console.WriteLine($"no_assessment      : {noAssess}");
Console.WriteLine($"reason frequency   : {string.Join(", ", reasonFreq.Select(kv => kv.Key + "=" + kv.Value))}");
Console.WriteLine($"score range        : {(scored.Count > 0 ? scored.Min(r => r.Score) + ".." + scored.Max(r => r.Score) : "n/a")}");
Console.WriteLine("--- money (EUR cents, scenario index) ---");
Console.WriteLine($"states             : {string.Join(", ", moneyStates.Select(kv => kv.Key + "=" + kv.Value))}");
Console.WriteLine($"renewalCost total  : {totalRenewal}");
Console.WriteLine($"availableFund total: {totalFund}");
Console.WriteLine($"uncovered total    : {totalUncovered}");
Console.WriteLine($"atRisk total       : {totalAtRisk}");
Console.WriteLine($"lines reconcile    : {reconciles}");
Console.WriteLine("NOTE: score = priority index, atRisk = heuristic scenario index; not probability/expected loss. No accuracy/AUC claims.");
