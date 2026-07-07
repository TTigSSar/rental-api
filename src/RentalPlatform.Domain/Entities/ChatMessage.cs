using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Domain.Entities;

/// <summary>
/// One message in a <see cref="Conversation"/>. A user text or image bubble, or a
/// System line emitted by a booking event. System messages have a null
/// <see cref="SenderId"/> and a non-null <see cref="SystemKind"/>.
/// </summary>
public sealed class ChatMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>Author of the message. Null for System messages.</summary>
    public Guid? SenderId { get; set; }

    public MessageType Type { get; set; }

    /// <summary>Text body, or the caption of an image message. Null for a bare image.</summary>
    public string? Body { get; set; }

    /// <summary>Public URL of the attached image (Type == Image).</summary>
    public string? AttachmentUrl { get; set; }

    /// <summary>Which booking event this System line announces (Type == System).</summary>
    public ChatSystemKind? SystemKind { get; set; }

    public DateTime CreatedAt { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public User? Sender { get; set; }
}
