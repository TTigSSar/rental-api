namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Viewer-neutral broadcast payload for a chat message pushed over realtime transport.
/// Unlike <see cref="ChatMessageResponse"/>, this carries no per-viewer fields
/// (no IsMine/Seen) — both participants receive the exact same payload and the
/// frontend derives isMine/seen locally from its own user id and read cursor.
/// </summary>
public sealed class ChatRealtimeMessage
{
    public Guid Id { get; init; }
    public Guid ConversationId { get; init; }

    /// <summary>Author id. Null for System messages.</summary>
    public Guid? SenderId { get; init; }

    /// <summary>Author display name. Null for System messages.</summary>
    public string? SenderName { get; init; }

    /// <summary>"text" | "image" | "system".</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Booking-event token for System lines (see <c>ChatTokens.SystemKindToken</c>).</summary>
    public string? SystemKind { get; init; }

    public string? Body { get; init; }
    public string? AttachmentUrl { get; init; }
    public DateTime SentAt { get; init; }
}
