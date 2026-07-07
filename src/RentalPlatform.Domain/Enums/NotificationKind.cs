namespace RentalPlatform.Domain.Enums;

// The ten notification event kinds. Each drives an icon + colour + label on the
// client card. Persisted as int; mapped to the frontend string token
// (e.g. ListingLive -> "listing_live") by the API serializer.
public enum NotificationKind
{
    Request = 0,
    Approved = 1,
    Declined = 2,
    ListingLive = 3,
    ListingChanges = 4,
    Pickup = 5,
    Confirm = 6,
    Return = 7,
    Review = 8,
    Message = 9
}
