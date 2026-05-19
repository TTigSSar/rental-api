using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain;

// Single source of truth for valid booking state transitions.
// Rules:
//   Pending  → Approved | Rejected | Expired | Cancelled
//   Approved → Cancelled | Completed
//   Rejected, Cancelled, Expired, Completed → (terminal — no further transitions)
public static class BookingStatusTransitions
{
    public static bool CanTransition(BookingStatus from, BookingStatus to) => (from, to) switch
    {
        (BookingStatus.Pending, BookingStatus.Approved) => true,
        (BookingStatus.Pending, BookingStatus.Rejected) => true,
        (BookingStatus.Pending, BookingStatus.Expired) => true,
        (BookingStatus.Pending, BookingStatus.Cancelled) => true,
        (BookingStatus.Approved, BookingStatus.Cancelled) => true,
        (BookingStatus.Approved, BookingStatus.Completed) => true,
        _ => false
    };
}
