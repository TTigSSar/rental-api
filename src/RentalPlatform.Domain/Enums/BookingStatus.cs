namespace RentalPlatform.Domain.Enums;

public enum BookingStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
    Expired = 4,
    Completed = 5,

    // Owner has handed over the toy (Approved → Active). Owner can then complete the rental.
    // Value 6 (legacy ReturnMarked) is intentionally retired and left unused.
    Active = 7
}
