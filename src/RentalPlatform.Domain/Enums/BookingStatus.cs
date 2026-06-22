namespace RentalPlatform.Domain.Enums;

public enum BookingStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
    Expired = 4,
    Completed = 5,

    // Legacy — no longer reachable from new transitions. Kept so existing DB rows are not orphaned.
    ReturnMarked = 6,

    // Owner has handed over the toy (Approved → Active). Owner can then complete the rental.
    Active = 7
}
