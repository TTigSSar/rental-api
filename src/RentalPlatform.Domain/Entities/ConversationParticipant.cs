namespace RentalPlatform.Domain.Entities;

/// <summary>
/// Per-participant state in a <see cref="Conversation"/> (one row per user, two per
/// thread). The read cursor powers the unread count and the "Seen" receipt: a message
/// is read by this participant when its CreatedAt is at or before <see cref="LastReadAt"/>.
/// </summary>
public sealed class ConversationParticipant
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Last message this participant has read. Null ⇒ nothing read yet.</summary>
    public Guid? LastReadMessageId { get; set; }

    /// <summary>Timestamp of the last read. Drives unread counts and the Seen indicator.</summary>
    public DateTime? LastReadAt { get; set; }

    public Conversation Conversation { get; set; } = null!;
    public User User { get; set; } = null!;
}
