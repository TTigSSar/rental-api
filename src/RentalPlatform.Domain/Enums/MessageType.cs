namespace RentalPlatform.Domain.Enums;

// The kind of a chat message. Drives client rendering: Text/Image are user
// bubbles; System renders as a centered inline line (see ChatSystemKind).
public enum MessageType
{
    Text = 0,
    Image = 1,
    System = 2
}
