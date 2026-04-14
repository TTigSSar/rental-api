using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class AdminPendingListingResponse
{
    public Guid Id { get; init; }
    public Guid OwnerId { get; init; }
    public string OwnerEmail { get; init; } = string.Empty;
    public Guid CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string City { get; init; } = string.Empty;
    public string Country { get; init; } = string.Empty;
    public ListingStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
