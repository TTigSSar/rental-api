using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Common;

// Maps the persisted enums to the exact string tokens the Angular client expects
// (src/app/features/notifications/models/notification.model.ts) and parses the
// feed-filter query value. Kept here so the contract lives in one place.
public static class NotificationTokens
{
    public static string KindToken(NotificationKind kind) => kind switch
    {
        NotificationKind.Request => "request",
        NotificationKind.Approved => "approved",
        NotificationKind.Declined => "declined",
        NotificationKind.ListingLive => "listing_live",
        NotificationKind.ListingChanges => "listing_changes",
        NotificationKind.Pickup => "pickup",
        NotificationKind.Confirm => "confirm",
        NotificationKind.Return => "return",
        NotificationKind.Review => "review",
        NotificationKind.Message => "message",
        _ => "message"
    };

    public static string CategoryToken(NotificationCategory category) => category switch
    {
        NotificationCategory.Booking => "booking",
        NotificationCategory.Listing => "listing",
        NotificationCategory.Reminder => "reminder",
        NotificationCategory.Review => "review",
        NotificationCategory.Message => "message",
        _ => "booking"
    };

    public static bool TryParseFilter(string? value, out Abstractions.NotificationFeedFilter filter)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null or "" or "all":
                filter = Abstractions.NotificationFeedFilter.All;
                return true;
            case "unread":
                filter = Abstractions.NotificationFeedFilter.Unread;
                return true;
            case "action":
                filter = Abstractions.NotificationFeedFilter.Action;
                return true;
            default:
                filter = Abstractions.NotificationFeedFilter.All;
                return false;
        }
    }
}
