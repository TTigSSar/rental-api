namespace RentalPlatform.Application.DTOs;

// Returned ONLY on CurrentUserResponse (self-view) — never on any other user's profile, listing, or
// public endpoint (home-point model). Latitude/Longitude are the exact point; PublicLatitude/PublicLongitude
// are the same geohash-cell-centroid pair every listing of this owner's publishes.
public sealed class HomePointResponse
{
    public decimal Latitude { get; init; }
    public decimal Longitude { get; init; }
    public decimal? PublicLatitude { get; init; }
    public decimal? PublicLongitude { get; init; }
    public ListingDistrictResponse? District { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
