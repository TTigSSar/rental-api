namespace RentalPlatform.Application.DTOs;

// The district a listing's exact point resolved to (or that the owner explicitly chose) — every
// caller sees this, including anonymous ones. Unlike Latitude/Longitude, a district name is not
// sensitive on its own (it does not pinpoint a home), so it is never gated by CanSeeExactCoordinates.
public sealed class ListingDistrictResponse
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string NameEn { get; init; } = string.Empty;
    public string NameHy { get; init; } = string.Empty;
    public string NameRu { get; init; } = string.Empty;
}
