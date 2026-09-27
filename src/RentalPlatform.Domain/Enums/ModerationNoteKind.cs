namespace RentalPlatform.Domain.Enums;

// The kind of an auto-posted moderation note (ChatMessage.Type == MessageType.ModerationNote),
// emitted by IModerationNoteEmitter into a member's Moderation conversation when an admin takes
// one of the three actions below. Persisted as int — explicit values are load-bearing (never
// reuse a retired value; see BookingStatus value 6 for the precedent).
public enum ModerationNoteKind
{
    Reject = 0,    // a listing was rejected
    Warn = 1,      // reserved for a future warn action
    Suspend = 2,   // the member's account was suspended
    Category = 3,  // a listing was recategorised
    Info = 4       // reserved: generic informational note
}
