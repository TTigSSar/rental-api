using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class MyListingResponse
{
    public Guid Id { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public PriceUnit PriceUnit { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public int? AgeFromMonths { get; init; }
    public int? AgeToMonths { get; init; }
    public string? Condition { get; init; }
    public string? HygieneNotes { get; init; }
    public string? SafetyNotes { get; init; }
    public decimal? DepositAmount { get; init; }
    public int? MinRentalDays { get; init; }
    public DeliveryType? DeliveryType { get; init; }
    public ListingStatus Status { get; init; }
    public string? RejectionReason { get; init; }
    public ListingRejectionResponse? Rejection { get; init; }
    public string? PrimaryImageUrl { get; init; }
    public DateTime? ModeratedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
