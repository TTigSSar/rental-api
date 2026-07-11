using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Common;

// Maps the chat enums + booking state to the exact string tokens the Angular client
// renders. The conversation status pill is DERIVED (ADR-001): it is not stored, but
// computed from the linked booking's status + the conversation's ClosedAt. Kept here
// so the contract lives in one place.
//
// Derived pill progression: requested -> approved -> active -> return_due -> completed -> closed.
// "completed" means the booking is Completed but the conversation hasn't locked yet
// (ClosedAt is still null, e.g. both party reviews aren't in yet per M-010); "closed"
// is the terminal, read-only state and is produced ONLY by the ClosedAt override below.
public static class ChatTokens
{
    public static string MessageTypeToken(MessageType type) => type switch
    {
        MessageType.Text => "text",
        MessageType.Image => "image",
        MessageType.System => "system",
        _ => "text"
    };

    public static string? SystemKindToken(ChatSystemKind? kind) => kind switch
    {
        ChatSystemKind.Request => "request",
        ChatSystemKind.Approved => "approved",
        ChatSystemKind.Handover => "handover",
        ChatSystemKind.Return => "return",
        ChatSystemKind.Closed => "closed",
        _ => null
    };

    // Derived UI status pill: booking status + ClosedAt override.
    public static string StatusToken(BookingStatus bookingStatus, DateOnly endDate, DateTime? closedAt, DateTime utcNow)
    {
        if (closedAt is not null)
        {
            return "closed";
        }

        var today = DateOnly.FromDateTime(utcNow);
        return bookingStatus switch
        {
            BookingStatus.Pending => "requested",
            BookingStatus.Approved => "approved",
            BookingStatus.Active => endDate < today ? "return_due" : "active",
            BookingStatus.Completed => "completed",
            // Rejected / Cancelled / Expired have no dedicated pill in the design;
            // fall back to the "requested" token rather than inventing one.
            _ => "requested"
        };
    }
}
