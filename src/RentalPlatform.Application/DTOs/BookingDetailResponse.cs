using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

/// <summary>
/// Full booking detail for the dedicated Booking Details page. Returned to either party
/// (renter or owner). Counterparty phone is gated to bookings that are at least Approved.
/// </summary>
public sealed class BookingDetailResponse
{
    public Guid Id { get; init; }
    public BookingStatus Status { get; init; }

    /// <summary>"renter" or "owner" — which side of the booking the caller is.</summary>
    public string Role { get; init; } = "none";

    // Toy / listing summary
    public Guid ListingId { get; init; }
    public string ListingTitle { get; init; } = string.Empty;
    public string? ListingPrimaryImageUrl { get; init; }
    public string? CategoryName { get; init; }
    public string? Condition { get; init; }
    public string City { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;

    /// <summary>Street address — only populated once the booking is at least Approved.</summary>
    public string? AddressLine { get; init; }

    // Pricing / dates
    public string Currency { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public decimal? DepositAmount { get; init; }
    public decimal TotalPrice { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }

    // Lifecycle timestamps (null until the milestone is reached) — drive the booking timeline.
    public DateTime CreatedAt { get; init; }
    public DateTime? ApprovedAt { get; init; }
    public DateTime? ReturnMarkedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public DateTime ExpiresAt { get; init; }

    // Owner's reason when the request was rejected (known reason code or free text). Null otherwise.
    public string? RejectionReason { get; init; }

    // Completion handshake
    public BookingParty? ReturnInitiatedBy { get; init; }
    public CompletionMethod? CompletedVia { get; init; }

    // Counterparty (the other side of the booking). Phone gated to Approved+.
    public Guid CounterpartyId { get; init; }
    public string CounterpartyFirstName { get; init; } = string.Empty;
    public string CounterpartyLastName { get; init; } = string.Empty;
    public string? CounterpartyAvatarUrl { get; init; }
    public string? CounterpartyPhoneNumber { get; init; }
}
