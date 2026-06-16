using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain;

// Single source of truth for valid booking state transitions.
// Rules:
//   Pending      → Approved | Rejected | Expired | Cancelled
//   Approved     → ReturnMarked | Cancelled
//   ReturnMarked → Completed | Approved (undo)
//   Rejected, Cancelled, Expired, Completed → (terminal — no further transitions)
//
// Completion is a two-sided handshake: the first party marks the toy returned
// (Approved → ReturnMarked), and the other party confirms (ReturnMarked → Completed).
// The initiator may undo (ReturnMarked → Approved) until the other party confirms.
public static class BookingStatusTransitions
{
    public static bool CanTransition(BookingStatus from, BookingStatus to) => (from, to) switch
    {
        (BookingStatus.Pending, BookingStatus.Approved) => true,
        (BookingStatus.Pending, BookingStatus.Rejected) => true,
        (BookingStatus.Pending, BookingStatus.Expired) => true,
        (BookingStatus.Pending, BookingStatus.Cancelled) => true,
        (BookingStatus.Approved, BookingStatus.Cancelled) => true,
        (BookingStatus.Approved, BookingStatus.ReturnMarked) => true,
        (BookingStatus.ReturnMarked, BookingStatus.Completed) => true,
        (BookingStatus.ReturnMarked, BookingStatus.Approved) => true,
        _ => false
    };
}
