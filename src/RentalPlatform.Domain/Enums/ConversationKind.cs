namespace RentalPlatform.Domain.Enums;

// Admin console Phase 1 (moderation messages): whether a Conversation is tied to a booking
// (the original ADR-001 shape) or is a booking-less thread between a moderator and a member.
// A Moderation conversation has no Booking, no toy strip, and never auto-closes (see
// ChatService.TryLazyCloseAsync). See Conversation's doc comment for the OwnerId/RenterId
// mapping a Moderation thread uses to reuse the existing participant/index shape unchanged.
public enum ConversationKind
{
    Booking = 0,
    Moderation = 1
}
