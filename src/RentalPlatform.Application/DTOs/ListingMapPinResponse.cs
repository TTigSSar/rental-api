using RentalPlatform.Domain.Enums;

namespace RentalPlatform.Application.DTOs;

// Maps P2-1: minimal shape for a map pin popup — enough to render without a second request.
// Latitude/Longitude here are ALWAYS the public (fuzzed) pair (ADR-008). There is no
// owner/admin-privileged variant of this endpoint: a pin never carries the exact coordinate.
public sealed class ListingMapPinResponse
{
    public Guid Id { get; init; }
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public string Title { get; init; } = string.Empty;
    public decimal PricePerDay { get; init; }
    public PriceUnit PriceUnit { get; init; }
    public string Currency { get; init; } = string.Empty;
    public string? PrimaryImageUrl { get; init; }
}
