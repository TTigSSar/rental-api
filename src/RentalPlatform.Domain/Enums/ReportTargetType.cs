namespace RentalPlatform.Domain.Enums;

// What kind of entity a Report is filed against. Persisted as int — explicit values are
// load-bearing, same rule as ModerationAction/ModerationTargetType.
public enum ReportTargetType
{
    Listing = 0,
    User = 1,
    Message = 2
}
