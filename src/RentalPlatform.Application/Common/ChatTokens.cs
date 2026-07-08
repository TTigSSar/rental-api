using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.Common;

// Maps the chat enums + booking state to the exact string tokens the Angular client
// renders. The conversation status pill is DERIVED (ADR-001): it is not stored, but
// computed from the linked booking's status + the conversation's ClosedAt. Kept here
// so the contract lives in one place.
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
            BookingStatus.Completed => "closed",
            // Rejected / Cancelled / Expired have no dedicated pill in the design;
            // fall back to the "requested" token rather than inventing one.
            _ => "requested"
        };
    }
}
