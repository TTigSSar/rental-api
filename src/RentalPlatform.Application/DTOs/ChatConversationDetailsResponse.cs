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

    // Rental-card fields (RC-1). All null for a Moderation conversation / one without a booking.

    /// <summary>Booking-derived status token (<c>ChatTokens.StatusToken</c>), same vocabulary as <see cref="Status"/>.</summary>
    public string? BookingStatus { get; init; }

    /// <summary>"owner" | "renter" — the caller's role in the booking.</summary>
    public string? ViewerRole { get; init; }

    /// <summary>"owner" | "renter" | "none" — whose move it is (<c>ChatTokens.TurnToken</c>).</summary>
    public string? Turn { get; init; }

    /// <summary>UTC; the request expiry while the booking is Pending, otherwise null.</summary>
    public DateTime? DeadlineAt { get; init; }

    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }

    /// <summary>Days between pickup and return, minimum 1 (ADR-043).</summary>
    public int? RentalDays { get; init; }

    /// <summary>The owner's stored rejection reason (code or free text), if any.</summary>
    public string? RejectionReason { get; init; }

    // Rental-card fields (RC-2b). Null for a Moderation conversation / one without a booking.

    /// <summary>The booked listing's id (for a "show toy listing" link).</summary>
    public Guid? ListingId { get; init; }

    /// <summary>The renter's note sent with the booking request; null when none was given.</summary>
    public string? RenterNote { get; init; }

    public IReadOnlyCollection<ChatMessageResponse> Messages { get; init; } = Array.Empty<ChatMessageResponse>();
}
