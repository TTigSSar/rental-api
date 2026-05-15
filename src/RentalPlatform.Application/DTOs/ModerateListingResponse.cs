using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

public sealed class ModerateListingResponse
{
    public Guid Id { get; init; }
    public ListingStatus Status { get; init; }
    public string? RejectionReason { get; init; }
    public DateTime ModeratedAt { get; init; }
    public string Message { get; init; } = string.Empty;
}
