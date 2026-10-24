namespace Churn.Core;

/// <summary>Allowed service lifecycle statuses (from the source contract).</summary>
public enum ServiceStatus { Active, Suspended, ProlongWait, Blocked, Deleted }

public enum PaymentTerm { Hour, Week, Month, QuarterYear, HalfYear, Year }

public enum BonusState { Locked, Unlocked }

public enum TransactionStatus { NotPerformed, Performed }

public enum TransactionType { Order, Prolong }

/// <summary>Risk levels. NoAssessment = not enough data to judge (NOT "low").</summary>
public enum RiskLevel { NoAssessment, Low, Medium, High }
