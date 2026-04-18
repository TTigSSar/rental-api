namespace RentalPlatform.Application.DTOs;

public sealed class ListingDetailsResponse
{
    public Guid Id { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string AddressLine { get; init; } = string.Empty;
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
    public ListingCategoryResponse Category { get; init; } = new();
    public ListingOwnerResponse Owner { get; init; } = new();
    public IReadOnlyCollection<ListingImageResponse> Images { get; init; } = Array.Empty<ListingImageResponse>();
    public IReadOnlyCollection<ListingBookedDateRangeResponse> BookedDateRanges { get; init; } = Array.Empty<ListingBookedDateRangeResponse>();
}
