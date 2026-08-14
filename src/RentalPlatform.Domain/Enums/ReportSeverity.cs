namespace RentalPlatform.Domain.Enums;

// Urgency of a community-submitted report. Derived server-side from the reason code
// (see ReportReasonCatalog) — never client-supplied. Persisted as int — explicit values are
// load-bearing, same rule as ModerationAction/ModerationTargetType.
public enum ReportSeverity
{
    Low = 0,
    Medium = 1,
    High = 2
}
