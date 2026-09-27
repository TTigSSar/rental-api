namespace RentalPlatform.Application.DTOs;

/// <summary>Full conversation view: header context + a page of messages.</summary>
public sealed class ChatConversationDetailsResponse
{
    public Guid Id { get; init; }

    /// <summary>"booking" | "moderation" (see <c>ChatTokens.ConversationKindToken</c>).</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>Null for a Moderation conversation (<see cref="Kind"/> == "moderation").</summary>
    public Guid? BookingId { get; init; }

    /// <summary>Id of the "other" participant (owner if viewer is renter, renter if viewer is owner).</summary>
    public Guid CounterpartId { get; init; }

    public string CounterpartName { get; init; } = string.Empty;
    public string? CounterpartAvatarUrl { get; init; }
    public bool CounterpartVerified { get; init; }

    /// <summary>Null for a Moderation conversation — there is no toy strip.</summary>
    public string? ToyTitle { get; init; }
    public string? ToyImageUrl { get; init; }

    /// <summary>
    /// Derived status pill token (see <c>ChatTokens.StatusToken</c>), or
    /// <c>ChatTokens.ModerationStatusToken</c> ("moderation") for a Moderation conversation.
    /// </summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Formatted booking range, e.g. "2026-07-10 – 2026-07-14". Null for a Moderation conversation.</summary>
    public string? BookingDates { get; init; }

    /// <summary>Null for a Moderation conversation.</summary>
    public decimal? BookingPrice { get; init; }

    public bool IsClosed { get; init; }

    public IReadOnlyCollection<ChatMessageResponse> Messages { get; init; } = Array.Empty<ChatMessageResponse>();
}
