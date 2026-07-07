namespace RentalPlatform.Domain.Enums;

// The booking-lifecycle event a System message announces inside a conversation.
// Mirrors the design's SYS_META set; emitted by booking events, never typed by a user.
public enum ChatSystemKind
{
    Request = 0,   // booking requested
    Approved = 1,  // owner approved the request
    Handover = 2,  // toy handed over / receipt confirmed (Approved → Active)
    Return = 3,    // return due
    Closed = 4     // rental complete + both reviews in → conversation read-only
}
