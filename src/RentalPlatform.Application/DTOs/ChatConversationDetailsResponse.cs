namespace RentalPlatform.Application.DTOs;

/// <summary>Full conversation view: header context + a page of messages.</summary>
public sealed class ChatConversationDetailsResponse
{
    public Guid Id { get; init; }
    public Guid BookingId { get; init; }
    public string CounterpartName { get; init; } = string.Empty;
    public string? CounterpartAvatarUrl { get; init; }
    public bool CounterpartVerified { get; init; }
    public string ToyTitle { get; init; } = string.Empty;
    public string? ToyImageUrl { get; init; }

    /// <summary>Derived status pill token (see <c>ChatTokens.StatusToken</c>).</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Formatted booking range, e.g. "2026-07-10 – 2026-07-14".</summary>
    public string BookingDates { get; init; } = string.Empty;

    public decimal BookingPrice { get; init; }
    public bool IsClosed { get; init; }

    public IReadOnlyCollection<ChatMessageResponse> Messages { get; init; } = Array.Empty<ChatMessageResponse>();
}
