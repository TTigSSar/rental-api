namespace RentalPlatform.Application.DTOs;

// Envelope for GET /api/listings/map-pins. IsTruncated tells the frontend (Maps P2-2) when the
// result was cut off at the pin cap so it can prompt "zoom in to see all", rather than silently
// implying the map shows every matching listing.
public sealed class ListingMapPinsResponse
{
    public IReadOnlyCollection<ListingMapPinResponse> Items { get; init; } = Array.Empty<ListingMapPinResponse>();
    public bool IsTruncated { get; init; }
}
