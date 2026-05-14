using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class CreateListingResponse
{
    public Guid Id { get; init; }
    public ListingStatus Status { get; init; }
    public DateTime CreatedAt { get; init; }
    public string Message { get; init; } = string.Empty;
}
