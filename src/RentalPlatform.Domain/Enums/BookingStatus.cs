namespace RentalPlatform.Domain.Enums;

public enum BookingStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
    Expired = 4,
    Completed = 5,

    // One party has marked the toy returned and is awaiting the other party's confirmation.
    // Sits between Approved and Completed in the two-sided completion handshake.
    ReturnMarked = 6
}
