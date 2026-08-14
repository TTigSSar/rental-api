namespace RentalPlatform.Domain.Enums;

// Triage state of a Report. Persisted as int — explicit values are load-bearing, same rule as
// ModerationAction/ModerationTargetType.
public enum ReportStatus
{
    Open = 0,
    Resolved = 1,
    Dismissed = 2
}
