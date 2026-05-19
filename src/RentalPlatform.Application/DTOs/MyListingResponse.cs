using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class MyListingResponse
{
    public Guid Id { get; init; }
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public ListingStatus Status { get; init; }
    public string? RejectionReason { get; init; }
    public string? PrimaryImageUrl { get; init; }
    public DateTime? ModeratedAt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
