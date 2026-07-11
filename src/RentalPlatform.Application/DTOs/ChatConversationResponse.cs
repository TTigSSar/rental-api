namespace RentalPlatform.Application.DTOs;

/// <summary>One inbox row: a conversation preview for the current user's chat list.</summary>
public sealed class ChatConversationResponse
{
    public Guid Id { get; init; }
    public Guid BookingId { get; init; }
    public string CounterpartName { get; init; } = string.Empty;
    public string? CounterpartAvatarUrl { get; init; }
    public string ToyTitle { get; init; } = string.Empty;
    public string? ToyImageUrl { get; init; }

    /// <summary>Derived status pill token (see <c>ChatTokens.StatusToken</c>).</summary>
    public string Status { get; init; } = string.Empty;

    public string? LastMessageSnippet { get; init; }
    public DateTime? LastMessageAt { get; init; }

    /// <summary>
    /// "text" | "image" | "system" token for the conversation's last message (see
    /// <c>ChatTokens.MessageTypeToken</c>), or null when there is no last message yet. Lets the
    /// client render a localized placeholder (e.g. "Photo") when <c>LastMessageSnippet</c> is
    /// null for an image message — the server never bakes in a literal display string.
    /// </summary>
    public string? LastMessageType { get; init; }

    /// <summary>True when the conversation's last message was sent by the current user (false if none, or a system message).</summary>
    public bool LastMessageIsMine { get; init; }

    public int UnreadCount { get; init; }
}
