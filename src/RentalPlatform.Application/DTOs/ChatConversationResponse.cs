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
    public int UnreadCount { get; init; }
}
