namespace RentalPlatform.Domain.Enums;

// What kind of entity a ModerationLogEntry is about. Persisted as int — explicit values are
// load-bearing, same rule as ModerationAction.
public enum ModerationTargetType
{
    Listing = 0,
    Category = 1,
    User = 2,
    Report = 3
}
