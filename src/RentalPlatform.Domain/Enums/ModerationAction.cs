namespace RentalPlatform.Domain.Enums;

// Every action recorded in the moderation audit trail (ModerationLogEntry). Persisted as int —
// explicit values are load-bearing (house rule: never reuse a retired value; see BookingStatus
// value 6 for the precedent). Phase 1 (AdminListingsService) only ever writes ListingApproved,
// ListingRejected, and ListingRecategorised; the rest are reserved for later phases (category
// management, user moderation, report handling) so the enum doesn't need a breaking change when
// those land.
public enum ModerationAction
{
    ListingApproved = 0,
    ListingRejected = 1,
    ListingRecategorised = 2,
    CategoryCreated = 3,
    CategoryRenamed = 4,
    CategoryReordered = 5,
    CategoryVisibilityChanged = 6,
    CategoryDeleted = 7,
    UserVerified = 8,
    UserSuspended = 9,
    UserReactivated = 10,
    ReportResolved = 11,
    ReportDismissed = 12,
    ReportReopened = 13
}
