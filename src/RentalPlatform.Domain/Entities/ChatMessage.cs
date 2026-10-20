using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

/// <summary>
/// One message in a <see cref="Conversation"/>. A user text or image bubble, a System line
/// emitted by a booking event, or a ModerationNote auto-posted by an admin action. System
/// messages have a null <see cref="SenderId"/> and a non-null <see cref="SystemKind"/>.
/// ModerationNote messages have a non-null <see cref="SenderId"/> (the acting moderator, so the
/// client can render "by {name}") and a non-null <see cref="NoteKind"/>.
/// </summary>
public sealed class ChatMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>Author of the message. Null for System messages.</summary>
    public Guid? SenderId { get; set; }

    /// <summary>
    /// The user whose action caused this System line (RC-5, ADR-001). Null for a system/timer
    /// transition and for lines written before RC-5 — those count as unread for both parties.
    /// A line never counts as unread for its actor.
    /// </summary>
    public Guid? ActorUserId { get; set; }

    public MessageType Type { get; set; }

    /// <summary>Text body, or the caption of an image message. Null for a bare image.</summary>
    public string? Body { get; set; }

    /// <summary>Public URL of the attached image (Type == Image).</summary>
    public string? AttachmentUrl { get; set; }

    /// <summary>Which booking event this System line announces (Type == System).</summary>
    public ChatSystemKind? SystemKind { get; set; }

    /// <summary>Which admin action this note announces (Type == ModerationNote).</summary>
    public ModerationNoteKind? NoteKind { get; set; }

    /// <summary>Subject of the note, e.g. the listing title (Type == ModerationNote). Max 200 chars.</summary>
    public string? NoteSubject { get; set; }

    /// <summary>Reason of the note, e.g. the rejection reason label (Type == ModerationNote). Max 200 chars.</summary>
    public string? NoteReason { get; set; }

    public DateTime CreatedAt { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public User? Sender { get; set; }
}
