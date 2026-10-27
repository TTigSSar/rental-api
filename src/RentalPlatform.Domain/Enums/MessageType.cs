namespace RentalPlatform.Domain.Enums;

// The kind of a chat message. Drives client rendering: Text/Image are user
// bubbles; System renders as a centered inline line (see ChatSystemKind);
// ModerationNote renders as an admin-authored note (see ModerationNoteKind) —
// unlike System, it has a non-null SenderId (the acting moderator).
// Location is an owner-shared meeting point (see MeetingPointKind) carrying exact coordinates.
public enum MessageType
{
    Text = 0,
    Image = 1,
    System = 2,
    ModerationNote = 3,
    Location = 4
}
