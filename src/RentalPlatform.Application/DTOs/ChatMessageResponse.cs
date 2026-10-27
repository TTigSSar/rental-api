namespace RentalPlatform.Application.DTOs;

/// <summary>A single message bubble in a conversation thread.</summary>
public sealed class ChatMessageResponse
{
    public Guid Id { get; init; }
    public Guid ConversationId { get; init; }

    /// <summary>Author id. Null for System messages.</summary>
    public Guid? SenderId { get; init; }

    /// <summary>Author display name. Null for System messages.</summary>
    public string? SenderName { get; init; }

    /// <summary>"text" | "image" | "system" | "moderationNote" | "location".</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Booking-event token for System lines (see <c>ChatTokens.SystemKindToken</c>).</summary>
    public string? SystemKind { get; init; }

    /// <summary>Admin-action token for ModerationNote lines (see <c>ChatTokens.ModerationNoteKindToken</c>).</summary>
    public string? NoteKind { get; init; }

    /// <summary>Subject of a ModerationNote (e.g. the listing title). Null otherwise.</summary>
    public string? NoteSubject { get; init; }

    /// <summary>Reason of a ModerationNote (e.g. the rejection reason label). Null otherwise.</summary>
    public string? NoteReason { get; init; }

    /// <summary>Exact latitude of a shared meeting point. Only for Type == "location".</summary>
    public decimal? Latitude { get; init; }

    /// <summary>Exact longitude of a shared meeting point. Only for Type == "location".</summary>
    public decimal? Longitude { get; init; }

    /// <summary>"home" | "other". Only for Type == "location".</summary>
    public string? MeetingPointKind { get; init; }

    public string? Body { get; init; }
    public string? AttachmentUrl { get; init; }
    public DateTime SentAt { get; init; }

    /// <summary>True when the current user authored this message.</summary>
    public bool IsMine { get; init; }

    /// <summary>True when the counterpart has read this (own) message — powers the "Seen" receipt.</summary>
    public bool Seen { get; init; }
}
