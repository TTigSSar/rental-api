namespace RentalPlatform.Application.DTOs;

public sealed class PendingListingForReviewResponse
{
    public Guid Id { get; init; }

    public Guid OwnerId { get; init; }
    public string OwnerEmail { get; init; } = string.Empty;
    public string OwnerFirstName { get; init; } = string.Empty;
    public string OwnerLastName { get; init; } = string.Empty;

    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string? AddressLine { get; init; }

    public int? AgeFromMonths { get; init; }
    public int? AgeToMonths { get; init; }
    public string? Condition { get; init; }
    public string? HygieneNotes { get; init; }
    public string? SafetyNotes { get; init; }
    public decimal? DepositAmount { get; init; }

    public IReadOnlyCollection<ListingImageResponse> Images { get; init; } = Array.Empty<ListingImageResponse>();

    public DateTime CreatedAt { get; init; }
}
